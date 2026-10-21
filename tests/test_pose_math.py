import math

import numpy as np
import pytest
from scipy.spatial.transform import Rotation as R

from phone_teleop import pose_math as pm

# Phone upright in landscape (device +x pointing up), screen facing the viewer,
# back camera looking north. In W3C angles: beta=0, gamma=-90 ... built directly
# from axes to avoid relying on an angle convention in the test itself.
def upright_landscape(heading_deg: float = 0.0) -> np.ndarray:
    # device axes in earth coords: x = up, y = west-ish (left of view), z = towards viewer (south)
    h = math.radians(heading_deg)
    view = np.array([-math.sin(h), math.cos(h), 0.0])  # heading 0 = north, +h = turn left (CCW)
    x = np.array([0.0, 0.0, 1.0])
    z = -view
    y = np.cross(z, x)
    return np.column_stack([x, y, z])


def test_device_rotation_matches_w3c_definition():
    a, b, g = 30.0, 20.0, -10.0
    rz = R.from_euler("z", a, degrees=True).as_matrix()
    rx = R.from_euler("x", b, degrees=True).as_matrix()
    ry = R.from_euler("y", g, degrees=True).as_matrix()
    np.testing.assert_allclose(pm.device_rotation(a, b, g), rz @ rx @ ry, atol=1e-12)


def test_calibrated_pose_is_identity():
    cal = pm.Calibration(upright_landscape(37.0))
    np.testing.assert_allclose(cal.head_rotation(upright_landscape(37.0)), np.eye(3), atol=1e-12)


def test_turning_left_gives_positive_yaw():
    cal = pm.Calibration(upright_landscape(0.0))
    roll, pitch, yaw = pm.matrix_to_rpy(cal.head_rotation(upright_landscape(25.0)))
    assert yaw == pytest.approx(math.radians(25.0))
    assert roll == pytest.approx(0.0, abs=1e-9)
    assert pitch == pytest.approx(0.0, abs=1e-9)


def test_looking_down_gives_positive_pitch():
    neutral = upright_landscape(0.0)
    # tilt the view down by 20 deg: rotate about the viewer's left axis (earth, at heading 0: -x = west)
    left = np.array([-1.0, 0.0, 0.0])
    down = R.from_rotvec(left * math.radians(20.0)).as_matrix()
    cal = pm.Calibration(neutral)
    roll, pitch, yaw = pm.matrix_to_rpy(cal.head_rotation(down @ neutral))
    assert pitch == pytest.approx(math.radians(20.0))  # FLU: +pitch = nose down
    assert yaw == pytest.approx(0.0, abs=1e-9)


def test_portrait_grip_calibrates_too():
    a, b, g = 0.0, 90.0 - 1e-3, 0.0  # upright portrait, near the beta=90 singularity
    cal = pm.Calibration.from_sample(a, b, g)
    np.testing.assert_allclose(cal.head_rotation(pm.device_rotation(a, b, g)), np.eye(3), atol=1e-9)


def test_flat_phone_refuses_calibration():
    with pytest.raises(pm.CalibrationError):
        pm.Calibration.from_sample(0.0, 0.0, 0.0)  # lying flat, screen up


def test_unwrap_angle():
    assert pm.unwrap_angle(math.radians(-170), math.radians(175)) == pytest.approx(math.radians(190))
    assert pm.unwrap_angle(0.3, 0.1) == pytest.approx(0.3)


def test_flatten_row_major_matches_quest_adapter_order():
    # Same check as ReachyDaemonTargetAdapterTests.FlattenRowMajor_UsesDaemonMatrixOrder
    m = np.eye(4)
    m[0, 1] = 2.0
    m[2, 3] = 5.0
    values = pm.flatten_row_major(m)
    assert values[1] == 2.0 and values[11] == 5.0


def test_full_body_target_is_valid_daemon_model():
    from reachy_mini.daemon.app.models import FullBodyTarget

    pose = pm.pose_matrix(pm.rpy_to_matrix(0.1, -0.2, 0.3))
    target = FullBodyTarget.model_validate(pm.full_body_target(pose, 0.25))
    np.testing.assert_allclose(target.target_head_pose.to_pose_array(), pose, atol=1e-9)
    assert target.target_body_yaw == pytest.approx(0.25)
