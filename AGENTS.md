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
  - Public default config: `tcp://localhost:40000`, identity `body`, 30 Hz, body yaw limit 45 degrees, antenna max 90 degrees.

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

- `Assets/Scripts/Runtime/Transport/WebRTCClient.cs`
  - WebRTC receive-only video client for the Reachy camera stream.
  - Uses Unity WebRTC plus WebSocketSharp signaling.
  - Default signaling URL is `ws://127.0.0.1:8766`; Quest builds should connect to the PC/robot host IP, not `localhost`.
  - Binds the first remote `VideoStreamTrack` texture to the in-scene video `RawImage`.
  - On signaling close/error, clears the `RawImage`, closes the peer connection, and lets UI reset the video toggle.

- `Assets/Scripts/Runtime/UI/ReachyEndpointInputController.cs`
  - Runtime Robot IP UI controller for the world-space `MainMenu`.
  - Users enter only the host/IP; the controller builds `tcp://<host>:40000`.
  - Uses an in-scene numeric keypad for Quest/XR Simulation. Do not rely on `TouchScreenKeyboard` as the primary input path.
  - Last successful host is saved with `PlayerPrefs`.

- `Assets/Scripts/Runtime/UI/HeadFollowMenu.cs`
  - Lightweight yaw-only floating follow behavior for the world-space `MainMenu`.
  - Attached to the root `MainMenu` and targets `CenterEyeAnchor`; falls back to `Camera.main`.
  - Uses a below-view offset, angular dead zone, position dead zone, and smooth easing so the menu follows the headset without feeling head-locked.
  - Keep pose/dead-zone math testable through static helpers; `RecenterNow()` should snap the menu under the current view when needed.

- `Assets/Scripts/Runtime/UI/ReachyVideoInputController.cs`
  - Runtime video UI controller for the world-space `MainMenu`.
  - `ConnectVideo` is a Toggle, not a plain Button: on toggles WebRTC video on, off disconnects.
  - Reuses the existing Robot IP input host and builds `ws://<host>:8766`.
  - Keep video IP entry on the existing in-scene keypad path; do not add a separate Quest keyboard path unless explicitly requested.

- `Tools/mock_reachy_router.py`
  - Python ROUTER receiver for local testing.

## Video Stream

- Unity receives video through WebRTC signaling over WebSocket at `ws://<robot-or-pc-host>:8766`.
- The Python video bridge default is `--signal-port 8766`; keep Unity's `ReachyVideoInputController.DefaultSignalingPort` aligned with that backend default.
- The `ConnectVideo` Toggle uses the same host typed into `RobotIpInput`; users should enter only the host/IP, not `ws://`, not a port, and not a path.
- In Quest builds, `localhost` means the headset and will not reach a bridge running on the PC. Use the PC/robot LAN IP.
- The video surface should become visible when connection starts, then clear/hide when signaling fails or disconnects.

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
- Prefer Unity MCP tooling for validation, inspection, and editor-safe automation when useful
- Editor tests are in `Assets/Tests/Editor`.
- Preferred batch command:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe' -batchmode -nographics -logFile 'C:\Users\Public\UnityProjects\ReachyMiniTeleop\Logs\EditModeTests.log' -projectPath 'C:\Users\Public\UnityProjects\ReachyMiniTeleop' -executeMethod ReachyMiniTeleop.Tests.Editor.ReachyBatchTestRunner.RunEditMode
```

- Batch summary output: `ReachyEditModeTestSummary.txt` (ignored by git).
- Last known validation: `passed=20`, `failed=0`, `skipped=0`.

## Guardrails

- Do not commit generated folders: `Library`, `Temp`, `Logs`, `UserSettings`, `Assets/Packages`.
- Update README and tests together if payload shape, coordinate conversion, or ZMQ framing changes.
- Keep Quest IP entry on the in-scene keypad path; Android/Quest system keyboard did not work reliably for this world-space UI.
- Keep `MainMenu` as a world-space Meta Interaction UI with `HeadFollowMenu`; do not parent it directly under the camera or replace the existing UI interaction stack with XR Interaction Toolkit unless explicitly requested.
- Keep Meta Immersive Debugger disabled for this demo unless explicitly debugging it; its `PanelInputModule` can throw invalid quaternion errors in XR Simulation.
