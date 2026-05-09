# Operaciones, Profiling y Carga

## Arranque de workers

Ejemplo simple:

```bash
cd cluster-unity-prototype/raspberry_worker
python3 worker.py --host 0.0.0.0 --port 7000 --label pi-a
```

Ejemplo con fallos inyectados:

```bash
python3 worker.py --host 0.0.0.0 --port 7001 --label pi-b --delay-ms 10 --jitter-ms 5 --drop-rate 0.01
```

Parámetros importantes:

- `--delay-ms`: retardo fijo artificial
- `--jitter-ms`: retardo aleatorio adicional
- `--drop-rate`: porcentaje de respuestas descartadas para probar timeouts
- `--max-entities-per-zone`: guardarrail para evitar snapshots absurdos
- `--report-interval`: cada cuantos segundos imprime stats internas
- `--zone-history-limit`: cuantos hitos recientes guarda por zona para diagnostico
- `--history-export-path`: fichero JSONL al que se exporta el historial por zona
- `--history-export-mode`: `metadata` o `full`, segun quieras solo metadatos o tambien entidades
- `--history-export-max-bytes`: tamano maximo del fichero activo antes de rotar
- `--history-export-max-files`: cuantas rotaciones conservar
- `--history-export-compress-rotated`: comprime las rotaciones antiguas como `.gz`

## Prueba de carga

Prueba local rápida:

```bash
cd cluster-unity-prototype
python3 tools/load_test.py --spawn-local-workers --workers 2 --grid-columns 2 --grid-rows 2 --entities-per-zone 32 --ticks 30
```

Prueba más fuerte:

```bash
python3 tools/load_test.py --spawn-local-workers --workers 4 --grid-columns 4 --grid-rows 4 --entities-per-zone 128 --ticks 240 --tick-interval 0.05
```

Forzar resultado completo:

```bash
python3 tools/load_test.py --spawn-local-workers --workers 2 --grid-columns 4 --grid-rows 4 --entities-per-zone 128 --ticks 60 --result-mode full
```

Forzar resultado delta:

```bash
python3 tools/load_test.py --spawn-local-workers --workers 2 --grid-columns 4 --grid-rows 4 --entities-per-zone 128 --ticks 60 --result-mode delta
```

Prueba con fallos:

```bash
python3 tools/load_test.py --spawn-local-workers --workers 4 --grid-columns 4 --grid-rows 4 --entities-per-zone 128 --ticks 120 --worker-delay-ms 8 --worker-jitter-ms 4 --worker-drop-rate 0.02
```

Smoke test especifico de snapshot patch:

```bash
python3 tools/snapshot_patch_smoke.py --spawn-local-worker
```

Consulta de historial por zona:

```bash
python3 tools/zone_history_query.py --host 127.0.0.1 --port 7000 --zone-id 0 --limit 8
```

Smoke test especifico de historial:

```bash
python3 tools/zone_history_smoke.py --spawn-local-worker
```

Smoke test especifico del export:

```bash
python3 tools/zone_history_export_smoke.py --export-path /tmp/cluster-history-export.jsonl
```

Smoke test de rotacion y compresion:

```bash
python3 tools/zone_history_rotation_smoke.py
```

Inspeccion o verificacion offline del export:

```bash
python3 tools/zone_history_replay.py --input /tmp/cluster-history-export.jsonl --zone-id 0 --tail 8 --verify-checksums
```

## Bateria comparativa recomendada

Con dos workers ya levantados en `7002` y `7003`:

```bash
cd cluster-unity-prototype
python3 tools/benchmark_matrix.py --workers 2 --base-port 7002
```

Con workers temporales creados por el propio benchmark:

```bash
python3 tools/benchmark_matrix.py --spawn-local-workers --workers 2 --base-port 7400
```

La matriz recorre:

- `100` enemigos con grid `2x2`
- `100` enemigos con grid `4x4`
- `500` enemigos con grid `2x2`
- `500` enemigos con grid `4x4`
- `5000` enemigos con grid `2x2`
- `5000` enemigos con grid `4x4`

Para comparar trafico antes y despues del delta:

```bash
python3 tools/benchmark_matrix.py --spawn-local-workers --workers 2 --base-port 7400 --result-mode full
python3 tools/benchmark_matrix.py --spawn-local-workers --workers 2 --base-port 7500 --result-mode delta
```

## Qué medir

El script de carga imprime:

- `successful_results`
- `timeouts`
- `checksum_errors`
- `stale_results`
- latencias `p50`, `p95`, `p99`, media y max
- throughput de entidades por segundo
- bytes enviados y recibidos
- stats por worker
- `result_mode` para saber si el worker esta devolviendo `full` o `delta`

Interpretación rápida:

- si `timeouts` sube, el cluster no está siguiendo el tick actual
- si `checksum_errors` sube, hay problema de protocolo o serialización
- si `stale_results` sube, el maestro está recibiendo respuestas fuera de contexto
- si `p95` crece mucho pero `p50` no, probablemente hay jitter o saturación puntual

## Profiling en Unity

El proyecto nuevo expone `ProfilerMarker` para usar Unity Profiler.

Marcadores principales:

- `Cluster.Update`
- `Cluster.DrainNetwork`
- `Cluster.Tick`
- `Cluster.SnapshotSerialize`
- `Cluster.SimulateSerialize`
- `Cluster.ApplyResult`
- `Cluster.LocalFallback`
- `Cluster.VisualUpdate`

Qué buscar:

- mucho tiempo en `SnapshotSerialize`: demasiadas resincronizaciones
- demasiados `Snapshot Frames: full` frente a `patch`: se esta perdiendo la base o cambiando demasiado el owner
- demasiadas resincronizaciones tras rebalances: `ownerStickiness` demasiado bajo
- mucho tiempo en `LocalFallback`: workers lentos o inestables
- mucho tiempo en `ApplyResult`: demasiadas entidades por zona o frecuencia demasiado alta
- poco tiempo en red pero mucho en render: el cuello no está en el cluster sino en Unity
- demasiadas entradas `SNAPSHOT_FULL` en el historial de una zona: el patch no esta aportando tanto como deberia

## Recomendaciones operativas

- mantener `requestTimeoutSeconds` por encima del `p99` real del cluster
- empezar con pocas zonas y subir gradualmente
- si las Raspberry son lentas, bajar `entities-per-zone` o aumentar workers
- si hay mucha migración entre zonas, considerar zonas más grandes o vigilar la relacion entre `Snapshot Frames: patch` y `full`
- usar red cableada en vez de Wi-Fi para reducir jitter
- si el balanceador cambia owners demasiado a menudo, subir `ownerStickiness` o `rebalanceIntervalSeconds`
- si quieres mas contexto de una zona concreta, consultar su historial antes de tocar timeouts o scheduler
- si activas export `full`, reservarlo para sesiones de diagnostico porque el fichero crece rapido
- si activas rotacion, el replay puede leer la base y sus rotaciones `.N` o `.N.gz`

## Incidencias típicas

`UNKNOWN_ZONE`:

- el worker recibió `Simulate` sin snapshot válido

`SNAPSHOT_MISMATCH`:

- el worker y el maestro no están usando el mismo `snapshotId`
- si el owner es el mismo, primero merece la pena revisar por qué no bastó el patch
- suele resolverse con reenvío de snapshot completo

`ZONE_HISTORY_REQUEST` sobre zona desconocida:

- el worker aun no ha recibido snapshot valido para esa zona
- o la zona estuvo en otro worker distinto

`TICK_OUT_OF_ORDER`:

- llegó un tick viejo o repetido
- conviene revisar timeouts y duplicados

`TOO_MANY_ENTITIES`:

- una zona concentra demasiada carga
- conviene aumentar rejilla o rebalancear

## Reparto de responsabilidades

Corre en Unity:

- render
- input del jugador
- HUD
- autoridad del estado de partida
- deteccion de derrota
- conexion y coordinacion de workers
- historial autoritativo reciente por zona

Corre en workers:

- simulacion de enemigos por zona
- reaccion de enemigos frente al jugador
- heartbeat y stats de procesamiento
- historial acotado por zona para diagnostico
- export opcional del historial a JSONL
- rotacion y compresion opcional del export
