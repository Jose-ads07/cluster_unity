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
    ZoneSnapshot,
    ZoneSnapshotPatch,
    compute_zone_checksum,
    decode_error,
    decode_result,
    decode_welcome,
    encode_hello,
    encode_simulate,
    encode_snapshot,
    encode_snapshot_patch,
    read_frame,
    send_frame,
)


def spawn_local_worker(host, port):
    command = [
        sys.executable,
        os.path.join(WORKER_DIR, "worker.py"),
        "--host",
        host,
        "--port",
        str(port),
        "--label",
        "patch-smoke",
        "--report-interval",
        "0",
    ]
    process = subprocess.Popen(command)
    time.sleep(1.0)
    return process


def receive_result(sock):
    message_type, payload, _ = read_frame(sock)
    if message_type == MessageType.ERROR:
        error = decode_error(payload)
        raise RuntimeError(f"worker error {error.code}: {error.message}")
    if message_type != MessageType.RESULT:
        raise RuntimeError(f"expected RESULT but received {message_type}")
    return decode_result(payload)


def main():
    parser = argparse.ArgumentParser(description="Smoke test for snapshot patch support")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=7900)
    parser.add_argument("--spawn-local-worker", action="store_true")
    args = parser.parse_args()

    worker_process = None
    if args.spawn_local_worker:
        worker_process = spawn_local_worker(args.host, args.port)

    sock = None
    try:
        sock = socket.create_connection((args.host, args.port), timeout=1.0)
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        sock.settimeout(None)

        send_frame(sock, MessageType.HELLO, encode_hello(label="snapshot-patch-smoke", capabilities=0, max_zones=8))
        message_type, payload, _ = read_frame(sock)
        if message_type != MessageType.WELCOME:
            raise RuntimeError(f"expected WELCOME but received {message_type}")
        decode_welcome(payload)

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

        request_one = SimulateRequest(
            request_id=1,
            zone_id=0,
            snapshot_id=1,
            tick=1,
            delta_time=0.05,
            player_x=0.0,
            player_y=0.0,
        )
        send_frame(sock, MessageType.SIMULATE, encode_simulate(request_one))
        result_one = receive_result(sock)
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

        request_two = SimulateRequest(
            request_id=2,
            zone_id=0,
            snapshot_id=2,
            tick=2,
            delta_time=0.05,
            player_x=0.0,
            player_y=0.0,
        )
        send_frame(sock, MessageType.SIMULATE, encode_simulate(request_two))
        result_two = receive_result(sock)
        result_two_ids = sorted(entity.entity_id for entity in result_two.entities)

        if result_two_ids != [2, 3]:
            raise RuntimeError(f"unexpected entity ids after patch: {result_two_ids}")

        print("Snapshot Patch Smoke Test")
        print("status=ok")
        print(f"zone_id={result_two.zone_id}")
        print(f"snapshot_id={result_two.snapshot_id}")
        print(f"tick={result_two.tick}")
        print(f"entity_ids={result_two_ids}")
        print(f"checksum={result_two.checksum}")
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
