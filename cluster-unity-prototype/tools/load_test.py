#!/usr/bin/env python3
import argparse
import math
import os
import queue
import random
import socket
import statistics
import subprocess
import sys
import threading
import time
from dataclasses import dataclass, field

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
WORKER_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, "..", "raspberry_worker"))
if WORKER_DIR not in sys.path:
    sys.path.insert(0, WORKER_DIR)

from protocol import (
    CAPABILITY_DELTA_RESULTS,
    DELTA_SCALE,
    EntityState,
    EntityStateCode,
    MessageType,
    ObjectiveType,
    SimulateRequest,
    ZoneSnapshot,
    compute_zone_checksum,
    decode_error,
    decode_heartbeat,
    decode_result,
    decode_result_delta,
    decode_stats_response,
    decode_welcome,
    encode_hello,
    encode_heartbeat,
    encode_simulate,
    encode_snapshot,
    encode_stats_request,
    quantize_milli,
    read_frame,
    send_frame,
)


@dataclass
class WorkerEndpoint:
    host: str
    port: int
    label: str


@dataclass
class ZoneRuntime:
    zone_id: int
    grid_x: int
    grid_y: int
    snapshot_id: int
    tick: int
    objective_type: int
    target_x: float
    target_y: float
    aux_x: float
    aux_y: float
    entities: list = field(default_factory=list)


class MasterClient:
    def __init__(self, endpoint, queue_ref, prefer_delta_results):
        self.endpoint = endpoint
        self.queue = queue_ref
        self.prefer_delta_results = prefer_delta_results
        self.sock = None
        self.running = False
        self.reader_thread = None
        self.bytes_sent = 0
        self.bytes_received = 0

    def connect(self, timeout_ms):
        self.sock = socket.create_connection((self.endpoint.host, self.endpoint.port), timeout=timeout_ms / 1000.0)
        self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.sock.settimeout(None)
        self.bytes_sent += send_frame(
            self.sock,
            MessageType.HELLO,
            encode_hello(
                label=f"load-test-{self.endpoint.label}",
                capabilities=CAPABILITY_DELTA_RESULTS if self.prefer_delta_results else 0,
                max_zones=1024,
            ),
        )
        message_type, payload, frame_size = read_frame(self.sock)
        self.bytes_received += frame_size
        if message_type != MessageType.WELCOME:
            raise RuntimeError(f"worker {self.endpoint.label} did not complete handshake")
        decode_welcome(payload)

        self.running = True
        self.reader_thread = threading.Thread(target=self._reader_loop, daemon=True)
        self.reader_thread.start()

    def _reader_loop(self):
        try:
            while self.running:
                message_type, payload, frame_size = read_frame(self.sock)
                self.bytes_received += frame_size
                self.queue.put((self.endpoint.label, message_type, payload))
        except EOFError:
            pass
        except Exception as exc:
            self.queue.put((self.endpoint.label, "reader-error", exc))

    def send_snapshot(self, snapshot):
        self.bytes_sent += send_frame(self.sock, MessageType.SNAPSHOT, encode_snapshot(snapshot))

    def send_simulate(self, request):
        self.bytes_sent += send_frame(self.sock, MessageType.SIMULATE, encode_simulate(request))

    def request_stats(self):
        self.bytes_sent += send_frame(self.sock, MessageType.STATS_REQUEST, encode_stats_request())

    def send_heartbeat(self, current_tick):
        self.bytes_sent += send_frame(self.sock, MessageType.HEARTBEAT, encode_heartbeat(current_tick, 0, 0, 0, 0))

    def close(self):
        self.running = False
        try:
            if self.sock is not None:
                self.sock.close()
        except Exception:
            pass


def build_zone_entities(zone_id, entities_per_zone, world_size):
    random.seed(zone_id * 991)
    center_x = ((zone_id % world_size) - (world_size * 0.5)) * 2.0
    center_y = ((zone_id // world_size) - (world_size * 0.5)) * 2.0
    entities = []

    for index in range(entities_per_zone):
        angle = (index / max(1, entities_per_zone)) * math.tau
        radius = 1.0 + ((index % 7) * 0.2)
        x = center_x + math.cos(angle) * radius
        y = center_y + math.sin(angle) * radius
        entities.append(
            EntityState(
                entity_id=(zone_id * 100000) + index,
                x=x,
                y=y,
                vx=0.0,
                vy=0.0,
                speed=1.0 + ((index % 5) * 0.35),
                state=int(EntityStateCode.CHASE),
                flags=1 if index % 5 == 0 else 0,
            )
        )

    return entities


def build_zone_runtime(zone_id, grid_columns, grid_rows, entities_per_zone):
    grid_x = zone_id % grid_columns
    grid_y = zone_id // grid_columns
    objective_type = zone_id % 3
    target_x = (grid_x - (grid_columns * 0.5)) * 3.0
    target_y = (grid_y - (grid_rows * 0.5)) * 3.0
    aux_x = 2.5 + (grid_x * 0.1)
    aux_y = -1.0 if zone_id % 2 == 0 else 1.0
    entities = build_zone_entities(zone_id, entities_per_zone, max(grid_columns, grid_rows))
    return ZoneRuntime(
        zone_id=zone_id,
        grid_x=grid_x,
        grid_y=grid_y,
        snapshot_id=1,
        tick=0,
        objective_type=objective_type,
        target_x=target_x,
        target_y=target_y,
        aux_x=aux_x,
        aux_y=aux_y,
        entities=entities,
    )


def build_snapshot(zone):
    checksum = compute_zone_checksum(zone.zone_id, zone.tick, zone.entities)
    return ZoneSnapshot(
        zone_id=zone.zone_id,
        snapshot_id=zone.snapshot_id,
        tick=zone.tick,
        grid_x=zone.grid_x,
        grid_y=zone.grid_y,
        objective_type=zone.objective_type,
        target_x=zone.target_x,
        target_y=zone.target_y,
        aux_x=zone.aux_x,
        aux_y=zone.aux_y,
        checksum=checksum,
        entities=list(zone.entities),
    )


def summarize_latencies(latencies_ms):
    if not latencies_ms:
        return {
            "count": 0,
            "p50_ms": 0.0,
            "p95_ms": 0.0,
            "p99_ms": 0.0,
            "max_ms": 0.0,
            "mean_ms": 0.0,
        }

    ordered = sorted(latencies_ms)

    def percentile(p):
        if len(ordered) == 1:
            return ordered[0]
        index = int(round((len(ordered) - 1) * p))
        return ordered[index]

    return {
        "count": len(ordered),
        "p50_ms": percentile(0.50),
        "p95_ms": percentile(0.95),
        "p99_ms": percentile(0.99),
        "max_ms": ordered[-1],
        "mean_ms": statistics.mean(ordered),
    }


def materialize_delta_entities(previous_entities, delta_entities):
    previous_by_id = {entity.entity_id: entity for entity in previous_entities}
    next_entities = []

    for delta_entity in delta_entities:
        previous = previous_by_id.get(delta_entity.entity_id)
        if previous is None:
            raise RuntimeError(f"delta result referenced unknown entity {delta_entity.entity_id}")

        next_entities.append(
            EntityState(
                entity_id=delta_entity.entity_id,
                x=(quantize_milli(previous.x) + delta_entity.dx) / DELTA_SCALE,
                y=(quantize_milli(previous.y) + delta_entity.dy) / DELTA_SCALE,
                vx=(quantize_milli(previous.vx) + delta_entity.dvx) / DELTA_SCALE,
                vy=(quantize_milli(previous.vy) + delta_entity.dvy) / DELTA_SCALE,
                speed=previous.speed,
                state=delta_entity.state,
                flags=delta_entity.flags,
                reserved=previous.reserved,
            )
        )

    return next_entities


def spawn_local_workers(worker_count, base_port, worker_delay_ms, worker_jitter_ms, worker_drop_rate):
    processes = []

    for index in range(worker_count):
        port = base_port + index
        cmd = [
            sys.executable,
            os.path.join(WORKER_DIR, "worker.py"),
            "--host",
            "127.0.0.1",
            "--port",
            str(port),
            "--label",
            f"local-{index}",
            "--delay-ms",
            str(worker_delay_ms),
            "--jitter-ms",
            str(worker_jitter_ms),
            "--drop-rate",
            str(worker_drop_rate),
            "--report-interval",
            "0",
        ]
        processes.append(subprocess.Popen(cmd))

    time.sleep(1.0)
    return processes


def main():
    parser = argparse.ArgumentParser(description="Load test for the binary Unity/Raspberry cluster protocol")
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--base-port", type=int, default=7300)
    parser.add_argument("--grid-columns", type=int, default=4)
    parser.add_argument("--grid-rows", type=int, default=4)
    parser.add_argument("--entities-per-zone", type=int, default=128)
    parser.add_argument("--ticks", type=int, default=120)
    parser.add_argument("--tick-interval", type=float, default=0.05)
    parser.add_argument("--timeout-ms", type=int, default=500)
    parser.add_argument("--result-mode", choices=("full", "delta"), default="delta")
    parser.add_argument("--spawn-local-workers", action="store_true")
    parser.add_argument("--worker-delay-ms", type=float, default=0.0)
    parser.add_argument("--worker-jitter-ms", type=float, default=0.0)
    parser.add_argument("--worker-drop-rate", type=float, default=0.0)
    args = parser.parse_args()

    total_zones = args.grid_columns * args.grid_rows
    endpoints = [
        WorkerEndpoint(host="127.0.0.1", port=args.base_port + index, label=f"worker-{index}")
        for index in range(args.workers)
    ]

    worker_processes = []
    if args.spawn_local_workers:
        worker_processes = spawn_local_workers(
            worker_count=args.workers,
            base_port=args.base_port,
            worker_delay_ms=args.worker_delay_ms,
            worker_jitter_ms=args.worker_jitter_ms,
            worker_drop_rate=args.worker_drop_rate,
        )

    inbox = queue.Queue()
    clients = []
    zones = {}
    request_counter = 1
    pending = {}
    latencies_ms = []
    timeouts = 0
    checksum_errors = 0
    stale_results = 0
    test_started = time.perf_counter()

    try:
        for endpoint in endpoints:
            client = MasterClient(endpoint, inbox, prefer_delta_results=args.result_mode == "delta")
            client.connect(timeout_ms=args.timeout_ms)
            clients.append(client)

        for zone_id in range(total_zones):
            zone = build_zone_runtime(zone_id, args.grid_columns, args.grid_rows, args.entities_per_zone)
            zones[zone.zone_id] = zone
            owner = clients[zone.zone_id % len(clients)]
            owner.send_snapshot(build_snapshot(zone))

        for tick in range(1, args.ticks + 1):
            tick_started = time.perf_counter()

            for zone_id in range(total_zones):
                zone = zones[zone_id]
                owner = clients[zone.zone_id % len(clients)]
                request = SimulateRequest(
                    request_id=request_counter,
                    zone_id=zone.zone_id,
                    snapshot_id=zone.snapshot_id,
                    tick=tick,
                    delta_time=args.tick_interval,
                    player_x=0.0,
                    player_y=0.0,
                )
                pending[request_counter] = {
                    "zone_id": zone.zone_id,
                    "tick": tick,
                    "started_at": time.perf_counter(),
                }
                owner.send_simulate(request)
                request_counter += 1

            deadline = time.perf_counter() + (args.timeout_ms / 1000.0)
            results_remaining = total_zones

            while results_remaining > 0 and time.perf_counter() < deadline:
                timeout = max(0.0, deadline - time.perf_counter())
                try:
                    worker_label, message_type, payload = inbox.get(timeout=timeout)
                except queue.Empty:
                    break

                if message_type == "reader-error":
                    raise RuntimeError(f"reader error from {worker_label}: {payload}")

                if message_type == MessageType.RESULT:
                    result = decode_result(payload)
                    request_meta = pending.pop(result.request_id, None)
                    if request_meta is None:
                        stale_results += 1
                        continue

                    if result.tick != request_meta["tick"]:
                        stale_results += 1
                        continue

                    computed_checksum = compute_zone_checksum(result.zone_id, result.tick, result.entities)
                    if computed_checksum != result.checksum:
                        checksum_errors += 1
                        continue

                    zone = zones[result.zone_id]
                    zone.tick = result.tick
                    zone.entities = list(result.entities)
                    latencies_ms.append((time.perf_counter() - request_meta["started_at"]) * 1000.0)
                    results_remaining -= 1
                    continue

                if message_type == MessageType.RESULT_DELTA:
                    result = decode_result_delta(payload)
                    request_meta = pending.pop(result.request_id, None)
                    if request_meta is None:
                        stale_results += 1
                        continue

                    if result.tick != request_meta["tick"]:
                        stale_results += 1
                        continue

                    zone = zones[result.zone_id]
                    materialized_entities = materialize_delta_entities(zone.entities, result.entities)
                    computed_checksum = compute_zone_checksum(result.zone_id, result.tick, materialized_entities)
                    if computed_checksum != result.checksum:
                        checksum_errors += 1
                        continue

                    zone.tick = result.tick
                    zone.entities = list(materialized_entities)
                    latencies_ms.append((time.perf_counter() - request_meta["started_at"]) * 1000.0)
                    results_remaining -= 1
                    continue

                if message_type == MessageType.ERROR:
                    error = decode_error(payload)
                    raise RuntimeError(f"worker {worker_label} error {error.code}: {error.message}")

                if message_type == MessageType.HEARTBEAT:
                    decode_heartbeat(payload)
                    continue

            if results_remaining > 0:
                timeouts += results_remaining
                timed_out_requests = [
                    request_id
                    for request_id, request_meta in list(pending.items())
                    if request_meta["tick"] == tick
                ]
                for request_id in timed_out_requests:
                    pending.pop(request_id, None)

            elapsed = time.perf_counter() - tick_started
            if elapsed < args.tick_interval:
                time.sleep(args.tick_interval - elapsed)

        for client in clients:
            client.request_stats()

        time.sleep(0.25)
        worker_stats = []
        while True:
            try:
                worker_label, message_type, payload = inbox.get_nowait()
            except queue.Empty:
                break

            if message_type == MessageType.STATS_RESPONSE:
                stats = decode_stats_response(payload)
                worker_stats.append((worker_label, stats))

        total_entities = total_zones * args.entities_per_zone * args.ticks
        elapsed_total = time.perf_counter() - test_started
        latency_summary = summarize_latencies(latencies_ms)
        bytes_sent = sum(client.bytes_sent for client in clients)
        bytes_received = sum(client.bytes_received for client in clients)

        print("Load Test Summary")
        print(f"result_mode={args.result_mode}")
        print(f"workers={len(clients)} zones={total_zones} entities_per_zone={args.entities_per_zone} ticks={args.ticks}")
        print(f"duration_s={elapsed_total:.3f}")
        print(f"successful_results={latency_summary['count']} timeouts={timeouts} checksum_errors={checksum_errors} stale_results={stale_results}")
        print(f"latency_p50_ms={latency_summary['p50_ms']:.3f}")
        print(f"latency_p95_ms={latency_summary['p95_ms']:.3f}")
        print(f"latency_p99_ms={latency_summary['p99_ms']:.3f}")
        print(f"latency_max_ms={latency_summary['max_ms']:.3f}")
        print(f"latency_mean_ms={latency_summary['mean_ms']:.3f}")
        print(f"throughput_entities_per_s={total_entities / max(elapsed_total, 0.001):.2f}")
        print(f"network_bytes_sent={bytes_sent} network_bytes_received={bytes_received}")

        for worker_label, stats in worker_stats:
            print(
                f"worker={worker_label} total_messages={stats.total_messages} "
                f"snapshots={stats.total_snapshots} simulations={stats.total_simulations} "
                f"errors={stats.total_errors} avg_processing_us={stats.average_processing_us} "
                f"max_processing_us={stats.max_processing_us} known_zones={stats.known_zones}"
            )
    finally:
        for client in clients:
            client.close()

        for process in worker_processes:
            process.terminate()
            try:
                process.wait(timeout=2.0)
            except subprocess.TimeoutExpired:
                process.kill()


if __name__ == "__main__":
    main()
