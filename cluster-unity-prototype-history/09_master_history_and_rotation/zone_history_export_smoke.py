#!/usr/bin/env python3
import argparse
import json
import os
import socket
import subprocess
import sys
import tempfile
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


def spawn_local_worker(host, port, export_path, export_max_bytes=0, compress_rotated=False):
    command = [
        sys.executable,
        os.path.join(WORKER_DIR, "worker.py"),
        "--host",
        host,
        "--port",
        str(port),
        "--label",
        "history-export-smoke",
        "--report-interval",
        "0",
        "--zone-history-limit",
        "8",
        "--history-export-path",
        export_path,
        "--history-export-mode",
        "full",
    ]
    if export_max_bytes > 0:
        command.extend(["--history-export-max-bytes", str(export_max_bytes), "--history-export-max-files", "3"])
    if compress_rotated:
        command.append("--history-export-compress-rotated")
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


def read_export_records(path):
    records = []
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                records.append(json.loads(line))
    return records


def main():
    parser = argparse.ArgumentParser(description="Smoke test for worker history export")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=7903)
    parser.add_argument("--export-path", default="")
    parser.add_argument("--keep-export-file", action="store_true")
    parser.add_argument("--export-max-bytes", type=int, default=0)
    parser.add_argument("--compress-rotated", action="store_true")
    args = parser.parse_args()

    temp_dir = None
    if args.export_path:
        export_path = os.path.abspath(args.export_path)
        export_dir = os.path.dirname(export_path)
        if export_dir:
            os.makedirs(export_dir, exist_ok=True)
    else:
        temp_dir = tempfile.TemporaryDirectory(prefix="cluster-history-export-")
        export_path = os.path.join(temp_dir.name, "zone-history.jsonl")

    if os.path.exists(export_path):
        os.remove(export_path)

    try:
        worker_process = spawn_local_worker(args.host, args.port, export_path, export_max_bytes=args.export_max_bytes, compress_rotated=args.compress_rotated)
        sock = None

        try:
            sock = socket.create_connection((args.host, args.port), timeout=1.0)
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            sock.settimeout(None)

            send_frame(sock, MessageType.HELLO, encode_hello(label="zone-history-export-smoke", capabilities=0, max_zones=8))
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

            request_one = SimulateRequest(request_id=1, zone_id=0, snapshot_id=1, tick=1, delta_time=0.05, player_x=0.0, player_y=0.0)
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

            request_two = SimulateRequest(request_id=2, zone_id=0, snapshot_id=2, tick=2, delta_time=0.05, player_x=0.0, player_y=0.0)
            send_frame(sock, MessageType.SIMULATE, encode_simulate(request_two))
            receive_result(sock)

            time.sleep(0.2)
        finally:
            if sock is not None:
                try:
                    sock.close()
                except Exception:
                    pass

            worker_process.terminate()
            try:
                worker_process.wait(timeout=2.0)
            except subprocess.TimeoutExpired:
                worker_process.kill()

        records = read_export_records(export_path)
        reasons = [record["reason_name"] for record in records]
        expected = ["SNAPSHOT_FULL", "SIMULATE", "SNAPSHOT_PATCH", "SIMULATE"]
        if reasons != expected:
            raise RuntimeError(f"unexpected exported reasons {reasons}, expected {expected}")

        if any("entities" not in record for record in records):
            raise RuntimeError("expected full export mode to include entity payloads")

        last_record = records[-1]
        if int(last_record["snapshot_id"]) != 2 or int(last_record["tick"]) != 2:
            raise RuntimeError("last exported record does not match latest simulation state")

        print("Zone History Export Smoke Test")
        print("status=ok")
        print(f"export_path={export_path}")
        print(f"record_count={len(records)}")
        print(f"reasons={reasons}")
        print(f"last_tick={last_record['tick']}")
        print(f"last_snapshot_id={last_record['snapshot_id']}")
    finally:
        if temp_dir is not None and not args.keep_export_file:
            temp_dir.cleanup()


if __name__ == "__main__":
    main()
