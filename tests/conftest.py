import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent))

from fakes import FakeDaemon, FakeSignal, run_uvicorn  # noqa: E402


class Teleop:
    def __init__(self, port, controller, link, app, daemon, signal):
        self.port = port
        self.controller = controller
        self.link = link
        self.app = app
        self.daemon = daemon
        self.signal = signal

    @property
    def hub(self):
        return self.app.state.hub

    def ws(self, path):
        return f"ws://127.0.0.1:{self.port}{path}"

    def http(self, path):
        return f"http://127.0.0.1:{self.port}{path}"


@pytest.fixture
async def fake_daemon():
    daemon = FakeDaemon()
    async with run_uvicorn(daemon.app) as port:
        daemon.port = port
        yield daemon


@pytest.fixture
async def fake_signal():
    async with FakeSignal() as sig:
        yield sig


@pytest.fixture
async def make_teleop(fake_daemon, fake_signal):
    """Factory: start the real teleop app (plain HTTP) against the fakes."""
    from contextlib import AsyncExitStack

    from phone_teleop.server import create_app
    from phone_teleop.teleop import RobotLink, TeleopController, TeleopSettings

    stack = AsyncExitStack()

    async def factory(settings=None, csv_path=None, signal_url=None):
        controller = TeleopController(settings or TeleopSettings())
        link = RobotLink("127.0.0.1", fake_daemon.port)
        app = create_app(controller, link,
                         signal_url=signal_url or f"ws://127.0.0.1:{fake_signal.port}",
                         csv_path=csv_path)
        port = await stack.enter_async_context(run_uvicorn(app))
        return Teleop(port, controller, link, app, fake_daemon, fake_signal)

    yield factory
    await stack.aclose()


@pytest.fixture
async def teleop(make_teleop):
    return await make_teleop()
