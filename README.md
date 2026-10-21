# Reachy Mini iPhone Teleop

Hold your iPhone in front of your eyes. It shows the Reachy Mini camera, and the robot's head follows the phone's orientation (yaw, pitch, roll). Everything runs on your local network, uses the free Safari browser, and needs no Apple developer account, no app install and no Hugging Face account.

```text
iPhone Safari (HTTPS page)
  ├─ wss://laptop:4443/signal ──proxy──> ws://<robot>:8443        GStreamer signaling only
  ├─ WebRTC video <──────────── direct on the LAN ──────────── robot
  └─ wss://laptop:4443/pose  ──> phone_teleop (laptop)
                                   calibrate → smooth/speed-cap → body follow
                                   → safety_limits.enforce()
                                   → ws://<robot>:8000/api/move/ws/set_target @ 50 Hz
```

The laptop is required because Safari only exposes motion sensors on HTTPS pages, and an HTTPS page can't open the daemon's plain `ws://` ports. The laptop serves the page over HTTPS with a certificate authority it creates itself, and relays poses and signaling. Video goes directly from the robot to the phone.

## Setup (once)

```bash
python3.12 -m venv .venv
.venv/bin/pip install -r requirements.txt
```

Check that the laptop can reach the robot. This should print a number:

```bash
curl http://192.168.50.95:8000/api/state/present_body_yaw
```

On macOS, if Terminal or Python asks for **Local Network** access, allow it; without it, connections to the robot fail with "No route to host". The **iPhone must join the robot's network** (the same subnet as the robot, e.g. 192.168.50.x). The laptop can be on two networks at once, for example wired to the robot router and on Wi-Fi for the internet.

Start it:

```bash
.venv/bin/python -m phone_teleop --robot 192.168.50.95
```

It prints the URL on the robot's network; use that one on the phone. Every local address, plus `<name>.local`, is in the certificate. On the iPhone, install the certificate once:

1. Open `http://<laptop-ip>:8080/` in Safari and tap **Download certificate**, then **Allow**.
2. Go to **Settings → Profile Downloaded → Install**.
3. Go to **Settings → General → About → Certificate Trust Settings** and switch on **Reachy Phone Teleop local CA**.

The CA is stored in `.certs/`, which git ignores, and is reused. If the laptop's IP changes, the server certificate is regenerated automatically and the phone keeps trusting it.

## Use

1. Turn off **Portrait Orientation Lock** in Control Center, or the landscape video shows sideways.
2. Open `https://<laptop-ip>:4443/` in Safari and tap **Start**, then **Allow** motion access.
3. If the robot is asleep: **⋯ → Motors on**, then **⋯ → Wake up**, and wait until the robot chip is green. Sleep turns the motors off and Wake up doesn't turn them on, so both are needed.
4. Hold the phone in **landscape** in front of your eyes, look straight ahead, and tap **Center**.
5. **Press and hold anywhere on the screen** to make the robot follow. Let go and it stops where it is. A green border means it's moving. "catching up" means it's still moving slowly to where you're looking.

Tip: **Share → Add to Home Screen** gives a full-screen app without Safari's toolbars.

### Safety

- Every target goes through `phone_teleop/safety_limits.py`, vendored verbatim from `~/EEPY/robot/safety_limits.py`: head pitch/roll ±40°, head yaw ±180°, body yaw ±160°, head-vs-body yaw ≤ 65°.
- Hold-to-move deadman. Lifting your finger, locking the screen, switching apps, a dropped connection, or no sample for 0.3 s all stop motion, and the robot holds its pose.
- The laptop polls the robot's motor mode, running daemon moves and present pose 5 times per second. Nothing is sent unless the motors are `enabled` and no move (wake up, sleep, a goto from another app) is running. If the robot's state is unknown for 1 s, motion stops.
- Each time you engage, or a move ends, or the robot stops following (more than 30° or 15 mm away from what was sent), the server re-reads the robot's present pose, rotation **and** translation, and starts from there. The robot doesn't snap to the phone. It catches up at most 60°/s until it's within 3° of your view, then follows at up to 180°/s per axis. Head translation ramps back to the neutral at 40 mm/s, for example from the sleep pose.
- Tapping Wake up or Sleep while holding is safe: motion pauses until the move is over, then re-syncs.
- Only one phone controls the robot at a time. The newest connection takes over, and a different phone or a page reload must tap Center again. The same page reconnecting after a Wi-Fi blip keeps its calibration.
- The sockets only accept connections from the app's own page (Origin check). There is no password: anyone on the network who can reach the laptop can open the page. The daemon itself is unauthenticated too.
- The body turns only once the head is more than 55° from it, so the head leads and the body follows. The daemon's own kinematics may adjust the body yaw further.

### Options

```
--robot HOST          daemon host (default 192.168.50.95)
--rate HZ             pose send rate (50)
--smoothing 0-1       fraction of remaining error per tick, 1 = none (0.5)
--max-speed DEG_S     angular speed cap per axis (180)
--catchup-speed DEG_S speed right after engaging, until caught up (60)
--deadman-timeout S   stop if no fresh sample for this long (0.3)
--body-follow DEG     head-vs-body yaw before the body turns (55, must be < 65)
--no-body             never move the body
--log-csv FILE        append every orientation message (for rate/jitter analysis)
--port / --setup-port HTTPS and certificate ports (4443 / 8080)
```

## Measuring

- **Orientation rate**: tap **⋯ → Stats**. It shows event Hz, the interval mean ± jitter and the max gap, plus raw alpha/beta/gamma. `--log-csv logs/orient.csv` records every message on the laptop. Use the rows with `src = evt` (real sensor events) and the `event_t` column. Other rows are the page re-sending the last sample (keepalive, touch, Center).
- **Phone ↔ laptop RTT** is also in the Stats overlay.
- **Video lag (one screenshot)**: open `https://<laptop-ip>:4443/clock` on the laptop and point the robot camera at it. On the phone, open Stats: a big clock in the bottom-right corner shows the **laptop's** time, synced over the WebSocket to within about half the best RTT. Take an iPhone screenshot. Lag = the corner clock − the clock seen in the video. Repeat a few times. The Stats overlay also shows fps, resolution and the WebRTC jitter-buffer delay.

## Test from the laptop

```bash
.venv/bin/python -m pytest              # Python: math, controller, safety, server, proxy
node --test tests/js                     # JS: helpers, signaling, page smoke test (Node ≥ 16.17)
```

Without a robot, use the MuJoCo sim (`pip install "reachy-mini[mujoco]"`; on macOS it needs `--headless`):

```bash
.venv/bin/reachy-mini-daemon --sim --headless --no-media --fastapi-port 18000 --no-preload-datasets &
REACHY_SIM_PORT=18000 .venv/bin/python -m pytest -q tests/test_sim.py
.venv/bin/python -m phone_teleop --robot 127.0.0.1 --robot-port 18000
```

## Layout

```text
phone_teleop/
  __main__.py       CLI: certs + HTTPS app + HTTP cert bootstrap
  server.py         Starlette routes: /, /clock, /pose (WSS), /signal (WSS proxy)
  teleop.py         TeleopController (pure) + RobotLink (daemon WS/REST) + control loop
  pose_math.py      DeviceOrientation → FLU head rotation, calibration, FullBodyTarget
  safety_limits.py  vendored from EEPY (logic unchanged; only a header note added)
  certs.py          local root CA + server cert
  web/              index.html, app.mjs, video.mjs (WebRTC), lib.mjs, clock.html, setup.html
tests/              pytest (+ tests/js for Node)
```

## Quest reference

The Unity/Meta Quest app this started from is still in `Assets/`, `Packages/` and `ProjectSettings/` as reference. The iPhone app doesn't use it. Its README is in [Docs/QuestReference.md](Docs/QuestReference.md). Pieces carried over: the FLU frame convention, the row-major `FullBodyTarget` matrix (`ReachyDaemonTargetAdapter`), and the GStreamer signaling handshake (`WebRTCClient.cs` → `web/video.mjs`).

## License

MIT, see [LICENSE](LICENSE).
