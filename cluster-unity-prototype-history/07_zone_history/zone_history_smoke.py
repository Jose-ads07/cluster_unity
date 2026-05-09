#!/usr/bin/env python3
import argparse
import os
import socket
import subprocess
import sys
import time


SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
WORKER_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, "..", "raspberry_worker"))
if WORKER_DIR not in sys.path:
    sys.path.insert(0, WORKER_DIR)

from protocol import (
    EntityState,
    EntityStateCode,
    MessageType,
    ObjectiveType,
    SimulateRequest,
    ZoneHistoryReason,
    ZoneSnapshot,
    ZoneSnapshotPatch,
    compute_zone_checksum,
    decode_error,
    decode_result,
    decode_welcome,
    decode_zone_history_response,
    encode_hello,
    encode_simulate,
    encode_snapshot,
    encode_snapshot_patch,
    encode_zone_history_request,
    read_frame,
    send_frame,
)


def spawn_local_worker(host, port, history_limit):
    command = [
        sys.executable,
        os.path.join(WORKER_DIR, "worker.py"),
        "--host",
        host,
        "--port",
        str(port),
        "--label",
        "history-smoke",
        "--report-interval",
        "0",
        "--zone-history-limit",
        str(history_limit),
    ]
    process = subprocess.Popen(command)
    time.sleep(1.0)
    return process


def receive_payload(sock, expected_type):
    message_type, payload, _ = read_frame(sock)
    if message_type == MessageType.ERROR:
        error = decode_error(payload)
        raise RuntimeError(f"worker error {error.code}: {error.message}")
    if message_type != expected_type:
        raise RuntimeError(f"expected {expected_type} but received {message_type}")
    return payload


def main():
    parser = argparse.ArgumentParser(description="Smoke test for worker zone history support")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=7901)
    parser.add_argument("--history-limit", type=int, default=8)
    parser.add_argument("--spawn-local-worker", action="store_true")
    args = parser.parse_args()

    worker_process = None
    if args.spawn_local_worker:
        worker_process = spawn_local_worker(args.host, args.port, args.history_limit)

    sock = None
    try:
        sock = socket.create_connection((args.host, args.port), timeout=1.0)
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        sock.settimeout(None)

        send_frame(sock, MessageType.HELLO, encode_hello(label="zone-history-smoke", capabilities=0, max_zones=8))
        decode_welcome(receive_payload(sock, MessageType.WELCOME))

        initial_entities = [
            EntityState(entity_id=1, x=-2.0, y=0.0, vx=0.0, vy=0.0, speed=2.0, state=int(EntityStateCode.CHASE), flags=0),
            EntityState(entity_id=2, x=2.0, y=0.0, vx=0.0, vy=0.0, speed=2.2, state=int(EntityStateCode.CHASE), flags=1),
        ]
        snapshot = ZoneSnapshot(
            zone_id=0,
            snapshot_id=1,
            tick=0,
            grid_x=0,
            grid_y=0,
            objective_type=int(ObjectiveType.SEEK),
            target_x=0.0,
            target_y=0.0,
            aux_x=0.0,
            aux_y=0.0,
            checksum=compute_zone_checksum(0, 0, initial_entities),
            entities=initial_entities,
        )
        send_frame(sock, MessageType.SNAPSHOT, encode_snapshot(snapshot))

        request_one = SimulateRequest(request_id=1, zone_id=0, snapshot_id=1, tick=1, delta_time=0.05, player_x=0.0, player_y=0.0)
        send_frame(sock, MessageType.SIMULATE, encode_simulate(request_one))
        result_one = decode_result(receive_payload(sock, MessageType.RESULT))
        result_one_entities = {entity.entity_id: entity for entity in result_one.entities}

        entity_three = EntityState(entity_id=3, x=0.0, y=2.5, vx=0.0, vy=0.0, speed=1.8, state=int(EntityStateCode.WANDER), flags=0)
        patched_entities = [result_one_entities[2], entity_three]
        snapshot_patch = ZoneSnapshotPatch(
            zone_id=0,
            base_snapshot_id=1,
            snapshot_id=2,
            tick=1,
            grid_x=0,
            grid_y=0,
            objective_type=int(ObjectiveType.SEEK),
            target_x=0.0,
            target_y=0.0,
            aux_x=0.0,
            aux_y=0.0,
            checksum=compute_zone_checksum(0, 1, patched_entities),
            removed_entity_ids=[1],
            upsert_entities=[entity_three],
        )
        send_frame(sock, MessageType.SNAPSHOT_PATCH, encode_snapshot_patch(snapshot_patch))

        request_two = SimulateRequest(request_id=2, zone_id=0, snapshot_id=2, tick=2, delta_time=0.05, player_x=0.0, player_y=0.0)
        send_frame(sock, MessageType.SIMULATE, encode_simulate(request_two))
        decode_result(receive_payload(sock, MessageType.RESULT))

        send_frame(sock, MessageType.ZONE_HISTORY_REQUEST, encode_zone_history_request(0, args.history_limit))
        history = decode_zone_history_response(receive_payload(sock, MessageType.ZONE_HISTORY_RESPONSE))

        reasons = [entry.reason for entry in history.entries]
        expected_reasons = [
            int(ZoneHistoryReason.SNAPSHOT_FULL),
            int(ZoneHistoryReason.SIMULATE),
            int(ZoneHistoryReason.SNAPSHOT_PATCH),
            int(ZoneHistoryReason.SIMULATE),
        ]
        if reasons != expected_reasons:
            raise RuntimeError(f"unexpected history reasons {reasons}, expected {expected_reasons}")

        if history.entries[-1].tick != 2 or history.entries[-1].snapshot_id != 2:
            raise RuntimeError("history tail does not match latest simulated state")

        print("Zone History Smoke Test")
        print("status=ok")
        print(f"zone_id={history.zone_id}")
        print(f"entry_count={len(history.entries)}")
        print(f"reasons={reasons}")
        print(f"last_tick={history.entries[-1].tick}")
        print(f"last_snapshot_id={history.entries[-1].snapshot_id}")
    finally:
        if sock is not None:
            try:
                sock.close()
            except Exception:
                pass

        if worker_process is not None:
            worker_process.terminate()
            try:
                worker_process.wait(timeout=2.0)
            except subprocess.TimeoutExpired:
                worker_process.kill()


if __name__ == "__main__":
    main()
