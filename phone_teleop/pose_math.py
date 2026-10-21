"""Phone orientation -> Reachy head pose math.

Frames:
- Earth (W3C DeviceOrientation): X east, Y north, Z up.
- Device (W3C): x right of the screen, y top of the screen (natural portrait),
  z out of the screen towards the viewer. The back camera looks along -z.
- Robot (FLU, same as the Quest app's CoordinateFrameUtil output):
  x forward, y left, z up.

A DeviceOrientation sample (alpha, beta, gamma) is the intrinsic Z-X'-Y''
rotation R that maps device coordinates to earth coordinates.

Calibration stores a neutral sample R0 and a "levelled" robot-world frame W0:
z = earth up, x = horizontal heading of the phone's viewing direction (-z of
the device). The head rotation in robot coordinates is then the earth-frame
rotation delta R(t) R0^T expressed in W0:

    head = W0^T  R(t) R0^T  W0

which is identity at the calibrated pose, turns left (+yaw) when the viewer
turns left about gravity, and works for portrait or either landscape grip.
"""

from __future__ import annotations

import math

import numpy as np
from scipy.spatial.transform import Rotation as R

# Below this horizontal component the viewing direction is ~vertical (phone
# lying flat), so the heading is undefined and calibration is refused.
MIN_HEADING_NORM = math.sin(math.radians(20.0))


class CalibrationError(ValueError):
    pass


def device_rotation(alpha_deg: float, beta_deg: float, gamma_deg: float) -> np.ndarray:
    """3x3 device->earth rotation for a W3C DeviceOrientation sample."""
    return R.from_euler("ZXY", [alpha_deg, beta_deg, gamma_deg], degrees=True).as_matrix()


def levelled_frame(neutral: np.ndarray) -> np.ndarray:
    """Robot-world frame W0 (columns = robot x, y, z in earth coords)."""
    forward = neutral @ np.array([0.0, 0.0, -1.0])
    horizontal = np.array([forward[0], forward[1], 0.0])
    norm = float(np.linalg.norm(horizontal))
    if norm < MIN_HEADING_NORM:
        raise CalibrationError("Hold the phone upright (screen facing you) to calibrate.")
    x = horizontal / norm
    z = np.array([0.0, 0.0, 1.0])
    y = np.cross(z, x)
    return np.column_stack([x, y, z])


class Calibration:
    def __init__(self, neutral: np.ndarray):
        self.neutral = neutral
        self.frame = levelled_frame(neutral)

    @classmethod
    def from_sample(cls, alpha: float, beta: float, gamma: float) -> "Calibration":
        return cls(device_rotation(alpha, beta, gamma))

    def head_rotation(self, current: np.ndarray) -> np.ndarray:
        w = self.frame
        return w.T @ current @ self.neutral.T @ w


def matrix_to_rpy(rotation: np.ndarray) -> tuple[float, float, float]:
    """(roll, pitch, yaw) in radians, extrinsic xyz: same convention as the daemon and enforce()."""
    roll, pitch, yaw = R.from_matrix(rotation).as_euler("xyz")
    return float(roll), float(pitch), float(yaw)


def rpy_to_matrix(roll: float, pitch: float, yaw: float) -> np.ndarray:
    return R.from_euler("xyz", [roll, pitch, yaw]).as_matrix()


def unwrap_angle(angle: float, reference: float) -> float:
    """Return angle + 2*pi*k closest to reference, so yaw stays continuous past +-180 deg."""
    return reference + (angle - reference + math.pi) % (2.0 * math.pi) - math.pi


def pose_matrix(rotation: np.ndarray, translation=(0.0, 0.0, 0.0)) -> np.ndarray:
    """4x4 head pose. Teleop is rotation-only: translation is the daemon's neutral (0,0,0),
    except while ramping there from wherever the robot was at sync (e.g. the sleep pose)."""
    pose = np.eye(4)
    pose[:3, :3] = rotation
    pose[:3, 3] = translation
    return pose


def flatten_row_major(pose: np.ndarray) -> list[float]:
    """Daemon Matrix4x4Pose order, same as ReachyDaemonTargetAdapter.FlattenRowMajor."""
    return [float(v) for v in np.asarray(pose, dtype=float).reshape(16)]


def full_body_target(pose: np.ndarray, body_yaw: float | None) -> dict:
    """FullBodyTarget JSON for ws://<robot>:8000/api/move/ws/set_target."""
    target: dict = {"target_head_pose": {"m": flatten_row_major(pose)}}
    if body_yaw is not None:
        target["target_body_yaw"] = float(body_yaw)
    return target
