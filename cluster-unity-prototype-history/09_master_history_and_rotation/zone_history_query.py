#!/usr/bin/env python3
import argparse
import os
import socket
import sys


SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
WORKER_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, "..", "raspberry_worker"))
if WORKER_DIR not in sys.path:
    sys.path.insert(0, WORKER_DIR)

from protocol import (
    MessageType,
    ZoneHistoryReason,
    decode_error,
    decode_welcome,
    decode_zone_history_response,
    encode_hello,
    encode_zone_history_request,
    read_frame,
    send_frame,
)


def format_reason(value):
    try:
        return ZoneHistoryReason(value).name
    except ValueError:
        return f"UNKNOWN_{value}"


def main():
    parser = argparse.ArgumentParser(description="Query recent zone history from a worker")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=7000)
    parser.add_argument("--zone-id", type=int, required=True)
    parser.add_argument("--limit", type=int, default=16)
    parser.add_argument("--label", default="zone-history-query")
    args = parser.parse_args()

    sock = socket.create_connection((args.host, args.port), timeout=1.0)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    sock.settimeout(None)

    try:
        send_frame(sock, MessageType.HELLO, encode_hello(label=args.label, capabilities=0, max_zones=8))
        message_type, payload, _ = read_frame(sock)
        if message_type != MessageType.WELCOME:
            raise RuntimeError(f"expected WELCOME but received {message_type}")
        welcome = decode_welcome(payload)

        send_frame(sock, MessageType.ZONE_HISTORY_REQUEST, encode_zone_history_request(args.zone_id, args.limit))
        message_type, payload, _ = read_frame(sock)
        if message_type == MessageType.ERROR:
            error = decode_error(payload)
            raise RuntimeError(f"worker error {error.code}: {error.message}")
        if message_type != MessageType.ZONE_HISTORY_RESPONSE:
            raise RuntimeError(f"expected ZONE_HISTORY_RESPONSE but received {message_type}")

        response = decode_zone_history_response(payload)
        print("Zone History Query")
        print(f"worker_label={welcome.label}")
        print(f"zone_id={response.zone_id}")
        print(f"entry_count={len(response.entries)}")

        for index, entry in enumerate(response.entries):
            print(
                f"[{index}] reason={format_reason(entry.reason)} "
                f"snapshot_id={entry.snapshot_id} tick={entry.tick} "
                f"checksum={entry.checksum} entities={entry.entity_count} "
                f"removed={entry.removed_count} upsert={entry.upsert_count} "
                f"recorded_at_ms={entry.recorded_at_ms}"
            )
    finally:
        sock.close()


if __name__ == "__main__":
    main()
