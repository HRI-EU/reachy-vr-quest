import math

import numpy as np
import pytest

from phone_teleop import pose_math as pm
from phone_teleop.teleop import TeleopController, TeleopSettings

DT = 0.02

# Landscape upright, looking along the reference heading (beta=0, gamma=-90 in W3C terms).
A0, B0, G0 = 0.0, 0.0, -90.0


def engaged(settings=None, **kw) -> TeleopController:
    c = TeleopController(settings or TeleopSettings(**kw))
    c.on_sample(A0, B0, G0, held=True, now=0.0)
    c.calibrate()
    c.sync_from_robot(0.0, 0.0, 0.0, 0.0)
    return c


def run(c: TeleopController, alpha: float, steps: int, start: float = 0.0) -> dict:
    target = None
    for i in range(steps):
        t = start + (i + 1) * DT
        c.on_sample(alpha, B0, G0, held=True, now=t)
        target = c.tick(t, DT)
    return target


def yaw_of(target: dict) -> float:
    m = np.array(target["target_head_pose"]["m"]).reshape(4, 4)
    return pm.matrix_to_rpy(m[:3, :3])[2]


def test_landscape_sample_calibrates():
    c = TeleopController()
    c.on_sample(A0, B0, G0, held=False, now=0.0)
    c.calibrate()


def test_no_output_until_calibrated_and_synced():
    c = TeleopController()
    c.on_sample(A0, B0, G0, held=True, now=0.0)
    assert c.tick(0.0, DT) is None  # not calibrated
    c.calibrate()
    assert c.tick(0.0, DT) is None  # not synced to the robot yet
    c.sync_from_robot(0, 0, 0, 0)
    assert c.tick(0.0, DT) is not None


def test_deadman_release_and_timeout_stop_output_and_force_resync():
    c = engaged()
    c.on_sample(A0, B0, G0, held=False, now=0.1)
    assert c.tick(0.1, DT) is None and not c.synced
    c = engaged()
    assert c.tick(0.0 + c.settings.deadman_timeout_s + 0.01, DT) is None


def test_speed_cap_limits_step():
    # Right after engaging the catch-up speed (60 deg/s) applies; once within 3 deg, max_speed.
    c = engaged(smoothing=1.0, max_speed_deg_s=90.0)
    c.on_sample(A0 + 60.0, B0, G0, held=True, now=DT)
    target = c.tick(DT, DT)
    assert math.degrees(yaw_of(target)) == pytest.approx(60.0 * DT, abs=1e-6)
    c = engaged(smoothing=1.0, max_speed_deg_s=90.0)
    c.tick(DT, DT)  # target == cmd: caught up
    assert c.caught_up
    c.on_sample(A0 + 60.0, B0, G0, held=True, now=2 * DT)
    target = c.tick(2 * DT, DT)
    assert math.degrees(yaw_of(target)) == pytest.approx(90.0 * DT, abs=1e-6)


def test_follows_yaw_and_body_takes_over_beyond_follow_angle():
    c = engaged(smoothing=1.0, max_speed_deg_s=1000.0, catchup_speed_deg_s=1000.0, body_follow_deg=55.0)
    target = run(c, A0 + 100.0, 50)
    assert math.degrees(yaw_of(target)) == pytest.approx(100.0, abs=1e-4)
    assert math.degrees(target["target_body_yaw"]) == pytest.approx(45.0, abs=1e-4)


def test_pitch_is_clamped_to_40_deg():
    c = engaged(smoothing=1.0, max_speed_deg_s=1000.0)
    for i in range(50):
        t = (i + 1) * DT
        c.on_sample(A0, B0 - 70.0, G0, held=True, now=t)  # strong tilt
        target = c.tick(t, DT)
    m = np.array(target["target_head_pose"]["m"]).reshape(4, 4)
    roll, pitch, _ = pm.matrix_to_rpy(m[:3, :3])
    assert abs(math.degrees(pitch)) <= 40.0 + 1e-6
    assert abs(math.degrees(roll)) <= 40.0 + 1e-6


def test_head_body_delta_never_exceeds_65_deg():
    c = engaged(smoothing=1.0, max_speed_deg_s=1000.0)
    for alpha in np.linspace(0, 340, 120):
        t = c.latest_rx + DT
        c.on_sample(A0 + alpha, B0, G0, held=True, now=t)
        target = c.tick(t, DT)
        delta = math.degrees(yaw_of(target) - target["target_body_yaw"])
        delta = (delta + 180) % 360 - 180
        assert abs(delta) <= 65.0 + 1e-6
        assert abs(math.degrees(target["target_body_yaw"])) <= 160.0 + 1e-6


def test_settings_validation():
    with pytest.raises(ValueError):
        TeleopSettings(body_follow_deg=70.0)
    with pytest.raises(ValueError):
        TeleopSettings(smoothing=0.0)


def test_non_finite_sample_rejected():
    c = TeleopController()
    with pytest.raises(ValueError):
        c.on_sample(float("nan"), 0, 0, held=True, now=0.0)
