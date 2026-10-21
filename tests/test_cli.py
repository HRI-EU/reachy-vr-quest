import asyncio
import json
import signal
import ssl
import subprocess
import sys
from pathlib import Path

import httpx
import pytest
from websockets.asyncio.client import connect

from fakes import free_port, wait_for
from phone_teleop.__main__ import parse_args

REPO = Path(__file__).resolve().parent.parent


def test_help():
    out = subprocess.run([sys.executable, "-m", "phone_teleop", "--help"], cwd=REPO,
                         capture_output=True, text=True, timeout=30)
    assert out.returncode == 0
    for flag in ["--robot", "--rate", "--catchup-speed", "--smoothing", "--max-speed", "--deadman-timeout",
                 "--body-follow", "--no-body", "--log-csv", "--port", "--setup-port", "--cert-dir"]:
        assert flag in out.stdout


def test_defaults_match_readme():
    a = parse_args([])
    assert (a.robot, a.port, a.setup_port, a.rate, a.smoothing, a.max_speed, a.deadman_timeout,
            a.body_follow, a.no_body) == ("192.168.50.95", 4443, 8080, 50.0, 0.5, 180.0, 0.3, 55.0, False)
    assert a.catchup_speed == 60.0
    assert parse_args(["--catchup-speed", "30"]).catchup_speed == 30.0


def test_local_ips_and_phone_ip():
    import ipaddress

    from phone_teleop.__main__ import local_ipv4s, phone_ip, route_ip

    ips = local_ipv4s()
    for ip in ips:
        assert not ipaddress.ip_address(ip).is_loopback
    assert route_ip("127.0.0.1") == "127.0.0.1"
    assert route_ip("no-such-host.invalid") is None
    ipaddress.ip_address(phone_ip("no-such-host.invalid"))  # falls back to a usable address
    assert phone_ip("127.0.0.1") == "127.0.0.1"


async def test_real_start_over_https(fake_daemon, fake_signal, tmp_path):
    port, setup_port = free_port(), free_port()
    cert_dir = tmp_path / "certs"
    proc = await asyncio.create_subprocess_exec(
        sys.executable, "-m", "phone_teleop", "--robot", "127.0.0.1",
        "--robot-port", str(fake_daemon.port), "--signal-port", str(fake_signal.port),
        "--port", str(port), "--setup-port", str(setup_port), "--host", "127.0.0.1",
        "--cert-dir", str(cert_dir),
        cwd=REPO, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT)
    output = []
    try:
        async def read_until_ready():
            while True:
                line = await proc.stdout.readline()
                if not line:
                    raise RuntimeError("exited early:\n" + "".join(output))
                output.append(line.decode())
                if "lag clock" in output[-1]:
                    return

        await asyncio.wait_for(read_until_ready(), 20)
        drain = asyncio.create_task(proc.stdout.read())
        ctx = ssl.create_default_context(cafile=str(cert_dir / "rootCA.crt"))

        # HTTPS page, verified against our CA (wait until uvicorn is listening).
        async with httpx.AsyncClient(verify=ctx) as client:
            async def get_page():
                try:
                    return await client.get(f"https://localhost:{port}/")
                except httpx.ConnectError:
                    return None
            r = None
            for _ in range(200):
                r = await get_page()
                if r is not None:
                    break
                await asyncio.sleep(0.05)
            assert r is not None and r.status_code == 200
            r = await client.get(f"http://127.0.0.1:{setup_port}/ca.crt")
            assert r.content == (cert_dir / "rootCA.crt").read_bytes()

        async with connect(f"wss://localhost:{port}/pose", ssl=ctx) as ws:
            async def send(msg):
                await ws.send(json.dumps(msg))

            await send({"type": "orient", "a": 0, "b": 0, "g": -90, "held": False, "seq": 0, "t": 0})
            await send({"type": "calibrate"})
            while True:
                m = json.loads(await asyncio.wait_for(ws.recv(), 5))
                if m["type"] == "calibrated":
                    assert m["ok"]
                    break

            async def stream():
                i = 0
                while True:
                    i += 1
                    await send({"type": "orient", "a": 10, "b": 0, "g": -90, "held": True, "seq": i, "t": 0})
                    await asyncio.sleep(0.01)

            streamer = asyncio.create_task(stream())
            await wait_for(lambda: len(fake_daemon.frames) >= 10, timeout=10)
            streamer.cancel()
        assert fake_daemon.invalid == []

        # the cert covers every local IPv4 + localhost + .local name
        from cryptography import x509
        from phone_teleop.__main__ import local_hostname, local_ipv4s
        leaf = x509.load_pem_x509_certificate((cert_dir / "server.crt").read_bytes())
        san = leaf.extensions.get_extension_for_class(x509.SubjectAlternativeName).value
        ips = {str(i) for i in san.get_values_for_type(x509.IPAddress)}
        names = set(san.get_values_for_type(x509.DNSName))
        assert {"127.0.0.1", *local_ipv4s()} <= ips, ips
        assert {"localhost", local_hostname()} <= names, names

        async with connect(f"wss://localhost:{port}/signal", ssl=ctx) as ws:
            assert json.loads(await asyncio.wait_for(ws.recv(), 5))["type"] == "welcome"

        # Ctrl+C must stop both servers (HTTPS + bootstrap) and exit.
        proc.send_signal(signal.SIGINT)
        try:
            await asyncio.wait_for(proc.wait(), 8)
        except TimeoutError:
            pytest.fail("python -m phone_teleop did not exit on SIGINT")
        drain.cancel()
    finally:
        if proc.returncode is None:
            proc.kill()
            await proc.wait()
