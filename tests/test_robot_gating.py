"""Round-3 features: robot-state gating, actions, divergence backstop, catch-up, translation ramp,
page sessions, Origin check, pong clock, rx counting. Real uvicorn app against FakeDaemon."""

import asyncio
import json
import math
import time

import httpx
import numpy as np
import pytest
from websockets.asyncio.client import connect
from websockets.exceptions import InvalidStatus

from fakes import wait_for, wait_quiet
from phone_teleop import teleop as tp
from phone_teleop.teleop import RobotState, TeleopController, TeleopSettings, diverged
from test_server import A0, B0, G0, Phone, calibrated_and_held, frame_rpy_body, phone

PERIOD = 0.02


def frame_pos(f):
    return np.array(f["target_head_pose"]["m"]).reshape(4, 4)[:3, 3]


def count_syncs(controller):
    calls = []
    orig = controller.sync_from_robot

    def wrapped(*a, **kw):
        calls.append(time.monotonic())
        return orig(*a, **kw)

    controller.sync_from_robot = wrapped
    return calls


def max_step_deg(frames, start=None):
    rpys = [frame_rpy_body(f)[0] for f in frames]
    if start is not None:
        rpys = [np.asarray(start), *rpys]
    d = np.abs(np.diff(np.array(rpys), axis=0))
    d[:, 2] = np.minimum(d[:, 2], 2 * math.pi - d[:, 2])
    return float(np.degrees(d.max())) if len(d) else 0.0


def statuses(p):
    return p.of("status")


# --- gating ---------------------------------------------------------------------

async def test_status_carries_robot_state(teleop):
    d = teleop.daemon
    async with phone(teleop) as p:
        await wait_for(lambda: statuses(p) and statuses(p)[-1]["motors"] == "enabled"
                       and statuses(p)[-1]["blocked"] == "")
        st = statuses(p)[-1]
        assert st["moving"] is False and st["blocked"] == "" and "catching_up" in st
        d.motors = "disabled"
        await wait_for(lambda: statuses(p)[-1]["motors"] == "disabled")
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: statuses(p)[-1]["blocked"] == "motors disabled")
        assert d.frames == []


@pytest.mark.parametrize("post_delay", [0.0, 0.5])  # 0.5: slow POST (> poll interval)
async def test_motors_off_while_holding_stops_frames_immediately(teleop, post_delay):
    d = teleop.daemon
    d.action_delay_s = post_delay
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: len(d.frames) >= 10)
        await p.send({"type": "action", "name": "motors_off"})
        t_sent = time.monotonic()
        assert (await p.reply("action"))["ok"]
        await asyncio.sleep(0.4)  # well past the POST and the next polls
        late = [t for t in d.frame_times if t > t_sent + 0.05]
        assert late == [], f"{len(late)} frames after motors_off was requested"
        await wait_for(lambda: statuses(p)[-1]["blocked"] == "motors disabled")
        assert d.motors == "disabled"
        # motors on again, still holding: resumes from the present pose, slowly
        n = len(d.frames)
        start = [d.head["roll"], d.head["pitch"], d.head["yaw"]]
        await p.send({"type": "action", "name": "motors_on"})
        await wait_for(lambda: len(d.frames) > n + 10, timeout=4)
        assert max_step_deg(d.frames[n:], start) <= 60 * PERIOD * 1.5


async def test_wake_up_while_holding_no_snap_after_move(teleop):
    """Reviewer scenario 1 (fake): hold at yaw 40, wake_up from the phone, move ends at yaw 0."""
    d = teleop.daemon
    d.move_duration_s = 0.5
    d.move_end_head = {"wake_up": {"yaw": 0.0, "pitch": 0.0, "roll": 0.0, "x": 0.0, "y": 0.0, "z": 0.0}}
    d.move_end_body = {"wake_up": 0.0}
    s = teleop.controller.settings
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        p.stream(held=True, a=A0 + 40.0)
        await wait_for(lambda: d.frames and abs(math.degrees(d.head["yaw"]) - 40) < 0.5, timeout=4)
        await p.send({"type": "action", "name": "wake_up"})
        t_action = time.monotonic()
        await wait_for(lambda: d.running, timeout=2)
        await wait_for(lambda: not d.running, timeout=3)
        t_end = time.monotonic()
        during = [t for t in d.frame_times if t_action + 0.05 < t < t_end]
        assert during == [], f"{len(during)} frames sent while the move was running"
        n = len(d.frames)
        await wait_for(lambda: len(d.frames) > n + 30, timeout=4)
        after = d.frames[n:]
        # Starts where the move left the robot (yaw 0), heads back to 40 at the catch-up speed.
        assert abs(math.degrees(frame_rpy_body(after[0])[0][2])) < 0.5
        assert max_step_deg(after, [0, 0, 0]) <= s.catchup_speed_deg_s * PERIOD * 1.5
        await wait_for(lambda: abs(math.degrees(frame_rpy_body(d.frames[-1])[0][2]) - 40) < 0.5, timeout=4)
        await wait_for(lambda: statuses(p)[-1]["catching_up"] is False)


async def test_external_move_is_caught_by_polling(teleop):
    """Someone POSTs wake_up straight to the daemon: we must stop and re-sync after it."""
    d = teleop.daemon
    d.move_duration_s = 0.6
    d.move_end_head = {"wake_up": {"yaw": math.radians(-30)}}
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        p.stream(held=True, a=A0 + 20.0)
        await wait_for(lambda: len(d.frames) > 5)
        async with httpx.AsyncClient(base_url=f"http://127.0.0.1:{d.port}") as c:
            await c.post("/api/move/play/wake_up")
        t0 = time.monotonic()
        await wait_for(lambda: statuses(p)[-1]["blocked"] == "robot move running", timeout=2)
        stop = await wait_quiet(lambda: len(d.frames), quiet=0.15)
        last_during = max(t for t in d.frame_times[:stop])
        assert last_during - t0 <= tp.POLL_INTERVAL_S * 2 + 0.1  # caught within ~ one poll
        await wait_for(lambda: not d.running, timeout=3)
        n = len(d.frames)
        await wait_for(lambda: len(d.frames) > n + 10, timeout=3)
        assert abs(math.degrees(frame_rpy_body(d.frames[n])[0][2]) + 30) < 0.5  # re-synced at -30
        assert max_step_deg(d.frames[n:], [0, 0, math.radians(-30)]) <= 60 * PERIOD * 1.5


async def test_action_poll_race_slow_post(teleop):
    """Polls answered while the wake_up POST is still in flight (move not registered yet) must
    not unblock motion."""
    d = teleop.daemon
    d.move_duration_s = 0.4
    d.action_delay_s = 0.5
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        p.stream(held=True, a=A0 + 10.0)
        await wait_for(lambda: len(d.frames) > 5)
        await p.send({"type": "action", "name": "wake_up"})
        t0 = time.monotonic()
        await wait_for(lambda: d.running, timeout=2)
        await wait_for(lambda: not d.running, timeout=2)
        t_end = time.monotonic()
        during = [t for t in d.frame_times if t0 + 0.05 < t < t_end]
        assert during == [], f"{len(during)} frames between the action and the end of the move"


async def test_state_poll_failure_blocks(teleop):
    d = teleop.daemon
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: len(d.frames) > 5)
        d.state_status = 500
        await wait_for(lambda: statuses(p)[-1]["blocked"] == "robot state unknown", timeout=2)
        n = await wait_quiet(lambda: len(d.frames))
        d.state_status = 200
        await wait_for(lambda: len(d.frames) > n + 5, timeout=3)


async def test_blocked_does_not_flap_in_normal_operation(teleop):
    d = teleop.daemon
    d.state_delay_s = 0.1  # ordinary Wi-Fi latency on the state reads
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: statuses(p)[-1]["synced"])
        syncs = count_syncs(teleop.controller)
        k = len(statuses(p))
        await asyncio.sleep(2.0)
        seen = {st["blocked"] for st in statuses(p)[k:]}
        assert seen == {""}, seen
        assert syncs == []


# --- divergence ------------------------------------------------------------------

async def test_divergence_resyncs_when_robot_moved_elsewhere(teleop):
    d = teleop.daemon
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        p.stream(held=True, a=A0 + 10.0)
        await wait_for(lambda: abs(math.degrees(d.head["yaw"]) - 10) < 0.5, timeout=4)
        d.follow = False
        d.head["yaw"] = math.radians(-50.0)  # pushed by hand / another controller
        await wait_for(lambda: "stopped following" in statuses(p)[-1]["error"], timeout=2)
        d.follow = True
        n = len(d.frames)
        await wait_for(lambda: len(d.frames) > n + 10)
        # re-synced from -50 (some frames before the re-sync may still say ~10)
        firsts = [math.degrees(frame_rpy_body(f)[0][2]) for f in d.frames[n:]]
        i = next(i for i, y in enumerate(firsts) if y < -40)
        assert max_step_deg(d.frames[n + i:]) <= 60 * PERIOD * 1.5


@pytest.mark.parametrize("lag_s", [0.05, 0.1])
async def test_no_divergence_false_positive_fast_following(make_teleop, lag_s):
    """Phone swinging at ~180 deg/s with a robot that lags 50-100 ms must not stutter."""
    t = await make_teleop()
    d = t.daemon
    d.follow_lag_s = lag_s
    async with phone(t) as p:
        await calibrated_and_held(t, p)
        await wait_for(lambda: statuses(p)[-1]["synced"])
        p.stream(held=True, a=A0)
        await asyncio.sleep(0.3)  # catch up
        syncs = count_syncs(t.controller)

        async def swing():
            t0 = time.monotonic()
            while time.monotonic() - t0 < 2.0:
                ph = (time.monotonic() - t0) * 2 * math.pi * 0.5  # 0.5 Hz, +-60 deg -> ~190 deg/s peak
                p.stream_args["a"] = A0 + 60 * math.sin(ph)
                await asyncio.sleep(0.01)

        await swing()
        assert syncs == [], f"{len(syncs)} re-syncs during normal fast following"
        assert "stopped following" not in t.link.last_error


async def test_no_divergence_false_positive_no_body_mode(make_teleop):
    """--no-body: head yaw is clamped to body +-65 by enforce() while cmd keeps going. The
    divergence check must compare against what was SENT, else it re-syncs over and over."""
    t = await make_teleop(TeleopSettings(body_follow=False))
    d = t.daemon
    async with phone(t) as p:
        await calibrated_and_held(t, p)
        p.stream(held=True, a=A0 + 120.0)
        await wait_for(lambda: abs(math.degrees(d.head["yaw"]) - 65) < 0.5, timeout=5)
        syncs = count_syncs(t.controller)
        await asyncio.sleep(1.5)
        assert syncs == [], f"{len(syncs)} spurious re-syncs while holding at the 65 deg clamp"
        assert "stopped following" not in t.link.last_error


# --- translation ramp ------------------------------------------------------------

async def test_translation_ramps_from_sleep_pose(teleop):
    """Reviewer scenario 2 (fake): sync from a sleep pose 43 mm low; ramp at <= 40 mm/s, no jump."""
    d = teleop.daemon
    d.head.update(z=-0.043, x=0.005, pitch=math.radians(30))
    s = teleop.controller.settings
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: d.frames and np.linalg.norm(frame_pos(d.frames[-1])) == 0.0, timeout=5)
    pos = np.array([frame_pos(f) for f in d.frames])
    assert np.linalg.norm(pos[0] - [0.005, 0, -0.043]) < 1e-9  # first frame = present pose
    steps_mm = 1000 * np.linalg.norm(np.diff(pos, axis=0), axis=1)
    assert steps_mm.max() <= s.max_linear_speed_mm_s * PERIOD * 2 + 1e-9  # dt capped at 2 periods
    assert "stopped following" not in teleop.link.last_error


# --- page sessions ------------------------------------------------------------

async def hello(p, page):
    msg = {"type": "hello"} if page is None else {"type": "hello", "page": page}
    await p.send(msg)
    return await p.reply("hello")


async def test_same_page_reconnect_keeps_calibration(teleop):
    d = teleop.daemon
    async with phone(teleop) as p:
        assert (await hello(p, "page-A"))["resumed"] is False
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: len(d.frames) > 3)
    async with phone(teleop) as p:
        assert (await hello(p, "page-A"))["resumed"] is True
        await wait_for(lambda: statuses(p) and statuses(p)[-1]["calibrated"])
        n = len(d.frames)
        p.stream(held=True)
        await wait_for(lambda: len(d.frames) > n + 3)


@pytest.mark.parametrize("second", ["page-B", None, "", 42])
async def test_other_page_or_no_page_clears_calibration(teleop, second):
    async with phone(teleop) as p:
        await hello(p, "page-A")
        await calibrated_and_held(teleop, p)
    async with phone(teleop) as p:
        await p.send({"type": "hello", "page": second} if second is not None else {"type": "hello"})
        assert (await p.reply("hello"))["resumed"] is False
        await asyncio.sleep(0.3)  # a couple of status messages after the hello
        assert statuses(p)[-1]["calibrated"] is False and teleop.controller.calibration is None


async def test_takeover_then_original_page_does_not_get_other_calibration(teleop):
    async with phone(teleop) as a:
        await hello(a, "page-A")
        await calibrated_and_held(teleop, a)
    async with phone(teleop) as b:
        await hello(b, "page-B")
        await calibrated_and_held(teleop, b)
    async with phone(teleop) as a2:
        assert (await hello(a2, "page-A"))["resumed"] is False
    async with phone(teleop) as b2:  # a2 (uncalibrated) was in between: nothing to resume
        assert (await hello(b2, "page-B"))["resumed"] is False
        assert teleop.controller.calibration is None


async def test_resume_twice_is_harmless(teleop):
    async with phone(teleop) as p:
        await hello(p, "P")
        await calibrated_and_held(teleop, p)
    async with phone(teleop) as p:
        assert (await hello(p, "P"))["resumed"] is True
        assert (await hello(p, "P"))["resumed"] is False
        assert teleop.controller.calibration is not None
        await wait_for(lambda: statuses(p) and statuses(p)[-1]["calibrated"] is True)


# --- origin / pong / rx ----------------------------------------------------------

@pytest.mark.parametrize("path", ["/pose", "/signal"])
async def test_origin_check(teleop, path):
    host = f"127.0.0.1:{teleop.port}"
    async with connect(teleop.ws(path), origin=f"https://{host}") as ws:
        await ws.send(json.dumps({"type": "ping", "t": 1}))
        await asyncio.wait_for(ws.recv(), 3)
    for bad in ["https://evil.example", f"https://127.0.0.1:{teleop.port + 1}", "null"]:
        with pytest.raises(InvalidStatus) as exc:
            async with connect(teleop.ws(path), origin=bad):
                pass
        assert exc.value.response.status_code == 403


async def test_pong_has_server_epoch_ms(teleop):
    async with phone(teleop) as p:
        await p.send({"type": "ping", "t": 5})
        r = await p.reply("pong")
        assert r["t"] == 5 and abs(r["server_t"] - time.time() * 1000) < 2000


async def test_rx_counts_only_real_events(teleop):
    async with phone(teleop) as p:
        for src in ["evt", "keepalive", "ptr", "center", None, "evt"]:
            msg = {"type": "orient", "a": 0, "b": 0, "g": -90, "held": False}
            if src:
                msg["src"] = src
            await p.send(msg)
        await p.send({"type": "ping", "t": 0})
        await p.reply("pong")
        assert teleop.hub.rx_count == 3


async def test_csv_has_src_and_event_time(make_teleop, tmp_path):
    path = tmp_path / "sub" / "log.csv"
    t = await make_teleop(csv_path=path)
    async with phone(t) as p:
        await p.send({"type": "orient", "a": 1, "b": 2, "g": 3, "held": True, "seq": 9, "t": 4.5, "et": 4.25,
                      "src": "keepalive"})
        await p.send({"type": "ping", "t": 0})
        await p.reply("pong")
    t.hub._csv_file.flush()
    rows = [line.split(",") for line in path.read_text().strip().splitlines()]
    assert rows[1][1:] == ["4.5", "4.25", "keepalive", "9", "1", "2", "3", "1"]


async def test_static_no_store(teleop):
    async with httpx.AsyncClient() as c:
        r = await c.get(teleop.http("/static/app.mjs"))
        assert r.headers["cache-control"] == "no-store"


# --- pure units -------------------------------------------------------------------

def state(rpy=(0, 0, 0), pos=(0, 0, 0), t=10.0, motors="enabled", moving=False):
    return RobotState(motors=motors, moving=moving, head=(*pos, *rpy), body_yaw=0.0, requested_at=t)


def test_diverged_unit():
    hist = [(9.0, np.zeros(3), np.zeros(3)), (9.9, np.array([0, 0, math.radians(179)]), np.zeros(3))]
    assert not diverged(state(rpy=(0, 0, math.radians(-179))), hist)  # yaw wraps
    assert diverged(state(rpy=(0, 0, math.radians(140))), hist)
    assert not diverged(state(pos=(0, 0, -0.014), rpy=(0, 0, math.radians(179))), hist)
    assert diverged(state(pos=(0, 0, -0.016), rpy=(0, 0, math.radians(179))), hist)
    assert not diverged(state(rpy=(1, 1, 1), t=8.0), hist)  # no command before the poll
    assert diverged(state(rpy=(math.radians(31), 0, math.radians(179))), hist)


def test_blocked_reason_unit():
    link = tp.RobotLink("x")
    assert link.blocked_reason(10.0) == "no daemon connection"
    link.connected = True
    assert link.blocked_reason(10.0) == "robot state unknown"
    link.state = state(t=10.0)
    assert link.blocked_reason(10.5) == ""
    assert link.blocked_reason(10.0 + tp.STATE_STALE_S + 0.01) == "robot state unknown"
    link.state = state(t=10.0, motors="gravity_compensation")
    assert link.blocked_reason(10.1) == "motors gravity_compensation"
    link.state = state(t=10.0, motors=None)
    assert link.blocked_reason(10.1) == "motors unknown"
    link.state = state(t=10.0, moving=True)
    assert link.blocked_reason(10.1) == "robot move running"


def _sync_tick(c, robot=(0, 0, 0, 0), pos=(0, 0, 0)):
    c.sync_from_robot(*robot, position=pos)


@pytest.mark.parametrize("sample, desc", [
    ((A0, B0, G0 + 70.0), "pitch target beyond 40 (pre-clamped)"),
    ((A0, B0, G0 + 85.0), "tilt-hold branch (> 75 deg)"),
    ((A0 + 170.0, B0, G0), "yaw near the 179 clamp"),
])
def test_catch_up_finishes_at_clamp_limits(sample, desc):
    c = TeleopController(TeleopSettings())
    c.on_sample(A0, B0, G0, False, 0.0)
    c.calibrate()
    c.on_sample(*sample, True, 0.0)
    _sync_tick(c)
    t = 0.0
    for _ in range(500):
        t += PERIOD
        c.on_sample(*sample, True, t)
        c.tick(t, PERIOD)
        if c.caught_up:
            break
    assert c.caught_up, desc


def test_catch_up_no_body_mode_beyond_65_finishes():
    c = TeleopController(TeleopSettings(body_follow=False))
    c.on_sample(A0, B0, G0, False, 0.0)
    c.calibrate()
    _sync_tick(c)
    t = 0.0
    for _ in range(500):
        t += PERIOD
        c.on_sample(A0 + 120.0, B0, G0, True, t)
        c.tick(t, PERIOD)
    assert c.caught_up


def test_catch_up_speed_never_exceeds_max_speed():
    c = TeleopController(TeleopSettings(max_speed_deg_s=30.0, catchup_speed_deg_s=60.0, smoothing=1.0))
    c.on_sample(A0, B0, G0, False, 0.0)
    c.calibrate()
    _sync_tick(c)
    c.on_sample(A0 + 90, B0, G0, True, 0.0)
    out = c.tick(0.0, PERIOD)
    assert math.degrees(frame_rpy_body(out)[0][2]) == pytest.approx(30.0 * PERIOD)


def test_sync_rejects_non_finite_robot_pose():
    c = TeleopController()
    with pytest.raises(ValueError):
        c.sync_from_robot(0, float("nan"), 0, 0)
    with pytest.raises(ValueError):
        c.sync_from_robot(0, 0, 0, 0, position=(0, float("inf"), 0))
    assert not c.synced


def test_translation_ramp_unit():
    s = TeleopSettings(max_linear_speed_mm_s=40.0)
    c = TeleopController(s)
    c.on_sample(A0, B0, G0, True, 0.0)
    c.calibrate()
    c.sync_from_robot(0, 0, 0, 0, position=(0.0, 0.03, -0.04))  # 50 mm away
    t, last, steps = 0.0, np.array([0.0, 0.03, -0.04]), []
    for _ in range(100):
        t += PERIOD
        c.on_sample(A0, B0, G0, True, t)
        p = frame_pos(c.tick(t, PERIOD))
        steps.append(np.linalg.norm(p - last) * 1000)
        last = p
    assert max(steps) <= 40.0 * PERIOD + 1e-9
    assert np.allclose(last, 0)
    assert sum(steps) == pytest.approx(50.0)  # straight line, no overshoot
