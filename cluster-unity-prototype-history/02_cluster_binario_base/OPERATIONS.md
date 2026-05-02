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
- mucho tiempo en `LocalFallback`: workers lentos o inestables
- mucho tiempo en `ApplyResult`: demasiadas entidades por zona o frecuencia demasiado alta
- poco tiempo en red pero mucho en render: el cuello no está en el cluster sino en Unity

## Recomendaciones operativas

- mantener `requestTimeoutSeconds` por encima del `p99` real del cluster
- empezar con pocas zonas y subir gradualmente
- si las Raspberry son lentas, bajar `entities-per-zone` o aumentar workers
- si hay mucha migración entre zonas, considerar zonas más grandes o deltas de estado
- usar red cableada en vez de Wi-Fi para reducir jitter

## Incidencias típicas

`UNKNOWN_ZONE`:

- el worker recibió `Simulate` sin snapshot válido

`SNAPSHOT_MISMATCH`:

- el worker y el maestro no están usando el mismo `snapshotId`
- suele resolverse con reenvío de snapshot

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

Corre en workers:

- simulacion de enemigos por zona
- reaccion de enemigos frente al jugador
- heartbeat y stats de procesamiento
