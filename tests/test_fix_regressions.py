"""Regression tests for the round-1 fixes (tilt hold, body cap, dt cap, malformed frames, new session)."""

import asyncio
import json
import math

import numpy as np
import pytest
from scipy.spatial.transform import Rotation as R
from websockets.asyncio.client import connect

from fakes import wait_for, wait_quiet
from phone_teleop import pose_math as pm
from phone_teleop.teleop import MAX_TILT_DEG, TeleopController, TeleopSettings
from test_controller_properties import Sim, calibrated_sim, check_speed, out_rpy_body

DT = 0.02
A0, B0, G0 = 0.0, 0.0, -90.0
NEUTRAL = pm.device_rotation(A0, B0, G0)
W0 = pm.levelled_frame(NEUTRAL)


def sample_for_head(roll, pitch, yaw):
    """W3C angles of the phone pose whose calibrated head rotation is Rxyz(roll, pitch, yaw) (deg)."""
    head = R.from_euler("xyz", [roll, pitch, yaw], degrees=True).as_matrix()
    device = W0 @ head @ W0.T @ NEUTRAL
    import warnings
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", UserWarning)
        a, b, g = R.from_matrix(device).as_euler("ZXY", degrees=True)
    return a, b, g


def run_path(sim, path):
    targets = []
    for rpy in path:
        sim.sample(*sample_for_head(*rpy), True)
        sim.tick()
        targets.append(np.degrees(sim.c.target_rpy()))
    return np.array(targets)


@pytest.mark.parametrize("roll, yaw", [(0, 0), (10, 30), (-25, -120), (35, 170)])
@pytest.mark.parametrize("direction", [1, -1])  # look down / look up
def test_tilt_sweep_through_threshold_is_continuous(roll, yaw, direction):
    s = TeleopSettings(smoothing=0.5, max_speed_deg_s=180.0)
    sim = calibrated_sim(s, robot=(0.0, 0.0, math.radians(yaw) if abs(yaw) < 150 else math.radians(150),
                                   math.radians(yaw) * 0.6))
    # settle at the start pose, then tilt to 110 deg (past vertical) and back, 0.5 deg per tick
    start = [(roll, 0, yaw)] * 100
    sweep = [(roll, direction * p, yaw) for p in np.arange(0, 110, 0.5)]
    path = start + sweep + sweep[::-1] + start
    targets = run_path(sim, path)
    check_speed(sim, 180.0)
    # Target must not jump (allow wrap of yaw near 180)
    d = np.abs(np.diff(targets, axis=0))
    d[:, 2] = np.minimum(d[:, 2], 360 - d[:, 2])
    assert d.max() < 3.0, (d.max(axis=0), np.argmax(d.max(axis=1)))
    outs = np.array([np.degrees(out_rpy_body(o[1])[0]) for o in sim.outputs])
    # Head never spins around: yaw stays near the commanded heading, roll near the phone roll.
    yaw_err = (outs[:, 2] - min(yaw, 179) + 180) % 360 - 180
    assert np.abs(yaw_err[100:]).max() < 5.0, np.abs(yaw_err).max()
    assert np.abs(outs[100:, 0] - np.clip(roll, -40, 40)).max() < 5.0
    # Pitch saturates in the direction of the tilt
    assert outs[:, 1].max() <= 40 + 1e-6 and outs[:, 1].min() >= -40 - 1e-6
    mid = 100 + len(sweep) - 1
    assert outs[mid, 1] == pytest.approx(direction * 40.0, abs=1.0)
    # back at the start pose at the end
    assert np.abs(outs[-1] - [np.clip(roll, -40, 40), 0, min(yaw, 179)]).max() < 2.0


def test_noise_around_threshold_does_not_drift():
    rng = np.random.default_rng(5)
    sim = calibrated_sim(smoothing=0.5, max_speed_deg_s=180.0, robot=(0, 0, math.radians(20), 0))
    path = [(rng.normal(0, 2), MAX_TILT_DEG + rng.normal(0, 3), 20 + rng.normal(0, 2)) for _ in range(500)]
    run_path(sim, path)
    check_speed(sim, 180.0)
    outs = np.array([np.degrees(out_rpy_body(o[1])[0]) for o in sim.outputs])
    assert np.abs(outs[:, 2] - 20).max() < 6.0
    assert np.abs(outs[:, 0]).max() < 6.0
    assert outs[-50:, 1].min() > 35.0


def test_pure_roll_past_threshold_holds():
    """Phone rotated from landscape to portrait after calibration (roll 90): saturate/hold, no spin."""
    sim = calibrated_sim(smoothing=1.0, max_speed_deg_s=180.0)
    run_path(sim, [(r, 0, 0) for r in np.arange(0, 180, 1.0)] + [(179.0, 0, 0)] * 50)
    check_speed(sim, 180.0)
    outs = np.array([np.degrees(out_rpy_body(o[1])[0]) for o in sim.outputs])
    assert np.abs(outs[:, 2]).max() < 1.0 and np.abs(outs[:, 1]).max() < 1.0
    assert outs[-1, 0] == pytest.approx(40.0, abs=0.5)


# --- body cap ------------------------------------------------------------------

@pytest.mark.parametrize("start_delta", [55.0, 60.0, 65.0, -65.0])
def test_body_cap_keeps_65_delta_when_starting_at_limit(start_delta):
    sim = calibrated_sim(smoothing=1.0, max_speed_deg_s=180.0,
                         robot=(0, 0, math.radians(start_delta), 0.0))
    sign = math.copysign(1, start_delta)
    # Phone pulls further in the same direction, fast, then reverses.
    path = [(0, 0, start_delta + sign * k) for k in range(0, 120, 3)] + \
           [(0, 0, start_delta + sign * k) for k in range(120, -120, -4)]
    run_path(sim, path)
    check_speed(sim, 180.0, include_first=True)  # check_safe (65 delta) runs on every output
    body = [o[1]["target_body_yaw"] for o in sim.outputs]
    assert abs(math.degrees(body[-1])) > 1.0  # the body did follow


# --- dt cap / first tick after sync ---------------------------------------------

def test_dt_cap_limits_step_after_stall():
    s = TeleopSettings(smoothing=1.0, max_speed_deg_s=180.0)
    c = TeleopController(s)
    c.on_sample(A0, B0, G0, True, 0.0)
    c.calibrate()
    c.sync_from_robot(0, 0, 0, 0)
    c.on_sample(A0 + 90, B0, G0, True, 0.0)
    out = c.tick(0.01, 5.0)  # a 5 s stall, still catching up (60 deg/s)
    assert math.degrees(out_rpy_body(out)[0][2]) == pytest.approx(s.catchup_speed_deg_s * 2 / s.send_rate_hz)
    c.caught_up = True
    before = math.degrees(out_rpy_body(out)[0][2])
    out = c.tick(0.02, 5.0)
    assert math.degrees(out_rpy_body(out)[0][2]) - before == pytest.approx(180.0 * 2 / s.send_rate_hz)


async def test_first_frame_after_sync_equals_robot_pose(teleop):
    from test_server import calibrated_and_held, frame_rpy_body, phone

    d = teleop.daemon
    d.head.update(yaw=math.radians(40.0), roll=0.1)
    d.body = math.radians(10.0)
    d.state_delay_s = 0.2
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: len(d.frames) >= 6)
    rpy, body = frame_rpy_body(d.frames[0])
    assert rpy == pytest.approx([0.1, 0.0, math.radians(40.0)], abs=1e-9)
    assert body == pytest.approx(math.radians(10.0))
    yaws = [frame_rpy_body(f)[0][2] for f in d.frames[:6]]
    assert np.degrees(np.abs(np.diff(yaws))).max() <= 180 * 0.02 * 1.5


# --- malformed frames never move the robot / never feed the deadman ---------------

async def test_malformed_frames_do_not_refresh_deadman(teleop):
    from test_server import calibrated_and_held, phone

    d = teleop.daemon
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: len(d.frames) >= 5)
        p.stop_stream()
        last_sample = p.last_sent

        async def spam():
            while True:
                for raw in ["junk", "[]", "null", b"\x01\x02", "", '{"type": "orient"}']:
                    await p.send(raw)
                await asyncio.sleep(0.01)

        task = asyncio.create_task(spam())
        try:
            await wait_quiet(lambda: len(d.frames), quiet=0.2, timeout=3)
        finally:
            task.cancel()
        assert d.frame_times[-1] - last_sample <= teleop.controller.settings.deadman_timeout_s + 0.1
        await p.send({"type": "ping", "t": 3})
        assert (await p.reply("pong"))["t"] == 3


async def test_malformed_frames_between_samples_do_not_change_target(teleop):
    from test_server import calibrated_and_held, frame_rpy_body, phone

    d = teleop.daemon
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        p.stream(held=True, a=A0 + 15.0)
        await wait_for(lambda: len(d.frames) >= 3)
        for raw in ["junk", "[1]", b"\xff", "42"]:
            await p.send(raw)
        await wait_for(lambda: abs(frame_rpy_body(d.frames[-1])[0][2] - math.radians(15)) < 1e-3)
        n = len(d.frames)
        await wait_for(lambda: len(d.frames) > n + 10)
        for f in d.frames[n:]:
            assert frame_rpy_body(f)[0][2] == pytest.approx(math.radians(15), abs=1e-3)


# --- new session ---------------------------------------------------------------

async def test_reconnect_needs_center_then_moves(teleop):
    from test_server import calibrated_and_held, phone

    d = teleop.daemon
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: len(d.frames) >= 3)
    async with phone(teleop) as p:
        await p.send({"type": "calibrate"})
        r = await p.reply("calibrated")
        assert r["ok"] is False  # no sample from the new session yet: stale one was cleared
        n = len(d.frames)
        p.stream(held=True)
        await asyncio.sleep(0.15)
        assert len(d.frames) == n
        await p.send({"type": "calibrate"})
        assert (await p.reply("calibrated"))["ok"]
        await wait_for(lambda: len(d.frames) > n + 3)
