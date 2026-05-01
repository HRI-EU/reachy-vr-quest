# ReachyMiniTeleop Agent Guide

## Project Snapshot

- `ReachyMiniTeleop` is a clean open-source Unity split-out for Quest-to-Reachy Mini teleoperation.
- Unity version: `6000.3.9f1`.
- License: Apache-2.0.
- Core path: skeleton provider -> Reachy payload builder -> ZMQ DEALER -> Reachy Mini ROUTER bridge.

## Key Files

- `Assets/Scenes/ReachyMiniTeleop.unity`
  - Main demo scene.
  - `MockSkeletonProvider` is active by default for no-headset testing.
  - `QuestTrackingProvider_Template` is inactive and should be wired for real Quest tracking.

- `Assets/Config/ReachyTeleopConfig.asset`
  - Public default config: `tcp://localhost:40000`, identity `body`, 10 Hz, body yaw limit 45 degrees, antenna max 90 degrees.

- `Assets/Scripts/Runtime/Reachy`
  - `ReachyHeadCommandBuilder`: pure payload math and payload construction.
  - `ReachyHeadCommandPublisher`: Unity lifecycle, publish loop, JSON serialization.
  - `CoordinateFrameUtil`: Unity RUF -> robot FLU conversion.
  - `IReachySkeletonProvider`: interface for real and mock tracking.

- `Assets/Scripts/Runtime/Tracking/MetaBodySkeletonProvider.cs`
  - Meta XR Movement `MetaSourceDataProvider` wrapper plus OpenXR hand stitch.

- `Assets/Scripts/Runtime/Transport/ReachyZmqDealerClient.cs`
  - ZMQ DEALER transport.
  - Requires NetMQ from NuGetForUnity restore; missing NetMQ DLLs should fail compilation rather than running a fake transport.

- `Tools/mock_reachy_router.py`
  - Python ROUTER receiver for local testing.

## Protocol And Coordinates

- Unity sends DEALER multipart messages as:

```text
[empty frame, JSON payload]
```

- Payload shape:

```json
{
  "body_yaw_degrees": 0.0,
  "head_position": { "x": 0.0, "y": 0.0, "z": 0.0 },
  "head_rotation": { "x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0 },
  "antennas": { "right": 0.0, "left": 0.0 }
}
```

- Unity input is RUF: x right, y up, z forward.
- Robot output is FLU: x forward, y left, z up.
- Position conversion is `(x, y, z) -> (z, -x, y)`.
- Keep frame conversion in `CoordinateFrameUtil`; keep payload math in `ReachyHeadCommandBuilder`.

## Testing

- Editor tests are in `Assets/Tests/Editor`.
- Preferred batch command:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe' -batchmode -nographics -logFile 'C:\Users\Public\UnityProjects\ReachyMiniTeleop\Logs\EditModeTests.log' -projectPath 'C:\Users\Public\UnityProjects\ReachyMiniTeleop' -executeMethod ReachyMiniTeleop.Tests.Editor.ReachyBatchTestRunner.RunEditMode
```

- Batch summary output: `ReachyEditModeTestSummary.txt` (ignored by git).
- Last known validation: `passed=15`, `failed=0`, `skipped=0`.

## Guardrails

- Do not commit generated folders: `Library`, `Temp`, `Logs`, `UserSettings`, `Assets/Packages`.
- Do not commit generated IDE files or local test summaries.
- Do not add real robot IPs, tokens, participant IDs, private lab endpoints, study data paths, or unpublished experiment logic.
- Update README and tests together if payload shape, coordinate conversion, or ZMQ framing changes.
