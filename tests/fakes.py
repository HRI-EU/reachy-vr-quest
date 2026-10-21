"""Fake Reachy daemon + fake GStreamer signaling server + uvicorn runner for server tests."""

from __future__ import annotations

import asyncio
import contextlib
import json

import numpy as np
import socket
import time

import uvicorn
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.routing import Route, WebSocketRoute
from starlette.websockets import WebSocket, WebSocketDisconnect
from websockets.asyncio.server import serve as ws_serve


def bound_socket(host: str = "127.0.0.1") -> socket.socket:
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind((host, 0))
    return sock


def free_port() -> int:
    with bound_socket() as s:
        return s.getsockname()[1]


class _TestServer(uvicorn.Server):
    """uvicorn polls should_exit every 100 ms; poll faster so test teardown is quick."""

    async def main_loop(self) -> None:
        await self.on_tick(0)
        while not self.should_exit:
            await asyncio.sleep(0.005)


@contextlib.asynccontextmanager
async def run_uvicorn(app, **config):
    """Serve an ASGI app on a free localhost port; yields the port."""
    sock = bound_socket()
    port = sock.getsockname()[1]
    server = _TestServer(uvicorn.Config(app, log_level="warning", lifespan="on", **config))
    server.install_signal_handlers = lambda: None  # uvicorn < 0.29 compat
    task = asyncio.create_task(server.serve(sockets=[sock]))
    try:
        await wait_for(lambda: server.started or task.done(), timeout=5)
        if task.done():
            task.result()
        yield port
    finally:
        server.should_exit = True
        with contextlib.suppress(Exception):
            await asyncio.wait_for(task, 5)
        sock.close()


async def wait_for(pred, timeout: float = 3.0, interval: float = 0.005):
    deadline = time.monotonic() + timeout
    while True:
        value = pred()
        if value:
            return value
        if time.monotonic() > deadline:
            raise TimeoutError("condition not met in time")
        await asyncio.sleep(interval)


async def wait_quiet(counter, quiet: float = 0.15, timeout: float = 3.0) -> int:
    """Wait until counter() stops changing for `quiet` seconds; returns its value."""
    deadline = time.monotonic() + timeout
    last, since = counter(), time.monotonic()
    while time.monotonic() < deadline:
        await asyncio.sleep(0.01)
        now_value = counter()
        if now_value != last:
            last, since = now_value, time.monotonic()
        elif time.monotonic() - since >= quiet:
            return last
    raise TimeoutError("counter kept changing")


class FakeDaemon:
    """Records set_target frames and REST calls; answers the state endpoints."""

    def __init__(self):
        from reachy_mini.daemon.app.models import FullBodyTarget

        self._model = FullBodyTarget
        self.head = {"x": 0.0, "y": 0.0, "z": 0.0, "roll": 0.0, "pitch": 0.0, "yaw": 0.0}
        self.body = 0.0
        self.state_delay_s = 0.0
        self.state_status = 200
        self.action_status = 200
        self.frames: list[dict] = []
        self.frame_times: list[float] = []
        self.invalid: list[str] = []
        self.posts: list[str] = []
        self.state_gets: list[str] = []
        self.ws_connections = 0
        self.reply_error_on_frame = False
        self._sockets: list[WebSocket] = []
        # Robot model: motor mode, running daemon moves, and whether the present
        # head pose follows received targets (like a real, enabled robot).
        self.motors = "enabled"
        self.running: list[str] = []
        self.move_duration_s = 0.0  # >0: wake_up/goto_sleep show up in /move/running this long
        self.follow = True
        self.follow_lag_s = 0.0  # present pose reaches a received target this much later
        self.action_delay_s = 0.0  # time between a POST arriving and the move being registered
        # Pose the robot ends in after a play move, e.g. {"wake_up": {"yaw": 0.0}}.
        self.move_end_head: dict[str, dict] = {}
        self.move_end_body: dict[str, float] = {}
        self.desync_events = 0

        async def motors_status(request: Request):
            self.state_gets.append(request.url.path)
            if self.state_status != 200:
                return JSONResponse({"detail": "boom"}, status_code=self.state_status)
            return JSONResponse({"mode": self.motors})

        async def move_running(request: Request):
            self.state_gets.append(request.url.path)
            if self.state_status != 200:
                return JSONResponse({"detail": "boom"}, status_code=self.state_status)
            return JSONResponse([{"uuid": u} for u in self.running])

        async def head_pose(request: Request):
            self.state_gets.append(request.url.path)
            await asyncio.sleep(self.state_delay_s)
            if self.state_status != 200:
                return JSONResponse({"detail": "boom"}, status_code=self.state_status)
            return JSONResponse(self.head)

        async def body_yaw(request: Request):
            self.state_gets.append(request.url.path)
            if self.state_status != 200:
                return JSONResponse({"detail": "boom"}, status_code=self.state_status)
            return JSONResponse(self.body)

        async def action(request: Request):
            self.posts.append(request.url.path)
            if self.action_status != 200:
                return JSONResponse({"detail": "nope"}, status_code=self.action_status)
            path = request.url.path
            if self.action_delay_s:
                await asyncio.sleep(self.action_delay_s)
            if path.endswith("/set_mode/enabled"):
                self.motors = "enabled"
            elif path.endswith("/set_mode/disabled"):
                self.motors = "disabled"
            elif "/move/play/" in path:
                name = path.rsplit("/", 1)[-1]
                if self.move_duration_s > 0:
                    uuid = f"move-{len(self.posts)}"
                    self.running.append(uuid)
                    asyncio.get_running_loop().call_later(self.move_duration_s, self._end_move, uuid, name)
                else:
                    self._end_move(None, name)
            return JSONResponse({"uuid": "1234"} if "/move/" in request.url.path else {"status": "ok"})

        async def set_target(ws: WebSocket):
            await ws.accept()
            self.ws_connections += 1
            self._sockets.append(ws)
            try:
                while True:
                    data = await ws.receive_text()
                    try:
                        self._model.model_validate_json(data)
                    except Exception as exc:  # noqa: BLE001
                        self.invalid.append(data)
                        await ws.send_text(json.dumps({"status": "error", "detail": str(exc)}))
                        continue
                    frame = json.loads(data)
                    self.frames.append(frame)
                    if self.follow and self.motors == "enabled" and not self.running:
                        if self.follow_lag_s:
                            asyncio.get_running_loop().call_later(self.follow_lag_s, self._apply_if_free, frame)
                        else:
                            self._apply(frame)
                    self.frame_times.append(time.monotonic())
                    if self.reply_error_on_frame:
                        await ws.send_text(json.dumps({"status": "error", "detail": "fake rejection"}))
            except WebSocketDisconnect:
                pass
            finally:
                self._sockets.remove(ws)

        self.app = Starlette(routes=[
            Route("/api/state/present_head_pose", head_pose),
            Route("/api/state/present_body_yaw", body_yaw),
            Route("/api/motors/status", motors_status),
            Route("/api/move/running", move_running),
            Route("/api/move/play/wake_up", action, methods=["POST"]),
            Route("/api/move/play/goto_sleep", action, methods=["POST"]),
            Route("/api/motors/set_mode/{mode}", action, methods=["POST"]),
            WebSocketRoute("/api/move/ws/set_target", set_target),
        ])

    def _end_move(self, uuid, name: str) -> None:
        if uuid is not None:
            self.running.remove(uuid)
        self.head.update(self.move_end_head.get(name, {}))
        if name in self.move_end_body:
            self.body = self.move_end_body[name]

    def _apply_if_free(self, frame: dict) -> None:
        if self.motors == "enabled" and not self.running:
            self._apply(frame)

    def _apply(self, frame: dict) -> None:
        from scipy.spatial.transform import Rotation

        m = frame.get("target_head_pose", {}).get("m")
        if m:
            pose = np.array(m).reshape(4, 4)
            roll, pitch, yaw = Rotation.from_matrix(pose[:3, :3]).as_euler("xyz")
            self.head.update(x=pose[0, 3], y=pose[1, 3], z=pose[2, 3], roll=roll, pitch=pitch, yaw=yaw)
        if frame.get("target_body_yaw") is not None:
            self.body = frame["target_body_yaw"]

    async def drop_clients(self):
        for ws in list(self._sockets):
            with contextlib.suppress(Exception):
                await ws.close()


class FakeSignal:
    """GStreamer-signaling stand-in: sends welcome, echoes text as {'echo': ...}, records traffic."""

    def __init__(self):
        self.received: list[str | bytes] = []
        self.connections = 0
        self.closed = 0
        self.port = None
        self._server = None
        self.peers = []

    async def _handler(self, ws):
        self.connections += 1
        self.peers.append(ws)
        try:
            await ws.send(json.dumps({"type": "welcome", "peerId": "peer-1"}))
            async for message in ws:
                self.received.append(message)
                if isinstance(message, bytes):
                    await ws.send(message[::-1])
                elif message == "close-me":
                    await ws.close(code=1000)
                    return
                else:
                    await ws.send(json.dumps({"echo": message}))
        except Exception:  # noqa: BLE001
            pass
        finally:
            self.closed += 1

    async def __aenter__(self):
        self._server = await ws_serve(self._handler, "127.0.0.1", 0)
        self.port = self._server.sockets[0].getsockname()[1]
        return self

    async def __aexit__(self, *exc):
        self._server.close()
        await self._server.wait_closed()
