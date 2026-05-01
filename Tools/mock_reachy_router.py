#!/usr/bin/env python3
"""Small ROUTER endpoint for testing ReachyMiniTeleop without a robot."""

from __future__ import annotations

import argparse
import json
import time

import zmq


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bind", default="tcp://*:40000", help="ROUTER bind endpoint")
    args = parser.parse_args()

    context = zmq.Context.instance()
    socket = context.socket(zmq.ROUTER)
    socket.bind(args.bind)
    print(f"[mock_reachy_router] listening on {args.bind}")

    try:
        while True:
            frames = socket.recv_multipart()
            if len(frames) < 2:
                print(f"[mock_reachy_router] short message: {frames!r}")
                continue

            identity = frames[0]
            payload_frame = frames[2] if len(frames) >= 3 and frames[1] == b"" else frames[1]
            payload_text = payload_frame.decode("utf-8", errors="replace")

            try:
                payload = json.loads(payload_text)
                print(f"[{time.strftime('%H:%M:%S')}] {identity.decode(errors='replace')}: {json.dumps(payload, indent=2)}")
            except json.JSONDecodeError:
                print(f"[{time.strftime('%H:%M:%S')}] {identity.decode(errors='replace')}: {payload_text}")

            socket.send_multipart([identity, b"", b'{"type":"ack"}'])
    except KeyboardInterrupt:
        print("\n[mock_reachy_router] stopped")
    finally:
        socket.close(0)
        context.term()


if __name__ == "__main__":
    main()

