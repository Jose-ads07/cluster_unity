#!/usr/bin/env python3
import argparse
import math
import random
import socket
import threading
import time
from dataclasses import dataclass, field

from protocol import (
    CAPABILITY_DELTA_RESULTS,
    DELTA_SCALE,
    EntityDeltaState,
    EntityState,
    EntityStateCode,
    ErrorCode,
    MessageType,
    ObjectiveType,
    ResultStatus,
    SimulateDeltaResult,
    ZoneSnapshot,
    SimulateResult,
    compute_zone_checksum,
    decode_hello,
    decode_simulate,
    decode_snapshot,
    decode_snapshot_patch,
    encode_error,
    encode_heartbeat,
    encode_result,
    encode_result_delta,
    encode_stats_response,
    encode_welcome,
    quantize_milli,
    read_frame,
    send_frame,
)


@dataclass
class WorkerStats:
    total_messages: int = 0
    total_snapshots: int = 0
    total_simulations: int = 0
    total_errors: int = 0
    average_processing_us: int = 0
    max_processing_us: int = 0

    def record_processing(self, processing_us):
        self.total_simulations += 1
        self.max_processing_us = max(self.max_processing_us, processing_us)
        if self.total_simulations == 1:
            self.average_processing_us = processing_us
        else:
            self.average_processing_us = int(
                (self.average_processing_us * 0.85) + (processing_us * 0.15)
            )


@dataclass
class ZoneState:
    zone_id: int
    snapshot_id: int
    tick: int
    grid_x: int
    grid_y: int
    objective_type: int
    target_x: float
    target_y: float
    aux_x: float
    aux_y: float
    checksum: int
    entities: list = field(default_factory=list)


class ProtocolError(Exception):
    def __init__(self, code, correlation_id, message):
        super().__init__(message)
        self.code = code
        self.correlation_id = correlation_id
        self.message = message


class ClusterWorkerServer:
    def __init__(self, host, port, label, heartbeat_ms, max_zones, max_entities_per_zone, delay_ms, jitter_ms, drop_rate, report_interval):
        self.host = host
        self.port = port
        self.label = label
        self.heartbeat_ms = heartbeat_ms
        self.max_zones = max_zones
        self.max_entities_per_zone = max_entities_per_zone
        self.delay_ms = delay_ms
        self.jitter_ms = jitter_ms
        self.drop_rate = drop_rate
        self.report_interval = report_interval
        self.started_at = time.perf_counter()
        self.session_counter = 1
        self.lock = threading.Lock()
        self.stats = WorkerStats()
        self.running = True

    def _next_session_id(self):
        with self.lock:
            value = self.session_counter
            self.session_counter += 1
            return value

    def _log(self, text):
        print(f"[worker {self.label}] {text}")

    def _report_stats_loop(self):
        while self.running and self.report_interval > 0:
            time.sleep(self.report_interval)
            with self.lock:
                stats = WorkerStats(
                    total_messages=self.stats.total_messages,
                    total_snapshots=self.stats.total_snapshots,
                    total_simulations=self.stats.total_simulations,
                    total_errors=self.stats.total_errors,
                    average_processing_us=self.stats.average_processing_us,
                    max_processing_us=self.stats.max_processing_us,
                )
            self._log(
                "stats "
                f"messages={stats.total_messages} "
                f"snapshots={stats.total_snapshots} "
                f"simulations={stats.total_simulations} "
                f"errors={stats.total_errors} "
                f"avg_us={stats.average_processing_us} "
                f"max_us={stats.max_processing_us}"
            )

    def _maybe_delay(self):
        total_delay_ms = self.delay_ms
        if self.jitter_ms > 0:
            total_delay_ms += random.uniform(0.0, self.jitter_ms)
        if total_delay_ms > 0:
            time.sleep(total_delay_ms / 1000.0)

    def _desired_velocity(self, entity, zone, request):
        player_x = request.player_x
        player_y = request.player_y
        dx = player_x - entity.x
        dy = player_y - entity.y
        distance = math.hypot(dx, dy)

        if distance > 1e-6:
            nx = dx / distance
            ny = dy / distance
        else:
            nx = 0.0
            ny = 0.0

        behavior_is_avoider = (entity.flags & 0x1) != 0
        direction_scale = -1.0 if behavior_is_avoider else 1.0

        if zone.objective_type == int(ObjectiveType.SEEK):
            return nx * entity.speed * direction_scale, ny * entity.speed * direction_scale, int(EntityStateCode.CHASE)

        if zone.objective_type == int(ObjectiveType.ORBIT):
            desired_radius = max(1.0, zone.aux_x)
            tangent_x = -ny
            tangent_y = nx
            radial_error = distance - desired_radius
            radial_strength = max(-1.0, min(1.0, radial_error * 0.35))
            orbit_bias = max(-1.0, min(1.0, zone.aux_y))
            desired_x = (tangent_x * orbit_bias + (nx * radial_strength * direction_scale)) * entity.speed
            desired_y = (tangent_y * orbit_bias + (ny * radial_strength * direction_scale)) * entity.speed
            return desired_x, desired_y, int(EntityStateCode.ORBIT)

        angle = (request.tick * 0.17) + (entity.entity_id * 0.37) + (zone.aux_y * 0.1)
        wander_x = math.cos(angle)
        wander_y = math.sin(angle)
        desired_x = ((nx * 0.45 * direction_scale) + wander_x * 0.55) * entity.speed
        desired_y = ((ny * 0.45 * direction_scale) + wander_y * 0.55) * entity.speed
        return desired_x, desired_y, int(EntityStateCode.WANDER)

    def _simulate_zone(self, zone, request):
        started_at = time.perf_counter()
        previous_entities = list(zone.entities)

        if request.snapshot_id != zone.snapshot_id:
            raise ProtocolError(
                ErrorCode.SNAPSHOT_MISMATCH,
                request.request_id,
                f"zone {zone.zone_id} expects snapshot {zone.snapshot_id} but received {request.snapshot_id}",
            )

        if request.tick <= zone.tick:
            raise ProtocolError(
                ErrorCode.TICK_OUT_OF_ORDER,
                request.request_id,
                f"zone {zone.zone_id} last tick {zone.tick} received {request.tick}",
            )

        damping = 0.45
        next_entities = []

        for entity in zone.entities:
            desired_vx, desired_vy, state_code = self._desired_velocity(entity, zone, request)
            next_vx = entity.vx + ((desired_vx - entity.vx) * damping)
            next_vy = entity.vy + ((desired_vy - entity.vy) * damping)
            next_x = entity.x + (next_vx * request.delta_time)
            next_y = entity.y + (next_vy * request.delta_time)
            next_entities.append(
                EntityState(
                    entity_id=entity.entity_id,
                    x=next_x,
                    y=next_y,
                    vx=next_vx,
                    vy=next_vy,
                    speed=entity.speed,
                    state=state_code,
                    flags=entity.flags,
                    reserved=entity.reserved,
                )
            )

        zone.entities = next_entities
        zone.tick = request.tick
        zone.checksum = compute_zone_checksum(zone.zone_id, zone.tick, zone.entities)

        processing_us = int((time.perf_counter() - started_at) * 1_000_000.0)
        with self.lock:
            self.stats.record_processing(processing_us)

        return SimulateResult(
            request_id=request.request_id,
            zone_id=zone.zone_id,
            snapshot_id=zone.snapshot_id,
            tick=zone.tick,
            status=int(ResultStatus.OK),
            processing_us=processing_us,
            checksum=zone.checksum,
            entities=list(zone.entities),
        ), previous_entities

    def _try_build_delta_result(self, previous_entities, result):
        previous_by_id = {entity.entity_id: entity for entity in previous_entities}
        delta_entities = []

        for entity in result.entities:
            previous = previous_by_id.get(entity.entity_id)
            if previous is None:
                return None

            dx = quantize_milli(entity.x) - quantize_milli(previous.x)
            dy = quantize_milli(entity.y) - quantize_milli(previous.y)
            dvx = quantize_milli(entity.vx) - quantize_milli(previous.vx)
            dvy = quantize_milli(entity.vy) - quantize_milli(previous.vy)

            if not all(-32768 <= value <= 32767 for value in (dx, dy, dvx, dvy)):
                return None

            delta_entities.append(
                EntityDeltaState(
                    entity_id=entity.entity_id,
                    dx=dx,
                    dy=dy,
                    dvx=dvx,
                    dvy=dvy,
                    state=entity.state,
                    flags=entity.flags,
                )
            )

        return SimulateDeltaResult(
            request_id=result.request_id,
            zone_id=result.zone_id,
            snapshot_id=result.snapshot_id,
            tick=result.tick,
            status=result.status,
            processing_us=result.processing_us,
            checksum=result.checksum,
            entities=delta_entities,
        )

    def _handle_connection(self, conn, addr):
        self._log(f"client connected: {addr}")
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        zones = {}
        handshake_complete = False
        last_tick = 0
        supports_delta_results = False

        try:
            while True:
                message_type, payload, _ = read_frame(conn)
                with self.lock:
                    self.stats.total_messages += 1

                if message_type == MessageType.HELLO:
                    hello = decode_hello(payload)
                    supports_delta_results = (hello.capabilities & CAPABILITY_DELTA_RESULTS) != 0
                    response = encode_welcome(
                        session_id=self._next_session_id(),
                        heartbeat_ms=self.heartbeat_ms,
                        max_zones=self.max_zones,
                        label=self.label,
                    )
                    send_frame(conn, MessageType.WELCOME, response)
                    handshake_complete = True
                    self._log(f"handshake completed with {hello.label} delta_results={'on' if supports_delta_results else 'off'}")
                    continue

                if not handshake_complete:
                    raise ProtocolError(ErrorCode.INVALID_MESSAGE, 0, "handshake required before any other message")

                if message_type == MessageType.SNAPSHOT:
                    snapshot = decode_snapshot(payload)
                    if snapshot.zone_id not in zones and len(zones) >= self.max_zones:
                        raise ProtocolError(ErrorCode.INVALID_MESSAGE, 0, "worker reached max zone count")
                    if len(snapshot.entities) > self.max_entities_per_zone:
                        raise ProtocolError(
                            ErrorCode.TOO_MANY_ENTITIES,
                            0,
                            f"zone {snapshot.zone_id} exceeds entity limit {self.max_entities_per_zone}",
                        )
                    snapshot.checksum = compute_zone_checksum(snapshot.zone_id, snapshot.tick, snapshot.entities)
                    zones[snapshot.zone_id] = ZoneState(
                        zone_id=snapshot.zone_id,
                        snapshot_id=snapshot.snapshot_id,
                        tick=snapshot.tick,
                        grid_x=snapshot.grid_x,
                        grid_y=snapshot.grid_y,
                        objective_type=snapshot.objective_type,
                        target_x=snapshot.target_x,
                        target_y=snapshot.target_y,
                        aux_x=snapshot.aux_x,
                        aux_y=snapshot.aux_y,
                        checksum=snapshot.checksum,
                        entities=list(snapshot.entities),
                    )
                    with self.lock:
                        self.stats.total_snapshots += 1
                    last_tick = max(last_tick, snapshot.tick)
                    continue

                if message_type == MessageType.SNAPSHOT_PATCH:
                    patch = decode_snapshot_patch(payload)
                    zone = zones.get(patch.zone_id)
                    if zone is None:
                        raise ProtocolError(ErrorCode.UNKNOWN_ZONE, 0, f"zone {patch.zone_id} not known for patch")
                    if patch.base_snapshot_id != zone.snapshot_id:
                        raise ProtocolError(
                            ErrorCode.SNAPSHOT_MISMATCH,
                            0,
                            f"zone {patch.zone_id} expects base snapshot {zone.snapshot_id} but received {patch.base_snapshot_id}",
                        )

                    entity_map = {entity.entity_id: entity for entity in zone.entities}
                    for entity_id in patch.removed_entity_ids:
                        entity_map.pop(entity_id, None)
                    for entity in patch.upsert_entities:
                        entity_map[entity.entity_id] = entity

                    if len(entity_map) > self.max_entities_per_zone:
                        raise ProtocolError(
                            ErrorCode.TOO_MANY_ENTITIES,
                            0,
                            f"zone {patch.zone_id} exceeds entity limit {self.max_entities_per_zone} after patch",
                        )

                    zone.snapshot_id = patch.snapshot_id
                    zone.tick = patch.tick
                    zone.grid_x = patch.grid_x
                    zone.grid_y = patch.grid_y
                    zone.objective_type = patch.objective_type
                    zone.target_x = patch.target_x
                    zone.target_y = patch.target_y
                    zone.aux_x = patch.aux_x
                    zone.aux_y = patch.aux_y
                    zone.entities = list(entity_map.values())
                    zone.checksum = compute_zone_checksum(zone.zone_id, zone.tick, zone.entities)
                    with self.lock:
                        self.stats.total_snapshots += 1
                    last_tick = max(last_tick, zone.tick)
                    continue

                if message_type == MessageType.SIMULATE:
                    request = decode_simulate(payload)
                    zone = zones.get(request.zone_id)
                    if zone is None:
                        raise ProtocolError(ErrorCode.UNKNOWN_ZONE, request.request_id, f"zone {request.zone_id} not known")

                    result, previous_entities = self._simulate_zone(zone, request)
                    last_tick = max(last_tick, result.tick)

                    if self.drop_rate > 0.0 and random.random() < self.drop_rate:
                        self._log(f"dropping result for request {request.request_id} due to injected drop rate")
                        continue

                    payload_type = MessageType.RESULT
                    payload = encode_result(result)
                    if supports_delta_results:
                        delta_result = self._try_build_delta_result(previous_entities, result)
                        if delta_result is not None:
                            payload_type = MessageType.RESULT_DELTA
                            payload = encode_result_delta(delta_result)

                    self._maybe_delay()
                    send_frame(conn, payload_type, payload)
                    continue

                if message_type == MessageType.HEARTBEAT:
                    heartbeat = encode_heartbeat(
                        last_tick=last_tick,
                        assigned_zones=len(zones),
                        pending_jobs=0,
                        average_processing_us=self.stats.average_processing_us,
                        uptime_ms=int((time.perf_counter() - self.started_at) * 1000.0),
                    )
                    send_frame(conn, MessageType.HEARTBEAT, heartbeat)
                    continue

                if message_type == MessageType.STATS_REQUEST:
                    with self.lock:
                        payload = encode_stats_response(
                            total_messages=self.stats.total_messages,
                            total_snapshots=self.stats.total_snapshots,
                            total_simulations=self.stats.total_simulations,
                            total_errors=self.stats.total_errors,
                            average_processing_us=self.stats.average_processing_us,
                            max_processing_us=self.stats.max_processing_us,
                            known_zones=len(zones),
                        )
                    send_frame(conn, MessageType.STATS_RESPONSE, payload)
                    continue

                raise ProtocolError(ErrorCode.INVALID_MESSAGE, 0, f"unsupported message type {int(message_type)}")
        except EOFError:
            pass
        except ProtocolError as exc:
            with self.lock:
                self.stats.total_errors += 1
            try:
                send_frame(conn, MessageType.ERROR, encode_error(exc.code, exc.correlation_id, exc.message))
            except Exception:
                pass
            self._log(f"protocol error for {addr}: {exc.message}")
        except Exception as exc:
            with self.lock:
                self.stats.total_errors += 1
            try:
                send_frame(conn, MessageType.ERROR, encode_error(ErrorCode.INTERNAL_ERROR, 0, str(exc)))
            except Exception:
                pass
            self._log(f"internal error for {addr}: {exc}")
        finally:
            try:
                conn.close()
            except Exception:
                pass
            self._log(f"client disconnected: {addr}")

    def serve(self):
        report_thread = None
        if self.report_interval > 0:
            report_thread = threading.Thread(target=self._report_stats_loop, daemon=True)
            report_thread.start()

        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind((self.host, self.port))
        sock.listen()
        self._log(f"listening on {self.host}:{self.port}")

        try:
            while True:
                conn, addr = sock.accept()
                thread = threading.Thread(target=self._handle_connection, args=(conn, addr), daemon=True)
                thread.start()
        finally:
            self.running = False
            sock.close()


def main():
    parser = argparse.ArgumentParser(description="Raspberry Pi distributed simulation worker")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=7000)
    parser.add_argument("--label", default="worker")
    parser.add_argument("--heartbeat-ms", type=int, default=1000)
    parser.add_argument("--max-zones", type=int, default=256)
    parser.add_argument("--max-entities-per-zone", type=int, default=4096)
    parser.add_argument("--delay-ms", type=float, default=0.0)
    parser.add_argument("--jitter-ms", type=float, default=0.0)
    parser.add_argument("--drop-rate", type=float, default=0.0)
    parser.add_argument("--report-interval", type=float, default=10.0)
    args = parser.parse_args()

    server = ClusterWorkerServer(
        host=args.host,
        port=args.port,
        label=args.label,
        heartbeat_ms=args.heartbeat_ms,
        max_zones=args.max_zones,
        max_entities_per_zone=args.max_entities_per_zone,
        delay_ms=args.delay_ms,
        jitter_ms=args.jitter_ms,
        drop_rate=args.drop_rate,
        report_interval=args.report_interval,
    )
    server.serve()


if __name__ == "__main__":
    main()
