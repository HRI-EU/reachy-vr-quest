"""Teleop controller (pure, testable) and the async link to the Reachy daemon."""

from __future__ import annotations

import asyncio
import json
import logging
import math
import time
from dataclasses import dataclass, field

import httpx
import numpy as np
import websockets
from websockets.asyncio.client import connect as ws_connect

from . import pose_math
from .safety_limits import (
    BODY_YAW_LIMIT_DEG,
    HEAD_BODY_YAW_DELTA_LIMIT_DEG,
    HEAD_PITCH_ROLL_LIMIT_DEG,
    enforce,
)

log = logging.getLogger("phone_teleop")

# Stay just inside +-180 so enforce()'s wrapped euler extraction never flips sign.
HEAD_YAW_COMMAND_LIMIT_DEG = 179.0
# Head tilt (angle between head up and world up) beyond which roll/yaw are held.
MAX_TILT_DEG = 75.0


@dataclass
class TeleopSettings:
    send_rate_hz: float = 50.0
    # No fresh phone sample for this long -> stop moving (deadman).
    deadman_timeout_s: float = 0.3
    # Fraction of the remaining error applied per tick (1.0 = no smoothing).
    smoothing: float = 0.5
    # Hard angular speed cap per axis for the commanded head pose.
    max_speed_deg_s: float = 180.0
    # Body starts turning once head-vs-body yaw exceeds this (must stay < 65 deg).
    body_follow_deg: float = 55.0
    body_follow: bool = True
    # Right after engaging the robot may be far from the phone's target (you turned
    # while released, or it was asleep): approach slowly until caught up.
    catchup_speed_deg_s: float = 60.0
    # Head translation is ramped to the neutral (0,0,0) at this speed after sync.
    max_linear_speed_mm_s: float = 40.0

    def __post_init__(self) -> None:
        if not 0.0 < self.smoothing <= 1.0:
            raise ValueError("smoothing must be in (0, 1]")
        if min(self.send_rate_hz, self.max_speed_deg_s, self.deadman_timeout_s,
               self.catchup_speed_deg_s, self.max_linear_speed_mm_s) <= 0:
            raise ValueError("rates, speeds and timeouts must be positive")
        if not 0.0 <= self.body_follow_deg < HEAD_BODY_YAW_DELTA_LIMIT_DEG:
            raise ValueError(f"body_follow_deg must be in [0, {HEAD_BODY_YAW_DELTA_LIMIT_DEG})")


@dataclass
class ControllerStatus:
    calibrated: bool = False
    engaged: bool = False
    synced: bool = False
    limited: bool = False
    catching_up: bool = False
    blocked: str = ""
    head_rpy_deg: tuple[float, float, float] = (0.0, 0.0, 0.0)
    body_yaw_deg: float = 0.0


class TeleopController:
    """Turns phone orientation samples into safe FullBodyTarget dicts.

    The commanded pose (cmd) only moves while engaged (deadman held, fresh
    samples, calibrated, and synced to the robot's present pose), and then at
    most max_speed_deg_s towards the phone's target. Every output goes through
    safety_limits.enforce().
    """

    def __init__(self, settings: TeleopSettings | None = None):
        self.settings = settings or TeleopSettings()
        self.calibration: pose_math.Calibration | None = None
        self.latest: np.ndarray | None = None
        self.latest_rx: float = -math.inf
        self.held = False
        self.cmd = np.zeros(3)  # roll, pitch, yaw (rad); yaw unwrapped
        self.body = 0.0  # rad
        self.pos = np.zeros(3)  # head translation (m), ramped to 0 after sync
        self.synced = False
        self.caught_up = False
        self.limited = False
        self._stashed_calibration: pose_math.Calibration | None = None
        self.blocked = ""  # set by control_loop: why the robot can't be commanded now

    # --- inputs -----------------------------------------------------------
    def on_sample(self, alpha: float, beta: float, gamma: float, held: bool, now: float) -> None:
        values = (float(alpha), float(beta), float(gamma))
        if not all(math.isfinite(v) for v in values):
            raise ValueError("non-finite orientation sample")
        self.latest = pose_math.device_rotation(*values)
        self.latest_rx = now
        self.held = bool(held)

    def release(self) -> None:
        self.held = False

    def new_session(self) -> None:
        """A new phone connection: iOS alpha has a per-page-load reference, so recalibrate.

        The old calibration is stashed: resume_session() restores it when the same page
        (same alpha reference) reconnects, e.g. after a Wi-Fi blip.
        """
        self.release()
        self._stashed_calibration = self.calibration
        self.calibration = None
        self.latest = None
        self.latest_rx = -math.inf
        self.synced = False

    def resume_session(self) -> bool:
        stashed = self._stashed_calibration
        self._stashed_calibration = None
        if stashed is None or self.calibration is not None:
            return False
        self.calibration = stashed
        return True

    def calibrate(self) -> None:
        if self.latest is None:
            raise pose_math.CalibrationError("No orientation data yet.")
        self.calibration = pose_math.Calibration(self.latest)
        self._stashed_calibration = None

    def sync_from_robot(self, roll: float, pitch: float, yaw: float, body_yaw: float,
                        position: tuple[float, float, float] = (0.0, 0.0, 0.0)) -> None:
        """Start the command from where the robot actually is (rotation AND translation)."""
        values = (roll, pitch, yaw, body_yaw, *position)
        if not all(math.isfinite(float(v)) for v in values):
            raise ValueError("non-finite robot pose")
        self.cmd = np.array([roll, pitch, yaw], dtype=float)
        self.body = float(body_yaw)
        self.pos = np.array(position, dtype=float)
        self.synced = True
        self.caught_up = False

    def desync(self) -> None:
        """Stop commanding; the next engage starts again from the robot's present pose."""
        self.synced = False

    # --- state ------------------------------------------------------------
    def is_engaged(self, now: float) -> bool:
        return (
            self.held
            and self.calibration is not None
            and self.latest is not None
            and now - self.latest_rx <= self.settings.deadman_timeout_s
        )

    def target_rpy(self) -> np.ndarray:
        """Phone target (roll, pitch, yaw) with yaw unwrapped next to cmd and pre-clamped."""
        assert self.calibration is not None and self.latest is not None
        head = self.calibration.head_rotation(self.latest)
        pr = math.radians(HEAD_PITCH_ROLL_LIMIT_DEG)
        yl = math.radians(HEAD_YAW_COMMAND_LIMIT_DEG)
        if head[2, 2] < math.cos(math.radians(MAX_TILT_DEG)):
            # Tilted ~vertical or beyond: the xyz euler split flips roll/yaw by 180 deg
            # here, so hold roll/yaw and saturate pitch towards where the view points.
            forward_z = head[2, 0]
            pitch = -math.copysign(pr, forward_z) if abs(forward_z) > 0.5 else self.cmd[1]
            return np.array([self.cmd[0], np.clip(pitch, -pr, pr), self.cmd[2]])
        roll, pitch, yaw = pose_math.matrix_to_rpy(head)
        yaw = pose_math.unwrap_angle(yaw, self.cmd[2])
        return np.array([
            np.clip(roll, -pr, pr),
            np.clip(pitch, -pr, pr),
            np.clip(yaw, -yl, yl),
        ])

    def tick(self, now: float, dt: float) -> dict | None:
        """Advance one control step. Returns a FullBodyTarget dict, or None to send nothing."""
        if not self.is_engaged(now):
            self.synced = False  # re-sync to the robot on the next engage
            return None
        if not self.synced:
            return None

        s = self.settings
        # A stalled loop must not turn into one big step: cap dt at two periods.
        dt = min(max(dt, 0.0), 2.0 / s.send_rate_hz)
        target = self.target_rpy()
        error = target - self.cmd
        if not self.caught_up and np.max(np.abs(error)) < math.radians(3.0):
            self.caught_up = True
        speed = s.max_speed_deg_s if self.caught_up else min(s.catchup_speed_deg_s, s.max_speed_deg_s)
        step = s.smoothing * error
        max_step = math.radians(speed) * dt
        self.cmd = self.cmd + np.clip(step, -max_step, max_step)

        body_limit = math.radians(BODY_YAW_LIMIT_DEG)
        if s.body_follow:
            follow = math.radians(s.body_follow_deg)
            desired = self.body
            delta = self.cmd[2] - self.body
            if delta > follow:
                desired = self.cmd[2] - follow
            elif delta < -follow:
                desired = self.cmd[2] + follow
            desired = float(np.clip(desired, -body_limit, body_limit))
            # Same speed cap as the head; enforce() keeps the 65 deg delta meanwhile.
            self.body = self.body + float(np.clip(desired - self.body, -max_step, max_step))

        max_move = s.max_linear_speed_mm_s / 1000.0 * dt
        norm = float(np.linalg.norm(self.pos))
        self.pos = np.zeros(3) if norm <= max_move else self.pos * (1.0 - max_move / norm)

        pose = pose_math.pose_matrix(pose_math.rpy_to_matrix(*self.cmd), self.pos)
        safe_pose, safe_body = enforce(pose, self.body if s.body_follow else None, self.body)
        self.limited = not np.allclose(safe_pose, pose, atol=1e-6)
        return pose_math.full_body_target(safe_pose, safe_body)

    def status(self, now: float) -> ControllerStatus:
        return ControllerStatus(
            calibrated=self.calibration is not None,
            engaged=self.is_engaged(now),
            synced=self.synced,
            limited=self.limited,
            catching_up=self.synced and not self.caught_up,
            blocked=self.blocked,
            head_rpy_deg=tuple(round(math.degrees(v), 1) for v in self.cmd),
            body_yaw_deg=round(math.degrees(self.body), 1),
        )


@dataclass
class RobotState:
    """Snapshot of the daemon, polled a few times per second."""

    motors: str | None  # "enabled" / "disabled" / "gravity_compensation"
    moving: bool  # a daemon move (wake_up, goto_sleep, goto, recorded move) is running
    head: tuple[float, float, float, float, float, float]  # x, y, z (m), roll, pitch, yaw (rad)
    body_yaw: float
    requested_at: float  # monotonic time the poll was started

    @property
    def rpy(self) -> np.ndarray:
        return np.array(self.head[3:])

    @property
    def position(self) -> tuple[float, float, float]:
        return self.head[0], self.head[1], self.head[2]


# Present-vs-commanded difference that means "the robot is not following us"
# (move started elsewhere, motors limp, daemon restarted): stop and re-sync.
DIVERGENCE_DEG = 30.0
DIVERGENCE_MM = 15.0
# Robot state older than this is unknown: no motion.
STATE_STALE_S = 1.0
POLL_INTERVAL_S = 0.2


@dataclass
class RobotLink:
    """WebSocket pose stream + REST state polling and actions to the Reachy daemon."""

    host: str
    port: int = 8000
    connected: bool = False
    last_error: str = ""
    state: RobotState | None = None
    _ws: object = field(default=None, repr=False)
    _http: httpx.AsyncClient | None = field(default=None, repr=False)
    _tasks: list = field(default_factory=list, repr=False)
    # Actions in flight block motion, and only polls started after the last action
    # finished may overwrite state (earlier ones predate the action's effect).
    _actions_in_flight: int = field(default=0, repr=False)
    _last_action_done: float = field(default=-math.inf, repr=False)

    @property
    def ws_url(self) -> str:
        return f"ws://{self.host}:{self.port}/api/move/ws/set_target"

    @property
    def api_url(self) -> str:
        return f"http://{self.host}:{self.port}/api"

    async def start(self) -> None:
        self._http = httpx.AsyncClient(base_url=self.api_url, timeout=2.0)
        self._tasks = [asyncio.create_task(self._connect_loop()), asyncio.create_task(self._poll_loop())]

    async def stop(self) -> None:
        for task in self._tasks:
            task.cancel()
        await asyncio.gather(*self._tasks, return_exceptions=True)
        self._tasks = []
        if self._ws is not None:
            await self._ws.close()
        if self._http is not None:
            await self._http.aclose()
            self._http = None

    # --- pose stream ------------------------------------------------------
    async def _connect_loop(self) -> None:
        backoff = 0.5
        while True:
            try:
                async with ws_connect(self.ws_url, open_timeout=3, max_queue=64) as ws:
                    self._ws = ws
                    self.connected = True
                    self.last_error = ""
                    backoff = 0.5
                    log.info("Connected to daemon %s", self.ws_url)
                    async for message in ws:  # daemon only replies on errors
                        self._on_daemon_message(message)
            except asyncio.CancelledError:
                raise
            except Exception as exc:  # noqa: BLE001 - keep reconnecting whatever happens
                self.last_error = f"daemon ws: {exc}"
                log.warning("Daemon WebSocket: %s", exc)
            finally:
                self._ws = None
                self.connected = False
            await asyncio.sleep(backoff)
            backoff = min(backoff * 2, 5.0)

    def _on_daemon_message(self, message: str | bytes) -> None:
        try:
            data = json.loads(message)
        except ValueError:
            return
        if isinstance(data, dict) and data.get("status") == "error":
            self.last_error = f"daemon: {data.get('detail', '')}"[:300]
            log.warning("Daemon rejected target: %s", data.get("detail"))

    async def send_target(self, target: dict) -> bool:
        ws = self._ws
        if ws is None:
            return False
        try:
            await ws.send(json.dumps(target))
            return True
        except websockets.ConnectionClosed:
            return False

    # --- robot state ------------------------------------------------------
    async def fetch_state(self) -> RobotState:
        assert self._http is not None
        requested_at = time.monotonic()
        motors, running, head, body = await asyncio.gather(
            self._http.get("/motors/status"),
            self._http.get("/move/running"),
            self._http.get("/state/present_head_pose"),
            self._http.get("/state/present_body_yaw"),
        )
        for response in (motors, running, head, body):
            response.raise_for_status()
        h = head.json()
        return RobotState(
            motors=motors.json().get("mode"),
            moving=len(running.json()) > 0,
            head=tuple(float(h[k]) for k in ("x", "y", "z", "roll", "pitch", "yaw")),
            body_yaw=float(body.json()),
            requested_at=requested_at,
        )

    async def _poll_loop(self) -> None:
        while True:
            try:
                state = await self.fetch_state()
                if self._actions_in_flight == 0 and state.requested_at > self._last_action_done:
                    self.state = state
            except asyncio.CancelledError:
                raise
            except Exception as exc:  # noqa: BLE001
                self.state = None
                self.last_error = f"robot state: {exc}"[:300]
            await asyncio.sleep(POLL_INTERVAL_S)

    def blocked_reason(self, now: float) -> str:
        """Why the robot must not be commanded right now ("" = ready)."""
        st = self.state
        if not self.connected:
            return "no daemon connection"
        if self._actions_in_flight:
            return "action in progress"
        if st is None or now - st.requested_at > STATE_STALE_S:
            return "robot state unknown"
        if st.motors != "enabled":
            return f"motors {st.motors or 'unknown'}"
        if st.moving:
            return "robot move running"
        return ""

    # --- actions ----------------------------------------------------------
    ACTIONS = {
        "wake_up": "/move/play/wake_up",
        "goto_sleep": "/move/play/goto_sleep",
        "motors_on": "/motors/set_mode/enabled",
        "motors_off": "/motors/set_mode/disabled",
    }

    async def action(self, name: str) -> dict:
        path = self.ACTIONS.get(name)
        if path is None:
            raise ValueError(f"unknown action {name!r}")
        assert self._http is not None
        # Block motion right away, until a poll started after the POST returns
        # shows the daemon's real state (move running, motors off, ...).
        self._actions_in_flight += 1
        if self.state is not None:
            self.state.moving = self.state.moving or name in ("wake_up", "goto_sleep")
            if name == "motors_off":
                self.state.motors = "disabled"
        try:
            response = await self._http.post(path)
            response.raise_for_status()
        finally:
            self._actions_in_flight -= 1
            self._last_action_done = time.monotonic()
        try:
            return response.json()
        except ValueError:
            return {"status": response.status_code}


def diverged(state: RobotState, cmd_history: list[tuple[float, np.ndarray, np.ndarray]]) -> bool:
    """True if the robot is far from what we commanded around the time it was polled."""
    before = [h for h in cmd_history if h[0] <= state.requested_at]
    if not before:
        return False
    _, rpy, pos = before[-1]
    diff = np.abs(state.rpy - rpy)
    diff[2] = abs(pose_math.unwrap_angle(state.rpy[2], rpy[2]) - rpy[2])
    moved_mm = 1000.0 * float(np.linalg.norm(np.array(state.position) - pos))
    return bool(np.max(diff) > math.radians(DIVERGENCE_DEG) or moved_mm > DIVERGENCE_MM)


async def control_loop(controller: TeleopController, link: RobotLink, on_sent=None) -> None:
    """Fixed-rate loop: gate on robot state, sync on engage, tick, send."""
    period = 1.0 / controller.settings.send_rate_hz
    last = time.monotonic()
    history: list[tuple[float, np.ndarray, np.ndarray]] = []  # (t, cmd rpy, cmd pos)
    checked_state = None
    while True:
        now = time.monotonic()
        dt, last = now - last, now
        blocked = link.blocked_reason(now)
        controller.blocked = blocked
        if blocked:
            controller.desync()  # robot may move (or have moved) without us
        elif controller.is_engaged(now) and not controller.synced:
            try:
                st = await link.fetch_state()
                if not st.moving and st.motors == "enabled":
                    controller.sync_from_robot(*st.rpy, st.body_yaw, position=st.position)
                    history.clear()
                    checked_state = link.state
            except Exception as exc:  # noqa: BLE001
                link.last_error = f"cannot read robot pose: {exc}"
            now = last = time.monotonic()  # the REST round trip is not motion time
            dt = 0.0
        elif controller.synced and link.state is not checked_state:
            checked_state = link.state
            if diverged(link.state, history):
                controller.desync()
                link.last_error = "robot stopped following; re-synced to its pose"
                log.warning(link.last_error)

        target = None if blocked else controller.tick(now, dt)
        if target is not None:
            # Record what was actually sent (after enforce()), not cmd: enforce() may
            # clamp yaw to body +-65 deg, and the robot correctly stops there.
            sent = np.array(target["target_head_pose"]["m"]).reshape(4, 4)
            sent_rpy = np.array(pose_math.matrix_to_rpy(sent[:3, :3]))
            sent_rpy[2] = pose_math.unwrap_angle(sent_rpy[2], controller.cmd[2])
            history.append((now, sent_rpy, sent[:3, 3].copy()))
            del history[:-100]
            if await link.send_target(target) and on_sent:
                on_sent(target)
        await asyncio.sleep(max(0.0, period - (time.monotonic() - now)))
