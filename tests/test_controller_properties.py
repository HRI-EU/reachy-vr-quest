"""Property tests for TeleopController: safety limits and speed cap on every output."""

import math

import numpy as np
import pytest

from phone_teleop import pose_math as pm
from phone_teleop.teleop import TeleopController, TeleopSettings

DT = 0.02
EPS = 1e-6
A0, B0, G0 = 0.0, 0.0, -90.0  # landscape upright


def out_rpy_body(target: dict) -> tuple[np.ndarray, float | None]:
    m = np.array(target["target_head_pose"]["m"], dtype=float).reshape(4, 4)
    assert np.all(np.isfinite(m))
    np.testing.assert_allclose(m[3], [0, 0, 0, 1])
    np.testing.assert_allclose(m[:3, 3], [0, 0, 0])
    rot = m[:3, :3]
    np.testing.assert_allclose(rot @ rot.T, np.eye(3), atol=1e-9)
    return np.array(pm.matrix_to_rpy(rot)), target.get("target_body_yaw")


def wrap(a: float) -> float:
    return (a + math.pi) % (2 * math.pi) - math.pi


def check_safe(target: dict, body_ref: float) -> None:
    rpy, body = out_rpy_body(target)
    lim = math.radians(40.0) + EPS
    assert abs(rpy[0]) <= lim and abs(rpy[1]) <= lim, np.degrees(rpy)
    b = body if body is not None else body_ref
    assert abs(b) <= math.radians(160.0) + EPS
    assert abs(rpy[2] - b) <= math.radians(65.0) + EPS, (math.degrees(rpy[2]), math.degrees(b))
    assert abs(wrap(rpy[2] - b)) <= math.radians(65.0) + EPS


class Sim:
    """Mirrors control_loop: sync on engage from a robot that tracks the last output perfectly."""

    def __init__(self, settings: TeleopSettings, robot=(0.0, 0.0, 0.0, 0.0)):
        self.c = TeleopController(settings)
        self.robot = list(robot)  # roll, pitch, yaw, body (rad)
        self.t = 0.0
        self.outputs: list[tuple[float, dict, bool]] = []  # (t, target, first_after_sync)

    def sample(self, a, b, g, held):
        self.c.on_sample(a, b, g, held, self.t)

    def tick(self, dt=DT):
        self.t += dt
        first = False
        if self.c.is_engaged(self.t) and not self.c.synced:
            self.c.sync_from_robot(*self.robot)
            first = True
        target = self.c.tick(self.t, dt)
        if target is not None:
            check_safe(target, self.robot[3])
            rpy, body = out_rpy_body(target)
            prev = self.robot.copy()
            self.robot = [*rpy, body if body is not None else self.robot[3]]
            self.outputs.append((dt, target, first, prev))
        return target


def check_speed(sim: Sim, cap_deg_s: float, include_first=True) -> None:
    for dt, target, first, prev in sim.outputs:
        if first and not include_first:
            continue
        rpy, body = out_rpy_body(target)
        lim = math.radians(cap_deg_s) * dt + 1e-6
        d = [rpy[0] - prev[0], rpy[1] - prev[1], wrap(rpy[2] - prev[2])]
        assert max(abs(x) for x in d) <= lim, (np.degrees(d), "first" if first else "")
        if body is not None:
            assert abs(body - prev[3]) <= lim, (math.degrees(body - prev[3]), "first" if first else "")


def calibrated_sim(settings=None, robot=(0.0, 0.0, 0.0, 0.0), **kw) -> Sim:
    sim = Sim(settings or TeleopSettings(**kw), robot)
    sim.sample(A0, B0, G0, False)
    sim.c.calibrate()
    return sim


# --- gating -----------------------------------------------------------------

def test_no_output_without_calibration_even_if_held():
    sim = Sim(TeleopSettings())
    for i in range(20):
        sim.sample(A0 + i, B0, G0, True)
        assert sim.tick() is None
    assert not sim.c.synced


def test_no_output_when_not_held():
    sim = calibrated_sim()
    for i in range(20):
        sim.sample(A0 + i, B0, G0, False)
        assert sim.tick() is None


def test_deadman_timeout_boundary():
    s = TeleopSettings(deadman_timeout_s=0.3)
    c = TeleopController(s)
    c.on_sample(A0, B0, G0, True, 10.0)
    c.calibrate()
    c.sync_from_robot(0, 0, 0, 0)
    assert c.tick(10.29, DT) is not None
    assert c.tick(10.31, DT) is None
    assert not c.synced  # forces a resync
    c.on_sample(A0, B0, G0, True, 10.32)
    assert c.tick(10.33, DT) is None  # must wait for a fresh sync
    c.sync_from_robot(0.1, 0, 0.2, 0)
    assert c.tick(10.34, DT) is not None


def test_release_forces_resync_from_robot():
    sim = calibrated_sim(smoothing=1.0, max_speed_deg_s=1e5, catchup_speed_deg_s=1e5)
    for _ in range(10):
        sim.sample(A0 + 30, B0, G0, True)
        sim.tick()
    sim.c.release()
    assert sim.tick() is None and not sim.c.synced
    # Someone moved the robot meanwhile; the next engage starts from there.
    sim.robot = [0.0, 0.0, math.radians(-20.0), 0.0]
    sim.sample(A0 + 30, B0, G0, True)
    out = sim.tick()
    assert sim.outputs[-1][2]  # was a fresh sync
    assert sim.outputs[-1][3][2] == pytest.approx(math.radians(-20.0))
    rpy, _ = out_rpy_body(out)
    # smoothing=1 and big cap: goes to the phone target, but only after starting from -20
    assert math.degrees(rpy[2]) == pytest.approx(30.0, abs=1e-6)


def test_engage_starts_from_robot_pose():
    sim = calibrated_sim(smoothing=1.0, max_speed_deg_s=90.0, robot=(0.1, -0.2, 0.5, 0.2))
    sim.sample(A0, B0, G0, True)
    out = sim.tick()
    rpy, body = out_rpy_body(out)
    step = math.radians(60.0) * DT  # catch-up speed until within 3 deg
    np.testing.assert_allclose(rpy, [0.1 - step, -0.2 + step, 0.5 - step], atol=1e-9)
    assert body == pytest.approx(0.2)


def test_smoothing_applies_fraction_of_error():
    sim = calibrated_sim(smoothing=0.25, max_speed_deg_s=10000.0, catchup_speed_deg_s=10000.0)
    sim.sample(A0 + 40, B0, G0, True)
    out = sim.tick()
    assert math.degrees(out_rpy_body(out)[0][2]) == pytest.approx(10.0)
    out = sim.tick()  # sample still fresh, same target
    assert math.degrees(out_rpy_body(out)[0][2]) == pytest.approx(17.5)


def test_speed_cap_scales_with_dt():
    sim = calibrated_sim(smoothing=1.0, max_speed_deg_s=100.0)
    sim.sample(A0 + 90, B0, G0, True)
    sim.tick(0.01)
    sim.tick(0.03)
    yaws = [math.degrees(out_rpy_body(o[1])[0][2]) for o in sim.outputs]
    assert yaws == pytest.approx([0.6, 2.4])  # catch-up 60 deg/s
    sim = calibrated_sim(smoothing=1.0, max_speed_deg_s=100.0, catchup_speed_deg_s=100.0)
    sim.sample(A0 + 90, B0, G0, True)
    sim.tick(0.01)
    sim.tick(0.03)
    yaws = [math.degrees(out_rpy_body(o[1])[0][2]) for o in sim.outputs]
    assert yaws == pytest.approx([1.0, 4.0])


def test_negative_dt_does_not_move():
    sim = calibrated_sim(smoothing=1.0)
    sim.sample(A0 + 90, B0, G0, True)
    out = sim.tick(-0.5)
    assert out_rpy_body(out)[0] == pytest.approx([0, 0, 0], abs=1e-12)


# --- yaw wrap ------------------------------------------------------------------

def test_yaw_wrap_past_180_has_no_jump():
    """Robot at 170 deg, phone keeps turning left past 180: output saturates, never flips to -180."""
    s = TeleopSettings(smoothing=1.0, max_speed_deg_s=180.0, body_follow=True)
    sim = calibrated_sim(s, robot=(0.0, 0.0, math.radians(170.0), math.radians(120.0)))
    alphas = list(np.linspace(170, 260, 60)) + list(np.linspace(260, 100, 120))
    for a in alphas:
        sim.sample(A0 + a, B0, G0, True)
        sim.tick()
    check_speed(sim, 180.0)
    yaws = [math.degrees(out_rpy_body(o[1])[0][2]) for o in sim.outputs]
    assert max(yaws) <= 179.0 + 1e-6
    assert min(yaws[:60]) > 160.0


def test_yaw_wrap_from_negative_side():
    s = TeleopSettings(smoothing=0.5, max_speed_deg_s=180.0)
    sim = calibrated_sim(s, robot=(0.0, 0.0, math.radians(-175.0), math.radians(-130.0)))
    for a in np.linspace(-175, -260, 80):
        sim.sample(A0 + a, B0, G0, True)
        sim.tick()
    check_speed(sim, 180.0)
    yaws = [math.degrees(out_rpy_body(o[1])[0][2]) for o in sim.outputs]
    assert min(yaws) >= -179.0 - 1e-6 and max(yaws) < -150.0


# --- body follow -------------------------------------------------------------

def test_body_follows_only_beyond_follow_angle_and_stays_in_limits():
    sim = calibrated_sim(smoothing=1.0, max_speed_deg_s=1000.0, body_follow_deg=55.0)
    for a in np.linspace(0, 50, 20):
        sim.sample(A0 + a, B0, G0, True)
        out = sim.tick()
        assert out["target_body_yaw"] == pytest.approx(0.0)
    for a in np.linspace(50, 179, 60):
        sim.sample(A0 + a, B0, G0, True)
        sim.tick()
    rpy, body = out_rpy_body(sim.outputs[-1][1])
    assert math.degrees(rpy[2]) == pytest.approx(179.0, abs=1e-6)
    assert math.degrees(body) == pytest.approx(124.0, abs=1e-6)


def test_no_body_mode_never_sends_body_and_clamps_head_to_65():
    sim = calibrated_sim(smoothing=1.0, max_speed_deg_s=1000.0, body_follow=False,
                         robot=(0, 0, 0, math.radians(10.0)))
    for a in np.linspace(0, 150, 50):
        sim.sample(A0 + a, B0, G0, True)
        out = sim.tick()
        assert "target_body_yaw" not in out
    rpy, _ = out_rpy_body(out)
    assert math.degrees(rpy[2]) == pytest.approx(75.0, abs=1e-6)
    assert sim.c.status(sim.t).limited


def test_engage_with_robot_head_body_delta_near_limit_does_not_jump_body():
    """Robot present head-vs-body delta 60 deg (legal, < 65): first frame must not jerk the body."""
    sim = calibrated_sim(smoothing=0.5, max_speed_deg_s=180.0,
                         robot=(0.0, 0.0, math.radians(60.0), 0.0))
    sim.sample(A0 + 60, B0, G0, True)  # phone already where the robot looks
    for _ in range(5):
        sim.tick()
    check_speed(sim, 180.0, include_first=True)


# --- random and adversarial sequences ----------------------------------------

def _random_run(seed: int, settings: TeleopSettings):
    rng = np.random.default_rng(seed)
    robot = (rng.uniform(-0.6, 0.6), rng.uniform(-0.6, 0.6), rng.uniform(-3.1, 3.1), 0.0)
    robot = (*robot[:3], float(np.clip(robot[2] + rng.uniform(-0.9, 0.9), -2.79, 2.79)))
    sim = Sim(settings, robot)
    a, b, g = rng.uniform(0, 360), rng.uniform(-60, 60), rng.uniform(-89, 89)
    sim.sample(a, b, g, False)
    try:
        sim.c.calibrate()
    except pm.CalibrationError:
        sim.sample(A0, B0, G0, False)
        sim.c.calibrate()
    held = True
    for _ in range(600):
        mode = rng.random()
        if mode < 0.02:
            held = not held
        if mode < 0.05:  # teleport (adversarial)
            a, b, g = rng.uniform(0, 360), rng.uniform(-180, 180), rng.uniform(-90, 90)
        else:
            a += rng.normal(0, 8)
            b += rng.normal(0, 4)
            g += rng.normal(0, 4)
            # keep in W3C ranges like a browser would
            a %= 360.0
            b = (b + 180) % 360 - 180
            g = float(np.clip(g, -90, 89.999))
        if rng.random() > 0.05:  # occasional missing samples
            sim.sample(a, b, g, held)
        dt = float(rng.choice([DT, DT, DT, 0.005, 0.05, 0.4]))
        sim.tick(dt)
    return sim


@pytest.mark.parametrize("seed", range(25))
@pytest.mark.parametrize("body_follow", [True, False])
def test_random_sequences_respect_limits_and_speed_cap(seed, body_follow):
    s = TeleopSettings(smoothing=float(np.random.default_rng(seed).uniform(0.1, 1.0)),
                       max_speed_deg_s=180.0, body_follow=body_follow)
    sim = _random_run(seed, s)
    assert sim.outputs, "expected some output"
    # Robot present pose comes from our own outputs (delta <= 55), so even
    # the first frame after a resync must respect the cap.
    check_speed(sim, s.max_speed_deg_s, include_first=True)


def test_adversarial_flip_flop_samples():
    """Alternating opposite orientations every sample: output still moves at most cap*dt."""
    sim = calibrated_sim(smoothing=1.0, max_speed_deg_s=180.0)
    for i in range(300):
        if i % 2:
            sim.sample(A0 + 179.9, B0, G0, True)
        else:
            sim.sample(A0 - 179.9, B0 + 89.9, G0 + 179.0, True)
        sim.tick()
    check_speed(sim, 180.0)


def test_extreme_tilt_saturates():
    sim = calibrated_sim(smoothing=1.0, max_speed_deg_s=1000.0)
    for i in range(100):
        sim.sample(A0, B0, G0 + 75.0, True)  # look 75 deg down
        sim.tick()
    rpy, _ = out_rpy_body(sim.outputs[-1][1])
    assert math.degrees(rpy[1]) == pytest.approx(40.0)


def test_looking_up_past_vertical_does_not_spin_head_around():
    """Tilting back slightly past straight up (pitch -90) is a euler singularity:
    the decomposition flips to roll=180, yaw=180. The head should saturate
    pitch, not swing yaw 180 deg and roll to the limit."""
    sim = calibrated_sim(smoothing=1.0, max_speed_deg_s=180.0)
    # Landscape (gamma=-90 is upright, camera east); gamma = -90 - d tilts the view up by d.
    for deg in list(np.linspace(0, 100, 50)) + [100.0] * 100:
        sim.sample(A0, B0, G0 - deg, True)
        sim.tick()
    rpy, _ = out_rpy_body(sim.outputs[-1][1])
    assert abs(math.degrees(rpy[2])) < 10.0, np.degrees(rpy)
    assert abs(math.degrees(rpy[0])) < 10.0, np.degrees(rpy)


# --- garbage -----------------------------------------------------------------

@pytest.mark.parametrize("bad", [float("nan"), float("inf"), -float("inf")])
def test_non_finite_values_rejected_and_state_kept(bad):
    c = TeleopController()
    c.on_sample(A0, B0, G0, True, 0.0)
    before = c.latest.copy()
    for args in [(bad, 0, 0), (0, bad, 0), (0, 0, bad)]:
        with pytest.raises(ValueError):
            c.on_sample(*args, held=True, now=0.1)
    np.testing.assert_array_equal(c.latest, before)
    assert c.latest_rx == 0.0


@pytest.mark.parametrize("bad", [None, "abc", [1], {"x": 1}])
def test_garbage_types_raise_type_or_value_error(bad):
    c = TeleopController()
    with pytest.raises((TypeError, ValueError)):
        c.on_sample(bad, 0, 0, held=True, now=0.0)


def test_huge_finite_angles_still_safe():
    sim = calibrated_sim(smoothing=1.0, max_speed_deg_s=180.0)
    for v in [1e9, -1e12, 1e300, 7e5]:
        sim.sample(v, v, v, True)
        sim.tick()
    check_speed(sim, 180.0)


def test_calibrate_without_data_raises():
    with pytest.raises(pm.CalibrationError):
        TeleopController().calibrate()


def test_status_values_are_json_friendly():
    import json
    sim = calibrated_sim()
    sim.sample(A0 + 10, B0, G0, True)
    sim.tick()
    from dataclasses import asdict
    st = asdict(sim.c.status(sim.t))
    json.dumps(st)
    assert st["calibrated"] and st["engaged"] and st["synced"]
