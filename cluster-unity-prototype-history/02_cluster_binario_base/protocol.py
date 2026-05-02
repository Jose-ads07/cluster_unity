#!/usr/bin/env python3
import io
import struct
from dataclasses import dataclass, field
from enum import IntEnum
from typing import List, Optional, Tuple


PROTOCOL_MAGIC = 0x52434C55
PROTOCOL_VERSION = 1
CAPABILITY_DELTA_RESULTS = 1 << 0
DELTA_SCALE = 1000.0

FRAME_HEADER = struct.Struct("<IHHI")
ENTITY_RECORD = struct.Struct("<ifffffBBH")
DELTA_ENTITY_RECORD = struct.Struct("<ihhhhBB")


class MessageType(IntEnum):
    HELLO = 1
    WELCOME = 2
    SNAPSHOT = 3
    SIMULATE = 4
    RESULT = 5
    HEARTBEAT = 6
    ERROR = 7
    STATS_REQUEST = 8
    STATS_RESPONSE = 9
    RESULT_DELTA = 10


class ObjectiveType(IntEnum):
    SEEK = 0
    ORBIT = 1
    WANDER = 2


class EntityStateCode(IntEnum):
    IDLE = 0
    CHASE = 1
    ORBIT = 2
    WANDER = 3


class ErrorCode(IntEnum):
    UNSUPPORTED_VERSION = 1
    INVALID_MESSAGE = 2
    UNKNOWN_ZONE = 3
    SNAPSHOT_MISMATCH = 4
    TICK_OUT_OF_ORDER = 5
    TOO_MANY_ENTITIES = 6
    INTERNAL_ERROR = 100


class ResultStatus(IntEnum):
    OK = 0
    RESNAPSHOT_REQUIRED = 1


@dataclass
class EntityState:
    entity_id: int
    x: float
    y: float
    vx: float
    vy: float
    speed: float
    state: int = int(EntityStateCode.CHASE)
    flags: int = 0
    reserved: int = 0


@dataclass
class EntityDeltaState:
    entity_id: int
    dx: int
    dy: int
    dvx: int
    dvy: int
    state: int = int(EntityStateCode.CHASE)
    flags: int = 0


@dataclass
class ZoneSnapshot:
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
    entities: List[EntityState] = field(default_factory=list)


@dataclass
class SimulateRequest:
    request_id: int
    zone_id: int
    snapshot_id: int
    tick: int
    delta_time: float
    player_x: float = 0.0
    player_y: float = 0.0


@dataclass
class SimulateResult:
    request_id: int
    zone_id: int
    snapshot_id: int
    tick: int
    status: int
    processing_us: int
    checksum: int
    entities: List[EntityState] = field(default_factory=list)


@dataclass
class SimulateDeltaResult:
    request_id: int
    zone_id: int
    snapshot_id: int
    tick: int
    status: int
    processing_us: int
    checksum: int
    entities: List[EntityDeltaState] = field(default_factory=list)


@dataclass
class HelloFrame:
    role: int
    capabilities: int
    max_zones: int
    label: str


@dataclass
class WelcomeFrame:
    session_id: int
    heartbeat_ms: int
    max_zones: int
    label: str


@dataclass
class HeartbeatFrame:
    last_tick: int
    assigned_zones: int
    pending_jobs: int
    average_processing_us: int
    uptime_ms: int


@dataclass
class ErrorFrame:
    code: int
    correlation_id: int
    message: str


@dataclass
class StatsResponseFrame:
    total_messages: int
    total_snapshots: int
    total_simulations: int
    total_errors: int
    average_processing_us: int
    max_processing_us: int
    known_zones: int


def clamp(value, minimum, maximum):
    return max(minimum, min(maximum, value))


def _pack_string(buffer, text):
    encoded = text.encode("utf-8")
    if len(encoded) > 65535:
        raise ValueError("string too long for protocol")
    buffer.extend(struct.pack("<H", len(encoded)))
    buffer.extend(encoded)


def _unpack_string(payload, offset):
    if offset + 2 > len(payload):
        raise ValueError("invalid string length")
    (length,) = struct.unpack_from("<H", payload, offset)
    offset += 2
    end = offset + length
    if end > len(payload):
        raise ValueError("invalid string payload")
    return payload[offset:end].decode("utf-8"), end


def _serialize_entities(buffer, entities):
    buffer.extend(struct.pack("<H", len(entities)))
    for entity in entities:
        buffer.extend(
            ENTITY_RECORD.pack(
                int(entity.entity_id),
                float(entity.x),
                float(entity.y),
                float(entity.vx),
                float(entity.vy),
                float(entity.speed),
                int(entity.state) & 0xFF,
                int(entity.flags) & 0xFF,
                int(entity.reserved) & 0xFFFF,
            )
        )


def _deserialize_entities(payload, offset):
    if offset + 2 > len(payload):
        raise ValueError("missing entity count")
    (count,) = struct.unpack_from("<H", payload, offset)
    offset += 2
    entities = []

    for _ in range(count):
        end = offset + ENTITY_RECORD.size
        if end > len(payload):
            raise ValueError("entity record truncated")

        record = ENTITY_RECORD.unpack_from(payload, offset)
        entities.append(
            EntityState(
                entity_id=record[0],
                x=record[1],
                y=record[2],
                vx=record[3],
                vy=record[4],
                speed=record[5],
                state=record[6],
                flags=record[7],
                reserved=record[8],
            )
        )
        offset = end

    return entities, offset


def _fnv1a_u32(current, value):
    current &= 0xFFFFFFFF
    for shift in range(0, 32, 8):
        current ^= (value >> shift) & 0xFF
        current = (current * 16777619) & 0xFFFFFFFF
    return current


def _to_float32(value):
    return struct.unpack("<f", struct.pack("<f", float(value)))[0]


def quantize_milli(value):
    return int(round(_to_float32(value) * DELTA_SCALE))


def compute_zone_checksum(zone_id, tick, entities):
    checksum = 2166136261
    checksum = _fnv1a_u32(checksum, int(zone_id) & 0xFFFFFFFF)
    checksum = _fnv1a_u32(checksum, int(tick) & 0xFFFFFFFF)
    checksum = _fnv1a_u32(checksum, len(entities) & 0xFFFFFFFF)

    for entity in sorted(entities, key=lambda item: item.entity_id):
        checksum = _fnv1a_u32(checksum, int(entity.entity_id) & 0xFFFFFFFF)
        checksum = _fnv1a_u32(checksum, quantize_milli(entity.x) & 0xFFFFFFFF)
        checksum = _fnv1a_u32(checksum, quantize_milli(entity.y) & 0xFFFFFFFF)
        checksum = _fnv1a_u32(checksum, quantize_milli(entity.vx) & 0xFFFFFFFF)
        checksum = _fnv1a_u32(checksum, quantize_milli(entity.vy) & 0xFFFFFFFF)
        checksum = _fnv1a_u32(checksum, quantize_milli(entity.speed) & 0xFFFFFFFF)
        checksum = _fnv1a_u32(checksum, int(entity.state) & 0xFFFFFFFF)
        checksum = _fnv1a_u32(checksum, int(entity.flags) & 0xFFFFFFFF)

    return checksum


def encode_frame(message_type, payload):
    return FRAME_HEADER.pack(
        PROTOCOL_MAGIC,
        PROTOCOL_VERSION,
        int(message_type),
        len(payload),
    ) + payload


def recv_exact(sock, size):
    chunks = []
    total = 0

    while total < size:
        data = sock.recv(size - total)
        if not data:
            raise EOFError("connection closed")
        chunks.append(data)
        total += len(data)

    return b"".join(chunks)


def read_frame(sock):
    header = recv_exact(sock, FRAME_HEADER.size)
    magic, version, message_type, payload_len = FRAME_HEADER.unpack(header)

    if magic != PROTOCOL_MAGIC:
        raise ValueError("invalid protocol magic")
    if version != PROTOCOL_VERSION:
        raise ValueError("unsupported protocol version")

    payload = recv_exact(sock, payload_len) if payload_len else b""
    return MessageType(message_type), payload, FRAME_HEADER.size + payload_len


def send_frame(sock, message_type, payload):
    frame = encode_frame(message_type, payload)
    sock.sendall(frame)
    return len(frame)


def encode_hello(label, capabilities=0, max_zones=0, role=1):
    buffer = bytearray()
    buffer.extend(struct.pack("<BIH", int(role), int(capabilities), int(max_zones)))
    _pack_string(buffer, label)
    return bytes(buffer)


def decode_hello(payload):
    if len(payload) < 7:
        raise ValueError("hello payload too short")
    role, capabilities, max_zones = struct.unpack_from("<BIH", payload, 0)
    label, _ = _unpack_string(payload, 7)
    return HelloFrame(role=role, capabilities=capabilities, max_zones=max_zones, label=label)


def encode_welcome(session_id, heartbeat_ms, max_zones, label):
    buffer = bytearray()
    buffer.extend(struct.pack("<IHH", int(session_id), int(heartbeat_ms), int(max_zones)))
    _pack_string(buffer, label)
    return bytes(buffer)


def decode_welcome(payload):
    if len(payload) < 8:
        raise ValueError("welcome payload too short")
    session_id, heartbeat_ms, max_zones = struct.unpack_from("<IHH", payload, 0)
    label, _ = _unpack_string(payload, 8)
    return WelcomeFrame(
        session_id=session_id,
        heartbeat_ms=heartbeat_ms,
        max_zones=max_zones,
        label=label,
    )


def encode_snapshot(snapshot):
    buffer = bytearray()
    buffer.extend(struct.pack("<iIiHHB3x", int(snapshot.zone_id), int(snapshot.snapshot_id), int(snapshot.tick), int(snapshot.grid_x), int(snapshot.grid_y), int(snapshot.objective_type)))
    buffer.extend(struct.pack("<ffffI", float(snapshot.target_x), float(snapshot.target_y), float(snapshot.aux_x), float(snapshot.aux_y), int(snapshot.checksum)))
    _serialize_entities(buffer, snapshot.entities)
    return bytes(buffer)


def decode_snapshot(payload):
    minimum = struct.calcsize("<iIiHHB3x") + struct.calcsize("<ffffI") + 2
    if len(payload) < minimum:
        raise ValueError("snapshot payload too short")

    zone_id, snapshot_id, tick, grid_x, grid_y, objective_type = struct.unpack_from("<iIiHHB3x", payload, 0)
    offset = struct.calcsize("<iIiHHB3x")
    target_x, target_y, aux_x, aux_y, checksum = struct.unpack_from("<ffffI", payload, offset)
    offset += struct.calcsize("<ffffI")
    entities, offset = _deserialize_entities(payload, offset)

    return ZoneSnapshot(
        zone_id=zone_id,
        snapshot_id=snapshot_id,
        tick=tick,
        grid_x=grid_x,
        grid_y=grid_y,
        objective_type=objective_type,
        target_x=target_x,
        target_y=target_y,
        aux_x=aux_x,
        aux_y=aux_y,
        checksum=checksum,
        entities=entities,
    )


def encode_simulate(request):
    return struct.pack(
        "<IiIifff",
        int(request.request_id),
        int(request.zone_id),
        int(request.snapshot_id),
        int(request.tick),
        float(request.delta_time),
        float(request.player_x),
        float(request.player_y),
    )


def decode_simulate(payload):
    if len(payload) != struct.calcsize("<IiIifff"):
        raise ValueError("invalid simulate payload")

    values = struct.unpack("<IiIifff", payload)
    return SimulateRequest(
        request_id=values[0],
        zone_id=values[1],
        snapshot_id=values[2],
        tick=values[3],
        delta_time=values[4],
        player_x=values[5],
        player_y=values[6],
    )


def encode_result(result):
    buffer = bytearray()
    buffer.extend(
        struct.pack(
            "<IiIiB3xII",
            int(result.request_id),
            int(result.zone_id),
            int(result.snapshot_id),
            int(result.tick),
            int(result.status),
            int(result.processing_us),
            int(result.checksum),
        )
    )
    _serialize_entities(buffer, result.entities)
    return bytes(buffer)


def decode_result(payload):
    minimum = struct.calcsize("<IiIiB3xII") + 2
    if len(payload) < minimum:
        raise ValueError("result payload too short")

    request_id, zone_id, snapshot_id, tick, status, processing_us, checksum = struct.unpack_from("<IiIiB3xII", payload, 0)
    offset = struct.calcsize("<IiIiB3xII")
    entities, offset = _deserialize_entities(payload, offset)

    return SimulateResult(
        request_id=request_id,
        zone_id=zone_id,
        snapshot_id=snapshot_id,
        tick=tick,
        status=status,
        processing_us=processing_us,
        checksum=checksum,
        entities=entities,
    )


def encode_result_delta(result):
    buffer = bytearray()
    buffer.extend(
        struct.pack(
            "<IiIiB3xII",
            int(result.request_id),
            int(result.zone_id),
            int(result.snapshot_id),
            int(result.tick),
            int(result.status),
            int(result.processing_us),
            int(result.checksum),
        )
    )
    buffer.extend(struct.pack("<H", len(result.entities)))
    for entity in result.entities:
        buffer.extend(
            DELTA_ENTITY_RECORD.pack(
                int(entity.entity_id),
                int(entity.dx),
                int(entity.dy),
                int(entity.dvx),
                int(entity.dvy),
                int(entity.state) & 0xFF,
                int(entity.flags) & 0xFF,
            )
        )
    return bytes(buffer)


def decode_result_delta(payload):
    minimum = struct.calcsize("<IiIiB3xII") + 2
    if len(payload) < minimum:
        raise ValueError("delta result payload too short")

    request_id, zone_id, snapshot_id, tick, status, processing_us, checksum = struct.unpack_from("<IiIiB3xII", payload, 0)
    offset = struct.calcsize("<IiIiB3xII")
    (entity_count,) = struct.unpack_from("<H", payload, offset)
    offset += 2
    entities = []

    for _ in range(entity_count):
        end = offset + DELTA_ENTITY_RECORD.size
        if end > len(payload):
            raise ValueError("delta entity record truncated")
        record = DELTA_ENTITY_RECORD.unpack_from(payload, offset)
        entities.append(
            EntityDeltaState(
                entity_id=record[0],
                dx=record[1],
                dy=record[2],
                dvx=record[3],
                dvy=record[4],
                state=record[5],
                flags=record[6],
            )
        )
        offset = end

    return SimulateDeltaResult(
        request_id=request_id,
        zone_id=zone_id,
        snapshot_id=snapshot_id,
        tick=tick,
        status=status,
        processing_us=processing_us,
        checksum=checksum,
        entities=entities,
    )


def encode_heartbeat(last_tick, assigned_zones, pending_jobs, average_processing_us, uptime_ms):
    return struct.pack(
        "<iHHIQ",
        int(last_tick),
        int(assigned_zones),
        int(pending_jobs),
        int(average_processing_us),
        int(uptime_ms),
    )


def decode_heartbeat(payload):
    if len(payload) != struct.calcsize("<iHHIQ"):
        raise ValueError("invalid heartbeat payload")

    values = struct.unpack("<iHHIQ", payload)
    return HeartbeatFrame(
        last_tick=values[0],
        assigned_zones=values[1],
        pending_jobs=values[2],
        average_processing_us=values[3],
        uptime_ms=values[4],
    )


def encode_error(code, correlation_id, message):
    buffer = bytearray()
    buffer.extend(struct.pack("<HI", int(code), int(correlation_id)))
    _pack_string(buffer, message)
    return bytes(buffer)


def decode_error(payload):
    if len(payload) < 6:
        raise ValueError("invalid error payload")

    code, correlation_id = struct.unpack_from("<HI", payload, 0)
    message, _ = _unpack_string(payload, 6)
    return ErrorFrame(code=code, correlation_id=correlation_id, message=message)


def encode_stats_request():
    return b""


def encode_stats_response(total_messages, total_snapshots, total_simulations, total_errors, average_processing_us, max_processing_us, known_zones):
    return struct.pack(
        "<QQQQIII",
        int(total_messages),
        int(total_snapshots),
        int(total_simulations),
        int(total_errors),
        int(average_processing_us),
        int(max_processing_us),
        int(known_zones),
    )


def decode_stats_response(payload):
    if len(payload) != struct.calcsize("<QQQQIII"):
        raise ValueError("invalid stats response payload")

    values = struct.unpack("<QQQQIII", payload)
    return StatsResponseFrame(
        total_messages=values[0],
        total_snapshots=values[1],
        total_simulations=values[2],
        total_errors=values[3],
        average_processing_us=values[4],
        max_processing_us=values[5],
        known_zones=values[6],
    )
