"""python -m phone_teleop --robot 192.168.50.95"""

from __future__ import annotations

import argparse
import asyncio
import logging
import re
import socket
import subprocess
from pathlib import Path

import uvicorn

from .certs import CA_CERT, ensure_server_cert
from .server import create_app, create_bootstrap_app
from .teleop import RobotLink, TeleopController, TeleopSettings

REPO_ROOT = Path(__file__).resolve().parent.parent


def route_ip(destination: str) -> str | None:
    """The laptop's own address on the interface that routes to destination (no packets sent)."""
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        try:
            s.connect((socket.gethostbyname(destination), 1))
            return s.getsockname()[0]
        except OSError:
            return None


def local_ipv4s() -> list[str]:
    """All IPv4 addresses of this machine (e.g. wired robot LAN + internet Wi-Fi)."""
    found: list[str] = []
    try:
        out = subprocess.run(["ifconfig"], capture_output=True, text=True, timeout=3).stdout
        found = re.findall(r"inet (\d+\.\d+\.\d+\.\d+)", out)
    except (OSError, subprocess.SubprocessError):
        pass
    return [ip for ip in dict.fromkeys(found) if not ip.startswith("127.")]


def phone_ip(robot: str) -> str:
    """The address the phone should use: the one on the robot's network, if there is one."""
    return route_ip(robot) or route_ip("10.255.255.255") or "127.0.0.1"


def local_hostname() -> str:
    name = socket.gethostname().split(".")[0]
    return f"{name}.local"


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    d = TeleopSettings()
    p = argparse.ArgumentParser(prog="python -m phone_teleop", description=__doc__)
    p.add_argument("--robot", default="192.168.50.95", help="Reachy daemon host/IP")
    p.add_argument("--robot-port", type=int, default=8000, help="daemon REST/WebSocket port")
    p.add_argument("--signal-port", type=int, default=8443, help="robot GStreamer signaling port")
    p.add_argument("--port", type=int, default=4443, help="HTTPS port for the phone")
    p.add_argument("--setup-port", type=int, default=8080, help="plain HTTP port serving the root CA")
    p.add_argument("--host", default="0.0.0.0", help="bind address")
    p.add_argument("--cert-dir", type=Path, default=REPO_ROOT / ".certs")
    p.add_argument("--extra-host", action="append", default=[],
                   help="extra IP/DNS name to put in the server cert (repeatable)")
    p.add_argument("--rate", type=float, default=d.send_rate_hz, help="pose send rate (Hz)")
    p.add_argument("--smoothing", type=float, default=d.smoothing, help="0-1, 1 = no smoothing")
    p.add_argument("--max-speed", type=float, default=d.max_speed_deg_s, help="deg/s cap per axis")
    p.add_argument("--catchup-speed", type=float, default=d.catchup_speed_deg_s,
                   help="deg/s right after engaging, until within 3 deg of the phone")
    p.add_argument("--deadman-timeout", type=float, default=d.deadman_timeout_s, help="seconds")
    p.add_argument("--body-follow", type=float, default=d.body_follow_deg,
                   help="head-vs-body yaw (deg) before the body turns")
    p.add_argument("--no-body", action="store_true", help="never move the body")
    p.add_argument("--log-csv", type=Path, help="append every orientation sample to this CSV")
    p.add_argument("-v", "--verbose", action="store_true")
    return p.parse_args(argv)


async def serve(args: argparse.Namespace) -> None:
    ip = phone_ip(args.robot)
    others = [a for a in local_ipv4s() if a != ip]
    # Every local address goes in the cert, so the phone can use whichever network it's on.
    hosts = [ip, local_hostname(), *others, "localhost", "127.0.0.1", *args.extra_host]
    cert, key = ensure_server_cert(args.cert_dir, hosts)
    https_url = f"https://{ip}:{args.port}/"

    settings = TeleopSettings(
        send_rate_hz=args.rate, smoothing=args.smoothing, max_speed_deg_s=args.max_speed,
        deadman_timeout_s=args.deadman_timeout, body_follow_deg=args.body_follow,
        body_follow=not args.no_body, catchup_speed_deg_s=args.catchup_speed,
    )
    link = RobotLink(args.robot, args.robot_port)
    app = create_app(TeleopController(settings), link,
                     signal_url=f"ws://{args.robot}:{args.signal_port}", csv_path=args.log_csv)
    level = "debug" if args.verbose else "warning"
    main = uvicorn.Server(uvicorn.Config(app, host=args.host, port=args.port, log_level=level,
                                         ssl_certfile=str(cert), ssl_keyfile=str(key),
                                         ws_ping_interval=5, ws_ping_timeout=5))
    setup = uvicorn.Server(uvicorn.Config(create_bootstrap_app(args.cert_dir / CA_CERT, https_url),
                                          host=args.host, port=args.setup_port, log_level=level))

    print(f"""
Reachy phone teleop
  robot daemon : {link.api_url}
  phone (HTTPS): {https_url}
  also works as: https://{local_hostname()}:{args.port}/{"".join(f"  https://{a}:{args.port}/" for a in others)}
  first time   : open http://{ip}:{args.setup_port}/ on the iPhone to install the certificate
  lag clock    : {https_url}clock

The phone must be on the same network as the robot ({args.robot}); use the address on that network.
""", flush=True)
    await asyncio.gather(main.serve(), setup.serve())


def main(argv: list[str] | None = None) -> None:
    args = parse_args(argv)
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s")
    try:
        asyncio.run(serve(args))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
