#!/usr/bin/env python3
import argparse
import gzip
import os
import socket
import subprocess
import sys
import tempfile
import time


SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
WORKER_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, "..", "raspberry_worker"))
TOOLS_DIR = SCRIPT_DIR
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


def spawn_local_worker(host, port, export_path, export_max_bytes):
    command = [
        sys.executable,
        os.path.join(WORKER_DIR, "worker.py"),
        "--host",
        host,
        "--port",
        str(port),
        "--label",
        "history-rotation-smoke",
        "--report-interval",
        "0",
        "--zone-history-limit",
        "16",
        "--history-export-path",
        export_path,
        "--history-export-mode",
        "full",
        "--history-export-max-bytes",
        str(export_max_bytes),
        "--history-export-max-files",
        "3",
        "--history-export-compress-rotated",
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


def count_gzip_lines(path):
    with gzip.open(path, "rt", encoding="utf-8") as handle:
        return sum(1 for line in handle if line.strip())


def main():
    parser = argparse.ArgumentParser(description="Smoke test for rotated/compressed worker history export")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=7904)
    parser.add_argument("--export-max-bytes", type=int, default=350)
    args = parser.parse_args()

    with tempfile.TemporaryDirectory(prefix="cluster-history-rotation-") as temp_dir:
        export_path = os.path.join(temp_dir, "zone-history.jsonl")
        worker_process = spawn_local_worker(args.host, args.port, export_path, args.export_max_bytes)
        sock = None

        try:
            sock = socket.create_connection((args.host, args.port), timeout=1.0)
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            sock.settimeout(None)

            send_frame(sock, MessageType.HELLO, encode_hello(label="zone-history-rotation-smoke", capabilities=0, max_zones=8))
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

            request = SimulateRequest(request_id=1, zone_id=0, snapshot_id=1, tick=1, delta_time=0.05, player_x=0.0, player_y=0.0)
            send_frame(sock, MessageType.SIMULATE, encode_simulate(request))
            result = receive_result(sock)
            by_id = {entity.entity_id: entity for entity in result.entities}

            entity_three = EntityState(entity_id=3, x=0.0, y=2.5, vx=0.0, vy=0.0, speed=1.8, state=int(EntityStateCode.WANDER), flags=0)
            patched_entities = [by_id[2], entity_three]
            patch = ZoneSnapshotPatch(
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
            send_frame(sock, MessageType.SNAPSHOT_PATCH, encode_snapshot_patch(patch))

            tick = 2
            request_id = 2
            for _ in range(6):
                request = SimulateRequest(request_id=request_id, zone_id=0, snapshot_id=2, tick=tick, delta_time=0.05, player_x=0.0, player_y=0.0)
                send_frame(sock, MessageType.SIMULATE, encode_simulate(request))
                receive_result(sock)
                request_id += 1
                tick += 1

            time.sleep(0.3)
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

        rotated_gzip = export_path + ".1.gz"
        if not os.path.exists(rotated_gzip):
            raise RuntimeError("expected at least one rotated gzip export file")

        rotated_lines = count_gzip_lines(rotated_gzip)
        if rotated_lines <= 0:
            raise RuntimeError("rotated gzip export file is empty")

        replay_command = [
            sys.executable,
            os.path.join(TOOLS_DIR, "zone_history_replay.py"),
            "--input",
            export_path,
            "--zone-id",
            "0",
            "--tail",
            "12",
            "--verify-checksums",
        ]
        replay_output = subprocess.check_output(replay_command, text=True)
        if "checksum_failed=0" not in replay_output:
            raise RuntimeError("replay verification reported checksum failures")
        if "files_loaded=" not in replay_output:
            raise RuntimeError("replay output did not report loaded files")

        print("Zone History Rotation Smoke Test")
        print("status=ok")
        print(f"export_path={export_path}")
        print(f"rotated_file={rotated_gzip}")
        print(f"rotated_lines={rotated_lines}")
        print("replay_verified=yes")


if __name__ == "__main__":
    main()
