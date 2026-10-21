"""Server integration: real uvicorn teleop app against a fake daemon and fake signaling server."""

import asyncio
import contextlib
import json
import math
import time

import httpx
import numpy as np
import pytest
from websockets.asyncio.client import connect
from websockets.exceptions import ConnectionClosed

from fakes import wait_for, wait_quiet
from phone_teleop import pose_math as pm
from phone_teleop.teleop import TeleopSettings

A0, B0, G0 = 0.0, 0.0, -90.0


class Phone:
    """A /pose client that records server messages."""

    def __init__(self, ws):
        self.ws = ws
        self.messages: list[dict] = []
        self.closed = asyncio.Event()
        self.seq = 0
        self._reader = asyncio.create_task(self._read())
        self._streamer = None

    async def _read(self):
        try:
            async for raw in self.ws:
                self.messages.append(json.loads(raw))
        except ConnectionClosed:
            pass
        finally:
            self.closed.set()

    def of(self, kind):
        return [m for m in self.messages if m.get("type") == kind]

    async def send(self, msg):
        await self.ws.send(json.dumps(msg) if not isinstance(msg, (str, bytes)) else msg)

    async def orient(self, a=A0, b=B0, g=G0, held=False):
        self.seq += 1
        self.last_sent = time.monotonic()
        await self.send({"type": "orient", "a": a, "b": b, "g": g, "held": held, "seq": self.seq, "t": 0})

    def stream(self, held=True, a=A0, b=B0, g=G0, period=0.01):
        """Keep sending samples like the page does (deviceorientation + keepalive)."""
        self.stream_args = dict(a=a, b=b, g=g, held=held)

        async def loop():
            while True:
                await self.orient(**self.stream_args)
                await asyncio.sleep(period)

        self.stop_stream()
        self._streamer = asyncio.create_task(loop())

    def stop_stream(self):
        if self._streamer:
            self._streamer.cancel()
            self._streamer = None

    async def reply(self, kind, timeout=3.0):
        n = len(self.of(kind))
        await wait_for(lambda: len(self.of(kind)) > n, timeout)
        return self.of(kind)[-1]

    async def close(self):
        self.stop_stream()
        await self.ws.close()
        await self.closed.wait()


@contextlib.asynccontextmanager
async def phone(t):
    ws = await connect(t.ws("/pose"))
    p = Phone(ws)
    try:
        yield p
    finally:
        await p.close()


def frame_rpy_body(frame):
    m = np.array(frame["target_head_pose"]["m"]).reshape(4, 4)
    return np.array(pm.matrix_to_rpy(m[:3, :3])), frame.get("target_body_yaw")


# --- /pose messages ------------------------------------------------------------

async def test_status_messages(teleop):
    async with phone(teleop) as p:
        st = await p.reply("status")
        for key in ["calibrated", "engaged", "synced", "limited", "head_rpy_deg", "body_yaw_deg",
                    "robot", "error", "sent", "rx"]:
            assert key in st
        await wait_for(lambda: p.of("status") and p.of("status")[-1]["robot"] is True)
        n = len(p.of("status"))
        await wait_for(lambda: len(p.of("status")) >= n + 3, timeout=2)  # ~5 Hz


async def test_ping_pong(teleop):
    async with phone(teleop) as p:
        await p.send({"type": "ping", "t": 123.5})
        assert (await p.reply("pong"))["t"] == 123.5


async def test_calibrate_errors_and_success(teleop):
    async with phone(teleop) as p:
        await p.send({"type": "calibrate"})
        r = await p.reply("calibrated")
        assert r["ok"] is False and "No orientation" in r["error"]
        await p.orient(0, 0, 0)  # flat on the table
        await p.send({"type": "calibrate"})
        r = await p.reply("calibrated")
        assert r["ok"] is False and "upright" in r["error"]
        await p.orient()
        await p.send({"type": "calibrate"})
        assert (await p.reply("calibrated"))["ok"] is True
        await wait_for(lambda: p.of("status")[-1]["calibrated"] is True)


@pytest.mark.parametrize("name, path", [
    ("wake_up", "/api/move/play/wake_up"),
    ("goto_sleep", "/api/move/play/goto_sleep"),
    ("motors_on", "/api/motors/set_mode/enabled"),
    ("motors_off", "/api/motors/set_mode/disabled"),
])
async def test_actions_forwarded_to_rest(teleop, name, path):
    async with phone(teleop) as p:
        await p.send({"type": "action", "name": name})
        r = await p.reply("action")
        assert r["ok"] is True and r["name"] == name
        assert teleop.daemon.posts == [path]


async def test_action_errors_reported(teleop):
    async with phone(teleop) as p:
        await p.send({"type": "action", "name": "self_destruct"})
        r = await p.reply("action")
        assert r["ok"] is False and "unknown action" in r["error"]
        teleop.daemon.action_status = 500
        await p.send({"type": "action", "name": "wake_up"})
        r = await p.reply("action")
        assert r["ok"] is False
        await p.send({"type": "action"})  # no name at all
        r = await p.reply("action")
        assert r["ok"] is False
        await p.send({"type": "ping", "t": 1})
        await p.reply("pong")  # session survived


@pytest.mark.parametrize("garbage", [
    {"type": "orient", "a": "x", "b": 0, "g": 0, "held": True},
    {"type": "orient", "a": None, "b": 0, "g": 0, "held": True},
    {"type": "orient", "b": 0, "g": 0, "held": True},
    {"type": "orient", "a": float("nan"), "b": 0, "g": 0, "held": True},
    {"type": "nope"},
    {},
])
async def test_bad_orient_releases_but_keeps_session(teleop, garbage):
    async with phone(teleop) as p:
        await p.orient(held=True)
        await wait_for(lambda: teleop.controller.held)
        await p.send(json.dumps(garbage))
        await p.send({"type": "ping", "t": 7})
        assert (await p.reply("pong"))["t"] == 7
        if garbage.get("type") == "orient":
            assert teleop.controller.held is False


@pytest.mark.parametrize("raw", ["not json", "[1, 2]", "42", "null", b"\x00binary"])
async def test_malformed_frames_do_not_kill_session(teleop, raw):
    """A single malformed frame (bad JSON, non-object JSON, binary) should not drop the phone."""
    async with phone(teleop) as p:
        await p.orient(held=True)
        await wait_for(lambda: teleop.controller.held)
        await p.send(raw)
        await p.send({"type": "ping", "t": 9})
        with contextlib.suppress(TimeoutError):
            await wait_for(lambda: p.closed.is_set() or p.of("pong"), 1.0)
        assert p.of("pong") and not p.closed.is_set(), "session dropped on a malformed frame"


# --- end to end ----------------------------------------------------------------

async def calibrated_and_held(teleop, p, a=A0):
    await wait_for(lambda: teleop.link.connected)
    await p.orient(a=a)
    await p.send({"type": "calibrate"})
    assert (await p.reply("calibrated"))["ok"]
    p.stream(held=True, a=a)


async def test_end_to_end_frames_flow_only_while_held(teleop):
    d = teleop.daemon
    d.head.update(yaw=0.3, pitch=0.1)
    d.body = 0.2
    async with phone(teleop) as p:
        await wait_for(lambda: teleop.link.connected)
        await p.orient()
        await asyncio.sleep(0.1)
        assert d.frames == []  # not calibrated: nothing sent
        await p.send({"type": "calibrate"})
        assert (await p.reply("calibrated"))["ok"]
        await asyncio.sleep(0.1)
        assert d.frames == []  # calibrated but not held
        p.stream(held=True, a=A0 + 20.0)
        await wait_for(lambda: len(d.frames) >= 20)
        assert d.invalid == []
        assert "/api/state/present_head_pose" in d.state_gets  # synced from the robot
        rpy0, body0 = frame_rpy_body(d.frames[0])
        cap = math.radians(teleop.controller.settings.max_speed_deg_s)
        # First frame starts from the robot's present pose (0.3 yaw, 0.1 pitch).
        assert abs(rpy0[2] - 0.3) <= cap * 0.05 and abs(rpy0[1] - 0.1) <= cap * 0.05
        assert body0 == pytest.approx(0.2)
        await wait_for(lambda: abs(frame_rpy_body(d.frames[-1])[0][2] - math.radians(20)) < 1e-3, 3)
        for f in d.frames:
            rpy, body = frame_rpy_body(f)
            assert abs(rpy[0]) <= math.radians(40) + 1e-9 and abs(rpy[1]) <= math.radians(40) + 1e-9
            assert abs(body) <= math.radians(160) + 1e-9
            assert abs(rpy[2] - body) <= math.radians(65) + 1e-9
        await wait_for(lambda: p.of("status")[-1]["engaged"] and p.of("status")[-1]["synced"])
        assert p.of("status")[-1]["sent"] > 0 and p.of("status")[-1]["rx"] > 0
        # Lift the finger: frames stop.
        p.stream(held=False, a=A0 + 20.0)
        stopped_at = await wait_quiet(lambda: len(d.frames))
        await asyncio.sleep(0.2)
        assert len(d.frames) == stopped_at
        # Hold again: resync from the robot (a new present_head_pose read).
        gets = len(d.state_gets)
        p.stream(held=True, a=A0 + 20.0)
        await wait_for(lambda: len(d.frames) > stopped_at + 5)
        assert len(d.state_gets) > gets


async def test_explicit_release_message_stops_frames(teleop):
    d = teleop.daemon
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: len(d.frames) >= 5)
        p.stop_stream()
        await p.send({"type": "release"})
        n = await wait_quiet(lambda: len(d.frames))
        assert teleop.controller.held is False
        assert len(d.frames) == n


async def test_samples_stopping_trips_deadman_timeout(teleop):
    d = teleop.daemon
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: len(d.frames) >= 5)
        p.stop_stream()  # phone froze, still "held"
        await wait_quiet(lambda: len(d.frames), quiet=0.2)
        assert teleop.controller.held is True
        # Last frame comes no later than the deadman timeout (+ some slack) after the last sample.
        assert d.frame_times[-1] - p.last_sent <= teleop.controller.settings.deadman_timeout_s + 0.1


async def test_disconnect_releases_deadman(teleop):
    d = teleop.daemon
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: len(d.frames) >= 5)
        p.stop_stream()
        await p.ws.close()
        await wait_for(lambda: teleop.controller.held is False)
        n = await wait_quiet(lambda: len(d.frames))
        assert len(d.frames) == n
        assert teleop.hub.owner is None


async def test_newest_phone_takes_over(teleop):
    d = teleop.daemon
    async with phone(teleop) as a:
        await calibrated_and_held(teleop, a)
        await wait_for(lambda: len(d.frames) >= 3)
        async with phone(teleop) as b:
            await wait_for(lambda: a.closed.is_set(), 3)
            assert a.of("kicked"), a.messages[-3:]
            assert a.ws.close_code == 4000
            # The takeover released the deadman: frames stop until b holds.
            n = await wait_quiet(lambda: len(d.frames))
            assert teleop.controller.held is False
            # A new session starts uncalibrated (iOS alpha reference is per page load).
            assert b.of("status") == [] or b.of("status")[-1]["calibrated"] is False
            await calibrated_and_held(teleop, b)
            await wait_for(lambda: len(d.frames) > n + 3)
            assert teleop.hub.owner is not None
        # b left: deadman released, no owner
        await wait_for(lambda: teleop.hub.owner is None and not teleop.controller.held)


async def test_kicked_phone_closing_does_not_release_new_owner(teleop):
    d = teleop.daemon
    ws_a = await connect(teleop.ws("/pose"))
    a = Phone(ws_a)
    async with phone(teleop) as b:
        await wait_for(lambda: a.closed.is_set())
        await calibrated_and_held(teleop, b)
        await wait_for(lambda: len(d.frames) >= 3)
        await a.close()
        n = len(d.frames)
        await wait_for(lambda: len(d.frames) > n + 5)
        assert teleop.controller.held


async def test_daemon_error_reply_shows_in_status(teleop):
    teleop.daemon.reply_error_on_frame = True
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: "fake rejection" in (p.of("status")[-1]["error"] if p.of("status") else ""))


async def test_present_pose_failure_means_no_frames(teleop):
    d = teleop.daemon
    d.state_status = 500
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: p.of("status") and p.of("status")[-1]["error"].startswith("robot state:")
                       and p.of("status")[-1]["blocked"] == "robot state unknown")
        assert p.of("status")[-1]["engaged"] is True
        assert d.frames == []
        d.state_status = 200
        await wait_for(lambda: len(d.frames) > 3)


async def test_daemon_reconnect_resyncs(teleop):
    d = teleop.daemon
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p)
        await wait_for(lambda: len(d.frames) >= 3)
        gets = len(d.state_gets)
        await d.drop_clients()
        await wait_for(lambda: d.ws_connections >= 2, timeout=5)
        n = len(d.frames)
        await wait_for(lambda: len(d.frames) > n + 3)
        assert len(d.state_gets) > gets  # re-read the present pose after the reconnect


async def test_slow_present_pose_does_not_cause_jump(teleop):
    """A slow state read (100 ms LAN hiccup) must not turn into one big step on the next tick."""
    d = teleop.daemon
    d.head.update(yaw=math.radians(60.0))
    d.body = math.radians(30.0)
    d.state_delay_s = 0.15
    start_yaw = d.head["yaw"]  # the fake's present pose follows frames afterwards
    s = teleop.controller.settings
    async with phone(teleop) as p:
        await calibrated_and_held(teleop, p, a=A0)  # phone target yaw 0, robot at 60
        await wait_for(lambda: len(d.frames) >= 10)
    yaws = [frame_rpy_body(f)[0][2] for f in d.frames[:10]]
    steps = np.degrees(np.abs(np.diff([start_yaw, *yaws])))
    period = 1.0 / s.send_rate_hz
    # 60 deg away: still catching up, so the catch-up speed is the cap
    assert steps.max() <= s.catchup_speed_deg_s * period * 1.5, steps.round(2)


async def test_csv_log(make_teleop, tmp_path):
    path = tmp_path / "log.csv"
    t = await make_teleop(csv_path=path)
    async with phone(t) as p:
        for _ in range(3):
            await p.orient(held=True)
        await wait_for(lambda: t.hub.rx_count == 3)
    t.hub._csv_file.flush()
    lines = path.read_text().strip().splitlines()
    assert lines[0] == "server_t,phone_t,event_t,src,seq,alpha,beta,gamma,held"
    assert len(lines) == 4


# --- /signal proxy -------------------------------------------------------------

async def test_signal_proxy_both_directions(teleop):
    sig = teleop.signal
    async with connect(teleop.ws("/signal")) as ws:
        welcome = json.loads(await asyncio.wait_for(ws.recv(), 3))
        assert welcome == {"type": "welcome", "peerId": "peer-1"}
        msg = json.dumps({"type": "setPeerStatus", "roles": ["listener"]})
        await ws.send(msg)
        assert json.loads(await asyncio.wait_for(ws.recv(), 3)) == {"echo": msg}
        await ws.send(b"\x01\x02\x03")
        assert await asyncio.wait_for(ws.recv(), 3) == b"\x03\x02\x01"
        big = "x" * 300_000  # big SDP-like frame
        await ws.send(big)
        assert json.loads(await asyncio.wait_for(ws.recv(), 3)) == {"echo": big}
    await wait_for(lambda: sig.closed == 1)  # phone close propagates upstream
    assert sig.received[0] == msg


async def test_signal_upstream_close_closes_phone(teleop):
    async with connect(teleop.ws("/signal")) as ws:
        await asyncio.wait_for(ws.recv(), 3)
        await ws.send("close-me")
        with pytest.raises(ConnectionClosed):
            await asyncio.wait_for(ws.recv(), 3)


async def test_signal_unreachable_closes_with_1011(make_teleop):
    from fakes import free_port

    t = await make_teleop(signal_url=f"ws://127.0.0.1:{free_port()}")
    async with connect(t.ws("/signal")) as ws:
        with pytest.raises(ConnectionClosed):
            await asyncio.wait_for(ws.recv(), 5)
        assert ws.close_code == 1011


async def test_two_signal_clients_are_independent(teleop):
    async with connect(teleop.ws("/signal")) as a, connect(teleop.ws("/signal")) as b:
        await asyncio.wait_for(a.recv(), 3)
        await asyncio.wait_for(b.recv(), 3)
        await a.send("A")
        await b.send("B")
        assert json.loads(await asyncio.wait_for(a.recv(), 3)) == {"echo": "A"}
        assert json.loads(await asyncio.wait_for(b.recv(), 3)) == {"echo": "B"}


# --- static --------------------------------------------------------------------

async def test_static_files_and_mime_types(teleop):
    async with httpx.AsyncClient(base_url=teleop.http("")) as client:
        r = await client.get("/")
        assert r.status_code == 200 and "text/html" in r.headers["content-type"]
        assert r.headers.get("cache-control") == "no-store"
        assert '<script type="module" src="/static/app.mjs">' in r.text
        for name in ["app.mjs", "lib.mjs", "video.mjs"]:
            r = await client.get(f"/static/{name}")
            assert r.status_code == 200
            assert r.headers["content-type"].split(";")[0] in ("text/javascript", "application/javascript")
        r = await client.get("/clock")
        assert r.status_code == 200 and "Lag Clock" in r.text
        assert (await client.get("/static/../server.py")).status_code == 404


async def test_bootstrap_app_serves_ca(tmp_path):
    from fakes import run_uvicorn
    from phone_teleop.certs import CA_CERT, ensure_server_cert
    from phone_teleop.server import create_bootstrap_app

    ensure_server_cert(tmp_path, ["127.0.0.1"])
    app = create_bootstrap_app(tmp_path / CA_CERT, "https://10.0.0.5:4443/")
    async with run_uvicorn(app) as port:
        async with httpx.AsyncClient(base_url=f"http://127.0.0.1:{port}") as client:
            r = await client.get("/")
            assert "https://10.0.0.5:4443/" in r.text and "{{" not in r.text
            r = await client.get("/ca.crt")
            assert r.content == (tmp_path / CA_CERT).read_bytes()
            assert r.headers["content-type"].startswith("application/x-x509-ca-cert")
            assert "attachment" in r.headers["content-disposition"]


async def test_new_phone_session_requires_recalibration(teleop):
    """iOS reports alpha relative to an arbitrary per-page-load heading, so a calibration made by a
    previous page/phone is meaningless for the new connection. The new session must start
    uncalibrated (UI shows 'tap Center') instead of moving with the stale calibration."""
    d = teleop.daemon
    async with phone(teleop) as a:
        await calibrated_and_held(teleop, a)
        await wait_for(lambda: len(d.frames) >= 3)
    await wait_for(lambda: teleop.hub.owner is None)
    async with phone(teleop) as b:
        st = await b.reply("status")
        assert st["calibrated"] is False
        n = len(d.frames)
        b.stream(held=True, a=A0 + 90.0)  # same physical pose, different alpha reference
        await asyncio.sleep(0.2)
        assert len(d.frames) == n, "robot moved using the previous session's calibration"
