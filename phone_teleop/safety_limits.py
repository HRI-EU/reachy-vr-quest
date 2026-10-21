"""
Vendored from ~/EEPY/robot/safety_limits.py so this repo runs on its own.
The logic is identical (only this note was added). Keep the two in sync.

Single source of truth for Reachy Mini's physical safety limits. Any script
that sends a head pose and/or body yaw to the robot should funnel its final
values through enforce() before sending anything, rather than
re-implementing (or, easier to miss, forgetting) these checks itself.

Limits confirmed directly against the Reachy Mini SDK's own safety-limit
tables (docs/source/SDK/core-concept.md and AGENTS.md, both agree):
    Head pitch/roll:        +-40 deg
    Head yaw:                +-180 deg
    Body yaw:                +-160 deg
    Head-vs-body yaw delta:  max 65 deg difference

Antennas are a different case: not in either safety-limit table (they're
"also usable as physical buttons", not a safety-relevant joint the same
way), so this pins the number to the robot's own URDF instead
(descriptions/reachy_mini/urdf/robot.urdf, right_antenna/left_antenna
joints: limit lower="-3.141592653589793" upper="3.141592653589793"), a
full +-180 deg swing, the joint's real mechanical range, not a guess.

The yaw-delta limit is the one most easily missed: it's a constraint
BETWEEN two joints, not a single-joint range, so clamping the head pose in
isolation (ignoring where the body currently is) can't enforce it.

No reachy_mini import here (numpy/scipy only), so this module can be
imported from either the robot/ venv or the vision/ venv (see the
README's "why two environments" note) without pulling in the SDK.
"""

import numpy as np
from scipy.spatial.transform import Rotation as R

HEAD_PITCH_ROLL_LIMIT_DEG = 40.0
HEAD_YAW_LIMIT_DEG = 180.0
BODY_YAW_LIMIT_DEG = 160.0
HEAD_BODY_YAW_DELTA_LIMIT_DEG = 65.0
ANTENNA_LIMIT_RAD = np.pi  # see the URDF citation above


def enforce(
    head_pose: np.ndarray,
    body_yaw_target: float | None,
    current_body_yaw: float,
) -> tuple[np.ndarray, float | None]:
    """Clamp a proposed head pose (+ optional body yaw target) to all four
    limits above, together, not as separate unrelated checks.

    Args:
        head_pose: 4x4 pose matrix, as sent to /api/move/set_target.
        body_yaw_target: body yaw to command this frame, in radians, or
            None if body yaw isn't being changed this frame.
        current_body_yaw: the body's presumed actual current yaw, in
            radians (the caller's own last-commanded value is fine, this
            doesn't have to be a fresh state query every frame), used for
            the head-vs-body delta check.

    Returns:
        (safe_head_pose, safe_body_yaw_target): a new 4x4 pose matrix and a
        body yaw value (or None, matching what was passed in), both
        guaranteed within all four limits above.
    """
    roll, pitch, yaw = R.from_matrix(head_pose[:3, :3]).as_euler("xyz")

    pr_limit = np.deg2rad(HEAD_PITCH_ROLL_LIMIT_DEG)
    roll = np.clip(roll, -pr_limit, pr_limit)
    pitch = np.clip(pitch, -pr_limit, pr_limit)

    yaw_limit = np.deg2rad(HEAD_YAW_LIMIT_DEG)
    yaw = np.clip(yaw, -yaw_limit, yaw_limit)

    # Cross-joint constraint: clamp yaw again, this time relative to
    # wherever the body actually is, not just the head's own absolute range.
    delta_limit = np.deg2rad(HEAD_BODY_YAW_DELTA_LIMIT_DEG)
    yaw = np.clip(yaw, current_body_yaw - delta_limit, current_body_yaw + delta_limit)

    safe_pose = np.eye(4)
    safe_pose[:3, 3] = head_pose[:3, 3]
    safe_pose[:3, :3] = R.from_euler("xyz", [roll, pitch, yaw]).as_matrix()

    safe_body_yaw = None
    if body_yaw_target is not None:
        body_limit = np.deg2rad(BODY_YAW_LIMIT_DEG)
        safe_body_yaw = float(np.clip(body_yaw_target, -body_limit, body_limit))

    return safe_pose, safe_body_yaw


def clamp_antennas(right_rad: float, left_rad: float) -> tuple[float, float]:
    """Clamp both antenna joints to their real mechanical range, see
    ANTENNA_LIMIT_RAD's comment for the URDF source.
    """
    return (
        float(np.clip(right_rad, -ANTENNA_LIMIT_RAD, ANTENNA_LIMIT_RAD)),
        float(np.clip(left_rad, -ANTENNA_LIMIT_RAD, ANTENNA_LIMIT_RAD)),
    )
