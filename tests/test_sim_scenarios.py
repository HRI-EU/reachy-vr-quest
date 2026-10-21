"""Reviewer scenarios against a real `reachy-mini-daemon --sim` (skipped unless REACHY_SIM_PORT is set).

    .venv/bin/reachy-mini-daemon --sim --headless --no-media --fastapi-port 18000 --no-preload-datasets &
    REACHY_SIM_PORT=18000 .venv/bin/python -m pytest -q tests/test_sim_scenarios.py
"""

import asyncio
import contextlib
import math
import os
import time

import httpx
import numpy as np
import pytest

from fakes import run_uvicorn, wait_for
from test_server import A0, Phone

SIM_PORT = os.environ.get("REACHY_SIM_PORT")
pytestmark = pytest.mark.skipif(not SIM_PORT, reason="set REACHY_SIM_PORT to a running sim daemon")
PERIOD = 0.02


class Rig:
    def __init__(self, link, controller, port, daemon):
        self.link, self.controller, self.port, self.daemon = link, controller, port, daemon
        self.sent: list[tuple[float, dict]] = []
        self.present: list[tuple[float, dict]] = []

    def frames_between(self, t0, t1):
        return [f for t, f in self.sent if t0 < t < t1]

    async def running(self):
        return (await self.daemon.get("/api/move/running")).json()

    async def wait_move_done(self, timeout=8):
        await wait_for(lambda: True)
        deadline = time.monotonic() + timeout
        while await self.running():
            assert time.monotonic() < deadline, "move did not finish"
            await asyncio.sleep(0.05)
        return time.monotonic()

    async def present_head(self):
        return (await self.daemon.get("/api/state/present_head_pose")).json()


@contextlib.asynccontextmanager
async def rig():
    from phone_teleop.server import create_app
    from phone_teleop.teleop import RobotLink, TeleopController, TeleopSettings

    controller = TeleopController(TeleopSettings())
    link = RobotLink("127.0.0.1", int(SIM_PORT))
    app = create_app(controller, link, signal_url="ws://127.0.0.1:9")
    async with httpx.AsyncClient(base_url=f"http://127.0.0.1:{SIM_PORT}", timeout=5) as daemon, \
            run_uvicorn(app) as port:
        r = Rig(link, controller, port, daemon)
        orig = link.send_target

        async def recording(target):
            ok = await orig(target)
            if ok:
                r.sent.append((time.monotonic(), target))
            return ok

        link.send_target = recording

        async def sample_present():
            async with httpx.AsyncClient(base_url=f"http://127.0.0.1:{SIM_PORT}", timeout=5) as c:
                while True:
                    h = (await c.get("/api/state/present_head_pose")).json()
                    r.present.append((time.monotonic(), h))
                    await asyncio.sleep(0.02)

        sampler = asyncio.create_task(sample_present())
        await daemon.post("/api/motors/set_mode/enabled")
        await r.wait_move_done()
        await daemon.post("/api/move/play/wake_up")
        await r.wait_move_done()
        await wait_for(lambda: link.connected and link.state is not None, 5)
        try:
            yield r
        finally:
            sampler.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await sampler


@contextlib.asynccontextmanager
async def held_phone(r, yaw_deg):
    from websockets.asyncio.client import connect

    ws = await connect(f"ws://127.0.0.1:{r.port}/pose")
    p = Phone(ws)
    await p.send({"type": "hello", "page": "sim"})
    await p.orient()
    await p.send({"type": "calibrate"})
    assert (await p.reply("calibrated"))["ok"]
    p.stream(held=True, a=A0 + yaw_deg)
    try:
        yield p
    finally:
        await p.close()


def rpy(target):
    from phone_teleop import pose_math as pm

    m = np.array(target["target_head_pose"]["m"]).reshape(4, 4)
    return np.degrees(pm.matrix_to_rpy(m[:3, :3])), 1000 * m[:3, 3]


def max_frame_step_deg(frames):
    a = np.array([rpy(f)[0] for f in frames])
    return float(np.abs(np.diff(a, axis=0)).max()) if len(a) > 1 else 0.0


def present_speed_deg_s(samples, t0, t1, window=0.1):
    """Max angular speed of the present pose over `window`-second spans."""
    pts = [(t, np.degrees([h["roll"], h["pitch"], h["yaw"]])) for t, h in samples if t0 <= t <= t1]
    best = 0.0
    for i, (ti, ai) in enumerate(pts):
        for tj, aj in pts[i + 1:]:
            if tj - ti >= window:
                best = max(best, float(np.abs(aj - ai).max()) / (tj - ti))
                break
    return best


async def test_sim_wake_up_while_holding_no_snap():
    """Scenario 1: hold at yaw 40, wake_up via /pose while holding, no snap when the move ends."""
    async with rig() as r:
        s = r.controller.settings
        async with held_phone(r, 40.0) as p:
            await wait_for(lambda: r.present and abs(math.degrees(r.present[-1][1]["yaw"]) - 40) < 2, 6)
            await p.send({"type": "action", "name": "wake_up"})
            t_action = time.monotonic()
            await wait_for(lambda: p.of("action"), 3)
            t_end = await r.wait_move_done()
            during = r.frames_between(t_action + 0.1, t_end - 0.05)
            assert during == [], f"{len(during)} frames sent while wake_up was running"
            await asyncio.sleep(1.5)
            after = r.frames_between(t_end - 0.05, time.monotonic())
            assert len(after) > 20
            first_yaw = rpy(after[0])[0][2]
            present_at_end = [h for t, h in r.present if t <= t_end][-1]
            assert abs(first_yaw - math.degrees(present_at_end["yaw"])) < 3.0, first_yaw
            assert max_frame_step_deg(after) <= s.catchup_speed_deg_s * PERIOD * 2 + 0.1
            # the physical (sim) head does not snap either
            assert present_speed_deg_s(r.present, t_end, time.monotonic()) <= s.catchup_speed_deg_s * 1.5
            assert abs(rpy(after[-1])[0][2] - 40.0) < 1.0  # got back to the phone
            assert "stopped following" not in r.link.last_error


async def test_sim_sleep_motors_on_engage_ramps_translation():
    """Scenario 2: goto_sleep -> motors on -> engage: translation ramps <= 40 mm/s, no 43 mm jump."""
    async with rig() as r:
        s = r.controller.settings
        await r.daemon.post("/api/move/play/goto_sleep")
        await r.wait_move_done()
        sleep = await r.present_head()
        assert sleep["z"] < -0.03  # really in the sleep pose
        async with held_phone(r, 0.0) as p:
            await p.send({"type": "action", "name": "motors_on"})
            await wait_for(lambda: p.of("action"), 3)
            t0 = time.monotonic()
            await wait_for(lambda: len(r.frames_between(t0, time.monotonic())) > 5, 5)
            await asyncio.sleep(2.0)
            frames = r.frames_between(t0, time.monotonic())
            pos = np.array([rpy(f)[1] for f in frames])
            sleep_mm = 1000 * np.array([sleep["x"], sleep["y"], sleep["z"]])
            assert np.linalg.norm(pos[0] - sleep_mm) < 3.0, (pos[0], sleep_mm)  # starts at the sleep pose
            steps = np.linalg.norm(np.diff(pos, axis=0), axis=1)
            assert steps.max() <= s.max_linear_speed_mm_s * PERIOD * 2 + 1e-6, steps.max()
            assert np.linalg.norm(pos[-1]) < 1e-6  # reached neutral
            # physical head: z velocity bounded (no 43 mm jump)
            zs = [(t, 1000 * h["z"]) for t, h in r.present if t >= t0]
            vmax = max(abs(z2 - z1) / (t2 - t1) for (t1, z1), (t2, z2) in zip(zs, zs[5:]))
            assert vmax <= s.max_linear_speed_mm_s * 2.0, vmax
            assert "stopped following" not in r.link.last_error


async def test_sim_external_move_is_caught():
    async with rig() as r:
        async with held_phone(r, 30.0) as p:
            await wait_for(lambda: r.present and abs(math.degrees(r.present[-1][1]["yaw"]) - 30) < 2, 6)
            await r.daemon.post("/api/move/play/goto_sleep")  # bypassing our app
            t0 = time.monotonic()
            await wait_for(lambda: p.of("status") and p.of("status")[-1]["blocked"] == "robot move running", 2)
            t_end = await r.wait_move_done()
            during = r.frames_between(t0 + 0.6, t_end - 0.05)
            assert during == [], f"{len(during)} frames during an external move"
            await asyncio.sleep(1.0)
            after = r.frames_between(t_end - 0.05, time.monotonic())
            assert after and max_frame_step_deg(after) <= 60 * PERIOD * 2 + 0.1
            sleep_pitch = [h for t, h in r.present if t <= t_end][-1]["pitch"]
            assert abs(rpy(after[0])[0][1] - math.degrees(sleep_pitch)) < 3.0  # re-synced to the sleep pose


async def test_sim_motors_off_while_holding_stops_frames():
    async with rig() as r:
        async with held_phone(r, 20.0) as p:
            await wait_for(lambda: len(r.sent) > 20, 5)
            await p.send({"type": "action", "name": "motors_off"})
            t0 = time.monotonic()
            await wait_for(lambda: p.of("action"), 3)
            if (await r.daemon.get("/api/motors/status")).json()["mode"] == "enabled":
                await r.daemon.post("/api/motors/set_mode/enabled")
                pytest.skip("this sim backend ignores set_mode/disabled (status stays 'enabled')")
            await asyncio.sleep(0.5)
            assert r.frames_between(t0 + 0.05, time.monotonic()) == []
            assert p.of("status")[-1]["blocked"] == "motors disabled"
        await r.daemon.post("/api/motors/set_mode/enabled")


async def test_sim_fast_following_no_false_divergence():
    """~190 deg/s peak swings: the sim lags a bit, but this must not trigger re-syncs (stutter)."""
    async with rig() as r:
        async with held_phone(r, 0.0) as p:
            await wait_for(lambda: p.of("status") and p.of("status")[-1]["synced"]
                           and not p.of("status")[-1]["catching_up"], 6)
            syncs = []
            orig = r.controller.sync_from_robot
            r.controller.sync_from_robot = lambda *a, **k: (syncs.append(time.monotonic()), orig(*a, **k))
            t0 = time.monotonic()
            while time.monotonic() - t0 < 3.0:
                p.stream_args["a"] = A0 + 60 * math.sin((time.monotonic() - t0) * math.pi)
                await asyncio.sleep(0.01)
            assert syncs == [], f"{len(syncs)} re-syncs during normal following (last error: {r.link.last_error})"
