"""Optional: teleop against a real `reachy-mini-daemon --sim`.

Run the sim first, e.g.
    .venv/bin/reachy-mini-daemon --sim --headless --no-media --fastapi-port 18000
then
    REACHY_SIM_PORT=18000 .venv/bin/python -m pytest tests/test_sim.py
"""

import asyncio
import json
import math
import os

import httpx
import pytest
from websockets.asyncio.client import connect

from fakes import run_uvicorn, wait_for

SIM_PORT = os.environ.get("REACHY_SIM_PORT")
pytestmark = pytest.mark.skipif(not SIM_PORT, reason="set REACHY_SIM_PORT to a running sim daemon")


async def present(client):
    head = (await client.get("/api/state/present_head_pose")).json()
    body = (await client.get("/api/state/present_body_yaw")).json()
    return head, body


@pytest.mark.parametrize("target_yaw, target_pitch, body_expected", [(30.0, 15.0, 0.0), (100.0, -10.0, 45.0)])
async def test_sim_follows_phone(target_yaw, target_pitch, body_expected):
    from phone_teleop.server import create_app
    from phone_teleop.teleop import RobotLink, TeleopController, TeleopSettings

    controller = TeleopController(TeleopSettings())
    link = RobotLink("127.0.0.1", int(SIM_PORT))
    app = create_app(controller, link, signal_url="ws://127.0.0.1:9")
    async with httpx.AsyncClient(base_url=f"http://127.0.0.1:{SIM_PORT}", timeout=5) as daemon, \
            run_uvicorn(app) as port:
        await daemon.post("/api/motors/set_mode/enabled")
        await daemon.post("/api/move/goto", json={"head_pose": {"x": 0, "y": 0, "z": 0, "roll": 0, "pitch": 0, "yaw": 0},
                                                  "body_yaw": 0.0, "duration": 0.5})
        await asyncio.sleep(0.7)
        await wait_for(lambda: link.connected, 5)
        async with connect(f"ws://127.0.0.1:{port}/pose") as ws:
            async def send(m):
                await ws.send(json.dumps(m))

            await send({"type": "orient", "a": 0, "b": 0, "g": -90, "held": False})
            await send({"type": "calibrate"})
            # Turn left by target_yaw, look down by target_pitch (gamma = -90 + pitch in this grip).

            async def stream():
                while True:
                    await send({"type": "orient", "a": target_yaw, "b": 0, "g": -90 + target_pitch, "held": True})
                    await asyncio.sleep(0.02)

            task = asyncio.create_task(stream())
            try:
                for _ in range(100):
                    head, body = await present(daemon)
                    if abs(math.degrees(head["yaw"]) - target_yaw) < 3 and \
                            abs(math.degrees(head["pitch"]) - target_pitch) < 3 and \
                            abs(math.degrees(body) - body_expected) < 3:
                        break
                    await asyncio.sleep(0.05)
            finally:
                task.cancel()
            assert link.last_error == "", link.last_error
            assert abs(math.degrees(head["yaw"]) - target_yaw) < 3, head
            assert abs(math.degrees(head["pitch"]) - target_pitch) < 3, head
            assert abs(math.degrees(head["roll"])) < 3, head
            assert abs(math.degrees(body) - body_expected) < 3, math.degrees(body)
