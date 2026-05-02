# Protocolo Binario

## Header

Todos los frames usan este header en little-endian:

- `magic: uint32`
- `version: uint16`
- `messageType: uint16`
- `payloadLength: uint32`

`magic` actual: `0x52434C55`

`version` actual: `1`

## Tipos de mensaje

- `1 HELLO`
- `2 WELCOME`
- `3 SNAPSHOT`
- `4 SIMULATE`
- `5 RESULT`
- `6 HEARTBEAT`
- `7 ERROR`
- `8 STATS_REQUEST`
- `9 STATS_RESPONSE`

## Record por entidad

Cada entidad ocupa 28 bytes:

- `id: int32`
- `x: float32`
- `y: float32`
- `vx: float32`
- `vy: float32`
- `speed: float32`
- `state: uint8`
- `flags: uint8`
- `reserved: uint16`

## HELLO

Campos:

- `role: uint8`
- `capabilities: uint32`
- `maxZones: uint16`
- `label: string16`

Uso:

- primer mensaje del maestro al conectar

Capabilities actuales:

- bit `0x1`: el cliente soporta `RESULT_DELTA`

## WELCOME

Campos:

- `sessionId: uint32`
- `heartbeatMs: uint16`
- `maxZones: uint16`
- `label: string16`

Uso:

- respuesta del worker al handshake

## SNAPSHOT

Campos:

- `zoneId: int32`
- `snapshotId: uint32`
- `tick: int32`
- `gridX: uint16`
- `gridY: uint16`
- `objectiveType: uint8`
- `padding: 3 bytes`
- `targetX: float32`
- `targetY: float32`
- `auxX: float32`
- `auxY: float32`
- `checksum: uint32`
- `entityCount: uint16`
- `entities[]`

Uso:

- resincronizar una zona
- enviar pertenencia nueva de entidades
- enviar objetivo distinto o cambio de owner

## SIMULATE

Campos:

- `requestId: uint32`
- `zoneId: int32`
- `snapshotId: uint32`
- `tick: int32`
- `deltaTime: float32`
- `playerX: float32`
- `playerY: float32`

Uso:

- pedir al worker que avance una zona un tick
- pasar la posicion actual del jugador para que la IA distribuida reaccione al master

## RESULT

Campos:

- `requestId: uint32`
- `zoneId: int32`
- `snapshotId: uint32`
- `tick: int32`
- `status: uint8`
- `padding: 3 bytes`
- `processingUs: uint32`
- `checksum: uint32`
- `entityCount: uint16`
- `entities[]`

Validaciones que hace el maestro:

- `zoneId` coincide con el job pendiente
- `snapshotId` coincide con el ultimo snapshot valido de esa zona
- `tick` coincide con el tick pedido
- `checksum` coincide con el calculado al recibir

## RESULT_DELTA

Campos:

- `requestId: uint32`
- `zoneId: int32`
- `snapshotId: uint32`
- `tick: int32`
- `status: uint8`
- `padding: 3 bytes`
- `processingUs: uint32`
- `checksum: uint32`
- `entityCount: uint16`
- `entities[]`

Cada `entity` delta ocupa 14 bytes:

- `id: int32`
- `dx: int16`
- `dy: int16`
- `dvx: int16`
- `dvy: int16`
- `state: uint8`
- `flags: uint8`

Semantica:

- `dx`, `dy`, `dvx` y `dvy` van cuantizados a milésimas
- se aplican sobre la ultima base conocida por el maestro
- si el worker no puede representar un delta o el cliente no lo soporta, vuelve a `RESULT`

## HEARTBEAT

Campos:

- `lastTick: int32`
- `assignedZones: uint16`
- `pendingJobs: uint16`
- `averageProcessingUs: uint32`
- `uptimeMs: uint64`

Uso:

- liveness
- datos ligeros de diagnostico

## ERROR

Campos:

- `code: uint16`
- `correlationId: uint32`
- `message: string16`

Codigos actuales:

- `1 UNSUPPORTED_VERSION`
- `2 INVALID_MESSAGE`
- `3 UNKNOWN_ZONE`
- `4 SNAPSHOT_MISMATCH`
- `5 TICK_OUT_OF_ORDER`
- `6 TOO_MANY_ENTITIES`
- `100 INTERNAL_ERROR`

## STATS_REQUEST y STATS_RESPONSE

`STATS_REQUEST` no lleva payload.

`STATS_RESPONSE` lleva:

- `totalMessages: uint64`
- `totalSnapshots: uint64`
- `totalSimulations: uint64`
- `totalErrors: uint64`
- `averageProcessingUs: uint32`
- `maxProcessingUs: uint32`
- `knownZones: uint32`

## Objetivos por zona

- `0 SEEK`
- `1 ORBIT`
- `2 WANDER`

## Observaciones de diseño

- TCP se usa por simplicidad operativa y orden garantizado.
- Los snapshots hacen el sistema recuperable tras reconexion.
- El checksum no sustituye a criptografia; aqui solo detecta corrupcion o desync accidental.
- El gameplay vive en Unity, pero la decision de movimiento de enemigos se calcula en workers a partir del `SIMULATE`.
- `RESULT_DELTA` reduce bytes en la ruta mas caliente sin cambiar el modelo autoritativo del maestro.
