"""Physical orientation conventions of pose_math, fed with W3C-canonical angles.

Each scenario is built physically (device axes in earth coordinates), converted
to the (alpha, beta, gamma) a browser would report (alpha in [0, 360), beta in
[-180, 180), gamma in [-90, 90)), and run through device_rotation + Calibration.
"""

import json
import math
import warnings

import numpy as np
import pytest
from scipy.spatial.transform import Rotation as R

from phone_teleop import pose_math as pm

UP = np.array([0.0, 0.0, 1.0])


def w3c_angles(device_to_earth: np.ndarray) -> tuple[float, float, float]:
    """(alpha, beta, gamma) in the W3C ranges for a device->earth rotation."""
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", UserWarning)  # gimbal lock at beta = 90 is expected
        a, b, g = R.from_matrix(device_to_earth).as_euler("ZXY", degrees=True)
    # scipy gives beta in [-90, 90]; W3C wants gamma in [-90, 90) instead.
    if g >= 90.0 or g < -90.0:
        a, b, g = a + 180.0, 180.0 - b, g - math.copysign(180.0, g)
    a = a % 360.0
    b = (b + 180.0) % 360.0 - 180.0
    assert -90.0 <= g < 90.0 + 1e-9
    return float(a), float(b), float(g)


def device_pose(grip: str, heading_deg: float = 0.0) -> np.ndarray:
    """Phone held upright in front of the eyes, screen facing the viewer.

    grip: 'portrait' (top up), 'landscape_x_up' (device +x = right edge up,
    i.e. top to the left), 'landscape_x_down' (top to the right).
    heading_deg: viewing direction, 0 = north, +h = turned left (CCW from above).
    """
    h = math.radians(heading_deg)
    view = np.array([-math.sin(h), math.cos(h), 0.0])
    z = -view  # screen normal points at the viewer, camera looks along -z
    device_up = {"portrait": "y", "landscape_x_up": "x", "landscape_x_down": "-x"}[grip]
    if device_up == "y":
        y = UP
        x = np.cross(y, z)
    elif device_up == "x":
        x = UP
        y = np.cross(z, x)
    else:
        x = -UP
        y = np.cross(z, x)
    m = np.column_stack([x, y, z])
    assert np.allclose(m.T @ m, np.eye(3)) and np.linalg.det(m) > 0
    return m


def viewer_turn(device: np.ndarray, yaw=0.0, pitch=0.0, roll=0.0, heading_deg=0.0) -> np.ndarray:
    """Apply a head motion (FLU yaw left, pitch down, roll = left side up) to the device."""
    h = math.radians(heading_deg)
    fwd = np.array([-math.sin(h), math.cos(h), 0.0])
    left = np.cross(UP, fwd)
    # Head motion in the viewer's levelled frame: Rz(yaw) Ry(pitch) Rx(roll) in (fwd, left, up).
    w = np.column_stack([fwd, left, UP])
    head = R.from_euler("xyz", [roll, pitch, yaw], degrees=True).as_matrix()
    return w @ head @ w.T @ device


def head_rpy_deg(neutral: np.ndarray, current: np.ndarray) -> np.ndarray:
    cal = pm.Calibration.from_sample(*w3c_angles(neutral))
    rot = cal.head_rotation(pm.device_rotation(*w3c_angles(current)))
    return np.degrees(pm.matrix_to_rpy(rot))


GRIPS = ["portrait", "landscape_x_up", "landscape_x_down"]


def test_w3c_helper_round_trips():
    for q in R.random(200, random_state=1):
        m = q.as_matrix()
        np.testing.assert_allclose(pm.device_rotation(*w3c_angles(m)), m, atol=1e-9)


def test_known_w3c_samples_mean_what_we_think():
    # Portrait upright, screen facing the viewer: beta = 90.
    np.testing.assert_allclose(pm.device_rotation(0, 90, 0) @ [0, 1, 0], UP, atol=1e-12)
    # Landscape with gamma = -90: device +x points up.
    np.testing.assert_allclose(pm.device_rotation(0, 0, -90) @ [1, 0, 0], UP, atol=1e-12)
    np.testing.assert_allclose(pm.device_rotation(0, 0, 90) @ [1, 0, 0], -UP, atol=1e-12)


@pytest.mark.parametrize("grip", GRIPS)
@pytest.mark.parametrize("heading", [0.0, 90.0, -135.0, 179.0])
def test_calibrated_pose_is_identity_for_all_grips(grip, heading):
    d = device_pose(grip, heading)
    np.testing.assert_allclose(head_rpy_deg(d, d), [0, 0, 0], atol=1e-6)


@pytest.mark.parametrize("grip", GRIPS)
@pytest.mark.parametrize("heading", [0.0, 77.0, -170.0])
@pytest.mark.parametrize(
    "motion, expected",
    [
        (dict(yaw=30.0), [0, 0, 30]),       # turn left -> +yaw
        (dict(yaw=-50.0), [0, 0, -50]),     # turn right -> -yaw
        (dict(pitch=25.0), [0, 25, 0]),     # look down -> +pitch (FLU)
        (dict(pitch=-25.0), [0, -25, 0]),   # look up -> -pitch
        (dict(roll=20.0), [20, 0, 0]),      # left side up (right ear down) -> +roll
        (dict(yaw=40.0, pitch=15.0, roll=-10.0), [-10, 15, 40]),
    ],
)
def test_head_motion_maps_to_flu_rpy(grip, heading, motion, expected):
    d0 = device_pose(grip, heading)
    d1 = viewer_turn(d0, heading_deg=heading, **motion)
    np.testing.assert_allclose(head_rpy_deg(d0, d1), expected, atol=1e-6)


def test_turn_left_increases_alpha_and_yaw():
    # Independent of the helpers: raw W3C landscape sample, alpha +30 (CCW from above).
    cal = pm.Calibration.from_sample(10.0, 0.0, -90.0)
    r, p, y = np.degrees(pm.matrix_to_rpy(cal.head_rotation(pm.device_rotation(40.0, 0.0, -90.0))))
    assert y == pytest.approx(30.0) and r == pytest.approx(0, abs=1e-9) and p == pytest.approx(0, abs=1e-9)


def test_portrait_beta_below_90_is_looking_down():
    cal = pm.Calibration.from_sample(0.0, 90.0, 0.0)
    _, p, _ = np.degrees(pm.matrix_to_rpy(cal.head_rotation(pm.device_rotation(0.0, 70.0, 0.0))))
    assert p == pytest.approx(20.0)


@pytest.mark.parametrize("gamma0", [-90.0 + 1e-6, 89.999])
def test_landscape_tilt_across_gamma_wrap_is_continuous(gamma0):
    """Looking up from landscape makes gamma wrap -90 -> +90 with alpha/beta flipping by 180."""
    d0 = device_pose("landscape_x_up" if gamma0 < 0 else "landscape_x_down", 0.0)
    prev = None
    for pitch in np.linspace(-30, 30, 241):
        d = viewer_turn(d0, pitch=pitch)
        rpy = head_rpy_deg(d0, d)
        np.testing.assert_allclose(rpy, [0, pitch, 0], atol=1e-6)
        if prev is not None:
            assert np.max(np.abs(rpy - prev)) < 1.0
        prev = rpy


def test_portrait_singularity_beta_90_is_continuous():
    """At beta=90 alpha and gamma are degenerate; the physical pose must still map smoothly."""
    d0 = device_pose("portrait", 0.0)
    a, b, g = w3c_angles(d0)
    assert b == pytest.approx(90.0)
    # Same physical pose, different (alpha, gamma) splits a browser could report at the singularity.
    matched = 0
    for k in [-60.0, -10.0, 25.0, 80.0]:
        for sign in (1.0, -1.0):
            m = pm.device_rotation(a + k, 90.0, g + sign * k)
            if np.allclose(m, d0, atol=1e-9):
                matched += 1
                cal = pm.Calibration(d0)
                np.testing.assert_allclose(cal.head_rotation(m), np.eye(3), atol=1e-9)
    assert matched == 4
    prev = None
    for pitch in np.linspace(-10, 10, 201):  # sweep through beta = 90
        d = viewer_turn(d0, pitch=pitch, yaw=pitch * 0.5)
        rpy = head_rpy_deg(d0, d)
        np.testing.assert_allclose(rpy, [0, pitch, pitch * 0.5], atol=1e-6)
        if prev is not None:
            assert np.max(np.abs(rpy - prev)) < 0.5
        prev = rpy


def test_calibration_on_beta_90_exact_sample():
    cal = pm.Calibration.from_sample(123.0, 90.0, 45.0)
    np.testing.assert_allclose(cal.head_rotation(pm.device_rotation(123.0, 90.0, 45.0)), np.eye(3), atol=1e-9)


@pytest.mark.parametrize("beta, gamma", [(0, 0), (180, 0), (-179.9, 0), (10, 5), (15, 0), (0, -15)])
def test_flat_phone_refuses_calibration(beta, gamma):
    with pytest.raises(pm.CalibrationError):
        pm.Calibration.from_sample(30.0, beta, gamma)


@pytest.mark.parametrize("beta, gamma", [(25, 0), (0, -30), (90, 0), (0, 89.9), (-60, 0)])
def test_tilted_phone_calibrates(beta, gamma):
    pm.Calibration.from_sample(30.0, beta, gamma)


def test_calibration_heading_ignores_phone_tilt():
    """Calibrating while looking 30 deg down: neutral stays the levelled frame, so pitch is relative."""
    d_level = device_pose("landscape_x_up", 20.0)
    d_down = viewer_turn(d_level, pitch=30.0, heading_deg=20.0)
    # Calibrated while looking down; returning to level must read as looking up 30 deg.
    np.testing.assert_allclose(head_rpy_deg(d_down, d_level), [0, -30, 0], atol=1e-6)


def test_full_body_target_round_trips_through_daemon_models():
    from reachy_mini.daemon.app.models import FullBodyTarget, XYZRPYPose

    rng = np.random.default_rng(3)
    for _ in range(50):
        rpy = rng.uniform([-0.7, -0.7, -3.1], [0.7, 0.7, 3.1])
        pose = pm.pose_matrix(pm.rpy_to_matrix(*rpy))
        payload = json.dumps(pm.full_body_target(pose, float(rng.uniform(-2.7, 2.7))))
        target = FullBodyTarget.model_validate_json(payload)  # exactly what the ws route does
        arr = target.target_head_pose.to_pose_array()
        np.testing.assert_allclose(arr, pose, atol=1e-12)
        back = XYZRPYPose.from_pose_array(arr)
        np.testing.assert_allclose([back.roll, back.pitch, back.yaw], rpy, atol=1e-9)
        assert back.x == back.y == back.z == 0.0
    no_body = FullBodyTarget.model_validate_json(json.dumps(pm.full_body_target(np.eye(4), None)))
    assert no_body.target_body_yaw is None


def test_matrix_to_rpy_matches_daemon_convention():
    from reachy_mini.daemon.app.models import XYZRPYPose

    p = XYZRPYPose(roll=0.2, pitch=-0.3, yaw=1.1)
    assert pm.matrix_to_rpy(p.to_pose_array()[:3, :3]) == pytest.approx((0.2, -0.3, 1.1))
