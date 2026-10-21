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

## Run it: step by step

The examples use this setup: robot at `192.168.50.95`, laptop at `192.168.50.62` on the robot's router. Replace them with your own addresses if they differ. iPhone menu names are given in English, with Dutch in brackets.

### A. One-time setup (laptop)

1. Open Terminal and go to the repo:
   ```bash
   cd ~/ReachyTeleop
   ```
2. Create the Python environment and install the dependencies (skip this if `.venv/` already exists):
   ```bash
   python3.12 -m venv .venv
   .venv/bin/pip install -r requirements.txt
   ```

### B. One-time setup (iPhone): trust the laptop's certificate

Do this once, with the server running (step C.3).

1. Connect the iPhone to the **robot's router Wi-Fi**. Check under Settings → Wi-Fi → ⓘ (Instellingen → Wifi → ⓘ) that the IP address starts with `192.168.50.`. If iOS says the network has no internet, stay connected and turn off Mobile Data (Mobiele data).
2. In Safari, open **`http://192.168.50.62:8080/`**. Note that this is `http` on port `8080`.
3. Tap **Download certificate**, then **Allow** (Sta toe).
4. Go to **Settings → Profile Downloaded → Install** (Instellingen → Profiel gedownload → Installeer). Enter your passcode and tap Install again.
5. Go to **Settings → General → About → Certificate Trust Settings** (Instellingen → Algemeen → Info → Certificaatvertrouwensinstellingen, at the very bottom). Switch on **Reachy Phone Teleop local CA**.
6. Open Control Center and turn off **Portrait Orientation Lock** (Portretstand-vergrendeling), so the video rotates when you hold the phone sideways.

### C. Every time

1. **Power on the robot** and wait 1–2 minutes for it to boot and join the router.
2. **Check the laptop can reach it.** This should print a number such as `-0.05`:
   ```bash
   curl http://192.168.50.95:8000/api/state/present_body_yaw
   ```
   If it doesn't, the robot isn't on the network yet. Look it up in the router's device list at `http://192.168.50.1`. If macOS asks whether Terminal or Python may access the **local network**, click **Allow**.
3. **Start the server**, and leave this Terminal window open:
   ```bash
   cd ~/ReachyTeleop
   .venv/bin/python -m phone_teleop --robot 192.168.50.95
   ```
   It prints the phone URL on the robot's network, for example `https://192.168.50.62:4443/`. To stop the server, press `Ctrl+C`.
4. **On the iPhone**, open the printed `https://…:4443/` URL in Safari. It should load with no warning. Tap **Start**, then **Allow** motion access.
5. **Wake the robot.** Don't touch the screen area while you do this. Tap **⋯ → Motors on**, so the chip changes from "motors disabled" to "robot". Then tap **⋯ → Wake up**; the chip shows "robot busy" while it moves and turns green when it's done. Keep your hands clear of the robot.
6. **Center.** Hold the phone in **landscape** in front of your eyes, look straight ahead, and tap **Center**.
7. **Drive.** Press and hold anywhere on the screen, and the robot's head follows the phone. Let go and it stops where it is. A green border means it's moving; "catching up" means it's still moving slowly to where you're looking.
8. **Finish.** Tap **⋯ → Sleep** (not while holding), wait for it to finish, then press `Ctrl+C` in the server Terminal.

Tip: in Safari, **Share → Add to Home Screen** (Zet op beginscherm) gives a full-screen app without the toolbars.

### Troubleshooting

| What you see | What to do |
|---|---|
| `zsh: no such file or directory: robot-ip` | You typed a placeholder. Use the real address, e.g. `192.168.50.95`. |
| `curl` hangs or says "No route to host" | The robot isn't on the network yet, or macOS blocked Local Network access for Terminal (System Settings → Privacy & Security → Local Network). |
| `http://…:8080/` doesn't load on the phone | The phone is on the wrong Wi-Fi, or the router isolates Wi-Fi clients (turn off "AP/client isolation"). |
| Safari says "This connection is not private" (Deze verbinding is niet privé) | The trust switch in B.5 isn't on. |
| Chip says "motors disabled" | Do C.5: Motors on, then Wake up. |
| Chip says "tap Center" | Do C.6. You need to do this again after a page reload. |
| "blocked: …" while holding | The robot can't be moved right now (motors off, a move running, or no robot state). The reason is shown in the chip. |
| No video | Wait a few seconds. Check the video chip and **⋯ → Stats**. Toggle **⋯ → Video off/on**. |
| Video shows sideways | Turn off Portrait Orientation Lock (B.6). |

### Safety

- Every target goes through `phone_teleop/safety_limits.py`, vendored from `~/EEPY/robot/safety_limits.py` (identical logic): head pitch/roll ±40°, head yaw ±180°, body yaw ±160°, head-vs-body yaw ≤ 65°.
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
