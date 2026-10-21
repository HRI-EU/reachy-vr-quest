"""HTTPS app for the phone, plus a plain-HTTP bootstrap page that serves the root CA.

Routes (HTTPS):
  /          teleop page            /clock   ms clock for glass-to-glass lag tests
  /pose      WSS: phone samples in, status out; drives the TeleopController
  /signal    WSS: transparent proxy to the robot's GStreamer signaling ws://<robot>:8443
"""

from __future__ import annotations

import asyncio
import contextlib
import csv
import json
import logging
import time
from dataclasses import asdict
from pathlib import Path
from urllib.parse import urlsplit

from starlette.applications import Starlette
from starlette.responses import FileResponse, HTMLResponse, Response
from starlette.routing import Mount, Route, WebSocketRoute
from starlette.staticfiles import StaticFiles
from starlette.websockets import WebSocket, WebSocketDisconnect, WebSocketState
from websockets.asyncio.client import connect as ws_connect

from . import pose_math
from .teleop import RobotLink, TeleopController, control_loop

log = logging.getLogger("phone_teleop")

WEB_DIR = Path(__file__).parent / "web"
STATUS_INTERVAL_S = 0.2


class TeleopHub:
    """Shared state: one controlling phone at a time (newest connection wins)."""

    def __init__(self, controller: TeleopController, link: RobotLink, signal_url: str,
                 csv_path: Path | None = None):
        self.controller = controller
        self.link = link
        self.signal_url = signal_url
        self.owner: WebSocket | None = None
        self.page: str | None = None  # page-load id of the last phone that said hello
        self.background: set[asyncio.Task] = set()
        self.sent_count = 0
        self.rx_count = 0
        self._csv_file = None
        self._csv = None
        if csv_path is not None:
            csv_path.parent.mkdir(parents=True, exist_ok=True)
            self._csv_file = open(csv_path, "a", newline="")
            self._csv = csv.writer(self._csv_file)
            if self._csv_file.tell() == 0:
                self._csv.writerow(["server_t", "phone_t", "event_t", "src", "seq",
                                    "alpha", "beta", "gamma", "held"])

    def log_sample(self, msg: dict) -> None:
        # src: "evt" = a real deviceorientation event (use these rows for rate/jitter),
        # "keepalive"/"ptr"/"center" = the last sample re-sent by the page.
        if self._csv is not None:
            self._csv.writerow([f"{time.time():.4f}", msg.get("t"), msg.get("et"), msg.get("src", ""),
                                msg.get("seq"), msg.get("a"), msg.get("b"), msg.get("g"),
                                int(msg.get("held") is True)])

    def close(self) -> None:
        if self._csv_file is not None:
            self._csv_file.close()

    def on_sent(self, _target: dict) -> None:
        self.sent_count += 1

    def status(self) -> dict:
        now = time.monotonic()
        st = asdict(self.controller.status(now))
        robot = self.link.state
        st.update(type="status", robot=self.link.connected, error=self.link.last_error,
                  motors=robot.motors if robot else None, moving=robot.moving if robot else None,
                  sent=self.sent_count, rx=self.rx_count)
        return st


def _hub(ws: WebSocket) -> TeleopHub:
    return ws.app.state.hub


async def _safe_send(ws: WebSocket, payload: dict) -> None:
    if ws.application_state == WebSocketState.CONNECTED:
        await ws.send_text(json.dumps(payload))


def _origin_ok(ws: WebSocket) -> bool:
    """Only our own page may open the sockets (blocks other sites in the phone's browser)."""
    origin = ws.headers.get("origin")
    if origin is None:  # non-browser clients (tests, scripts) send no Origin
        return True
    return urlsplit(origin).netloc == ws.headers.get("host")


async def pose_endpoint(ws: WebSocket) -> None:
    hub = _hub(ws)
    if not _origin_ok(ws):
        await ws.close(code=1008)
        return
    await ws.accept()
    previous, hub.owner = hub.owner, ws
    hub.controller.new_session()
    if previous is not None:
        with contextlib.suppress(Exception):
            await _safe_send(previous, {"type": "kicked", "reason": "another phone connected"})
            await previous.close(code=4000)

    async def status_pump() -> None:
        with contextlib.suppress(Exception):  # phone gone: the receive loop cleans up
            while True:
                await _safe_send(ws, hub.status())
                await asyncio.sleep(STATUS_INTERVAL_S)

    pump = asyncio.create_task(status_pump())
    try:
        while True:
            message = await ws.receive()
            if message["type"] == "websocket.disconnect" or hub.owner is not ws:
                break
            try:
                msg = json.loads(message.get("text") or "")
            except ValueError:
                msg = None
            if not isinstance(msg, dict):
                log.warning("Ignoring malformed message from phone")
                continue
            await _handle_pose_message(ws, hub, msg)
    except (WebSocketDisconnect, RuntimeError):
        pass
    finally:
        pump.cancel()
        if hub.owner is ws:
            hub.owner = None
            hub.controller.release()
        with contextlib.suppress(Exception):
            await ws.close()


async def _handle_pose_message(ws: WebSocket, hub: TeleopHub, msg: dict) -> None:
    kind = msg.get("type")
    if kind == "hello":
        page = msg.get("page")
        page = page if isinstance(page, str) and page else None
        resumed = page is not None and page == hub.page and hub.controller.resume_session()
        hub.page = page
        await _safe_send(ws, {"type": "hello", "resumed": resumed})
    elif kind == "orient":
        try:
            hub.controller.on_sample(msg["a"], msg["b"], msg["g"], msg.get("held") is True, time.monotonic())
        except (KeyError, TypeError, ValueError):
            hub.controller.release()  # garbage in -> stop moving, keep the session
            return
        if msg.get("src", "evt") == "evt":
            hub.rx_count += 1
        hub.log_sample(msg)
    elif kind == "release":
        hub.controller.release()
    elif kind == "calibrate":
        try:
            hub.controller.calibrate()
            await _safe_send(ws, {"type": "calibrated", "ok": True})
        except pose_math.CalibrationError as exc:
            await _safe_send(ws, {"type": "calibrated", "ok": False, "error": str(exc)})
    elif kind == "ping":
        # server_t lets the page show a laptop-synced clock for lag measurements.
        await _safe_send(ws, {"type": "pong", "t": msg.get("t"), "server_t": time.time() * 1000.0})
    elif kind == "action":
        # Run in the background so a slow REST call doesn't stall orientation samples.
        task = asyncio.create_task(_run_action(ws, hub, str(msg.get("name"))))
        hub.background.add(task)
        task.add_done_callback(hub.background.discard)


async def _run_action(ws: WebSocket, hub: TeleopHub, name: str) -> None:
    # link.action() blocks motion immediately; control_loop re-syncs once the move is over.
    hub.controller.desync()
    try:
        result = await hub.link.action(name)
        await _safe_send(ws, {"type": "action", "name": name, "ok": True, "result": result})
    except Exception as exc:  # noqa: BLE001
        with contextlib.suppress(Exception):
            await _safe_send(ws, {"type": "action", "name": name, "ok": False, "error": str(exc)[:300]})


async def signal_endpoint(ws: WebSocket) -> None:
    """Pipe text frames between the phone and the robot's signaling server, unchanged."""
    hub = _hub(ws)
    if not _origin_ok(ws):
        await ws.close(code=1008)
        return
    await ws.accept()
    try:
        upstream = await ws_connect(hub.signal_url, open_timeout=3, max_size=None)
    except Exception as exc:  # noqa: BLE001
        log.warning("Signaling upstream %s: %s", hub.signal_url, exc)
        await ws.close(code=1011, reason="robot signaling unreachable"[:120])
        return

    async def phone_to_robot() -> None:
        while True:
            message = await ws.receive()
            if message["type"] == "websocket.disconnect":
                return
            data = message.get("text")
            if data is None and message.get("bytes") is not None:
                data = message["bytes"]
            if data is not None:
                await upstream.send(data)

    async def robot_to_phone() -> None:
        async for data in upstream:
            if isinstance(data, bytes):
                await ws.send_bytes(data)
            else:
                await ws.send_text(data)

    tasks = [asyncio.create_task(phone_to_robot()), asyncio.create_task(robot_to_phone())]
    try:
        await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        await upstream.close()
        with contextlib.suppress(Exception):
            await ws.close()


class NoCacheStaticFiles(StaticFiles):
    """The page is a few KB on a LAN: never let Safari (or a Home Screen app) keep stale JS."""

    def file_response(self, *args, **kwargs) -> Response:
        response = super().file_response(*args, **kwargs)
        response.headers["Cache-Control"] = "no-store"
        return response


async def index(_request) -> Response:
    return FileResponse(WEB_DIR / "index.html", headers={"Cache-Control": "no-store"})


async def clock(_request) -> Response:
    return FileResponse(WEB_DIR / "clock.html")


def create_app(controller: TeleopController, link: RobotLink, signal_url: str,
               csv_path: Path | None = None, run_link: bool = True) -> Starlette:
    hub = TeleopHub(controller, link, signal_url, csv_path)

    @contextlib.asynccontextmanager
    async def lifespan(app: Starlette):
        app.state.hub = hub
        loop_task = None
        if run_link:
            await link.start()
            loop_task = asyncio.create_task(control_loop(controller, link, hub.on_sent))
        try:
            yield
        finally:
            if loop_task is not None:
                loop_task.cancel()
                await link.stop()
            hub.close()

    return Starlette(
        routes=[
            Route("/", index),
            Route("/clock", clock),
            WebSocketRoute("/pose", pose_endpoint),
            WebSocketRoute("/signal", signal_endpoint),
            Mount("/static", NoCacheStaticFiles(directory=WEB_DIR), name="static"),
        ],
        lifespan=lifespan,
    )


def create_bootstrap_app(ca_path: Path, https_url: str) -> Starlette:
    """Plain HTTP: lets the iPhone download the root CA before it trusts anything."""

    async def home(_request) -> Response:
        html = (WEB_DIR / "setup.html").read_text().replace("{{HTTPS_URL}}", https_url)
        return HTMLResponse(html)

    async def ca(_request) -> Response:
        return Response(ca_path.read_bytes(), media_type="application/x-x509-ca-cert",
                        headers={"Content-Disposition": 'attachment; filename="ReachyTeleopCA.crt"'})

    return Starlette(routes=[Route("/", home), Route("/ca.crt", ca)])

