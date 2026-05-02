# ReachyMiniTeleop Agent Guide

## Project

- Unity `6000.3.9f1`, Apache-2.0, Quest to Reachy Mini teleop.
- Main path: skeleton provider -> Reachy payload builder -> ZMQ DEALER -> Reachy Mini ROUTER bridge.
- Main scene: `Assets/Scenes/ReachyMiniTeleop.unity`.
- Default config: `Assets/Config/ReachyTeleopConfig.asset` (`tcp://localhost:40000`, identity `body`, `30 Hz`).
- Local receiver: `Tools/mock_reachy_router.py`.

## Runtime UI

- Users enter only a host/IP in `RobotIpInput`, never a scheme, port, or path.
- Pose data uses `tcp://<host>:40000`; video signaling uses `ws://<host>:8766`.
- `Connect With Pose Data` and `ConnectVideo` are Toggles; the last successful host is saved with `PlayerPrefs`.
- Keep IP entry on the in-scene keypad path for Quest/XR Simulation. `TouchScreenKeyboard` is optional fallback, not the primary path.
- `MainMenu` is a world-space Meta Interaction UI with `HeadFollowMenu`; do not parent it directly under the camera or replace the interaction stack unless asked.

## Protocol

- ZMQ messages are DEALER multipart frames: `[empty frame, JSON payload]`.
- Payload keys: `body_yaw_degrees`, `head_position`, `head_rotation`, `antennas`.
- Unity input is RUF; robot output is FLU.
- Position conversion is `(x, y, z) -> (z, -x, y)`.
- Keep frame conversion in `CoordinateFrameUtil`; keep payload math in `ReachyHeadCommandBuilder`.

## Video

- Unity receives the Reachy camera stream through WebRTC signaling at `ws://<robot-or-pc-host>:8766`.
- Keep `ReachyVideoInputController.DefaultSignalingPort` aligned with the Python bridge default `--signal-port 8766`.
- In Quest builds, `localhost` means the headset. Use the PC/robot LAN IP.
- The video surface should show while connecting/connected and clear/hide on signaling failure or disconnect.

## Tests And Guardrails

- Prefer Unity MCP for editor-safe inspection or validation when useful; editor tests live in `Assets/Tests/Editor`.
- Batch test method: `ReachyMiniTeleop.Tests.Editor.ReachyBatchTestRunner.RunEditMode`; summary: `ReachyEditModeTestSummary.txt`.
- Last known validation: `passed=20`, `failed=0`, `skipped=0`.
- Update README and tests together if payload shape, coordinate conversion, ports, or ZMQ framing change.
- NetMQ is required. Do not add fake fallback transports when NuGet DLLs are missing.
- Do not commit generated folders: `Library`, `Temp`, `Logs`, `UserSettings`, `Assets/Packages`.
- Keep Meta Immersive Debugger disabled unless explicitly debugging it; its `PanelInputModule` can throw invalid quaternion errors in XR Simulation.
