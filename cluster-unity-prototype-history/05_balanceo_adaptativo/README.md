# Cluster Unity + Raspberry Pi

Base de juego distribuido hibrido:

- Unity es el nodo maestro y renderiza.
- Las Raspberry Pi ejecutan workers de simulacion.
- La simulacion se reparte por una rejilla 2D de zonas.
- El protocolo principal es binario y stateful.
- La version actual ya es un juego minimo de supervivencia.

## Estado del proyecto

Hay dos niveles de implementacion:

- `unity_master/Distributed*.cs`: prototipo simple anterior, util para entender la idea.
- `unity_master/Cluster*.cs`: base nueva y recomendada, con protocolo binario, grid 2D, snapshots, validacion de tick y tolerancia a fallos.

## Estructura

- `unity_master/`
  - `ClusterProtocol.cs`: serializacion binaria y mensajes.
  - `ClusterWorkerClient.cs`: cliente TCP binario para cada worker.
  - `ClusterSimulationManager.cs`: maestro con grid 2D, snapshots, timeouts, gameplay y fallback local.
  - `ClusterPlayerController.cs`: movimiento del jugador en Unity.
  - `ClusterGameHUD.cs`: HUD y estado de partida.
  - `ClusterProfilerMarkers.cs`: puntos de profiling para Unity Profiler.
  - `EnemyView.cs`: vista minima para enemigos con color por comportamiento.
- `raspberry_worker/`
  - `protocol.py`: protocolo binario compartido por worker y herramientas.
  - `worker.py`: worker stateful con snapshots, simulate, heartbeat y stats.
- `tools/`
  - `load_test.py`: prueba de carga y throughput del protocolo binario.
- `docs/`
  - `ARCHITECTURE.md`
  - `PROTOCOL.md`
  - `OPERATIONS.md`

## Mejoras incluidas

- protocolo binario con frames tipados y longitud prefijada
- serializacion fija por entidad, mucho mas eficiente que JSON
- handshake, heartbeat, errores tipados y stats del worker
- negociacion de capacidades por handshake para activar resultados delta
- reparto por rejilla 2D en lugar de franjas verticales
- snapshots por zona y validacion por `tick`, `snapshotId` y `checksum`
- objetivos distintos por zona: `Seek`, `Orbit`, `Wander`
- IA distribuida orientada al jugador: perseguir, orbitar o escapar segun zona y flags
- reparto adaptativo por carga estimada y tiempos medios del worker
- fallback local si falla un worker o expira un job
- reconexion y reenvio de snapshots al volver un nodo
- soporte correcto para `localhost`, DNS e IPs literales como `127.0.0.1` o `192.168.1.50`
- resultados binarios delta por entidad para reducir el trafico del worker al maestro
- marcadores de profiling para Unity Profiler
- script de carga para medir latencia, throughput y robustez
- bateria automatizada para comparar `100`, `500` y `5000` enemigos con grid `2x2` y `4x4`

## Quick Start

### 1. Worker en Raspberry Pi

```bash
cd cluster-unity-prototype/raspberry_worker
python3 worker.py --host 0.0.0.0 --port 7000 --label pi-a
```

Opciones utiles:

```bash
python3 worker.py --host 0.0.0.0 --port 7001 --label pi-b --delay-ms 5 --jitter-ms 2
python3 worker.py --host 0.0.0.0 --port 7002 --label pi-c --drop-rate 0.02
```

### 2. Escena en Unity

1. Crea un proyecto 3D sencillo.
2. Copia `unity_master/Cluster*.cs` y `EnemyView.cs` a `Assets/Scripts/Cluster/`.
3. Crea un `GameObject` vacio llamado `ClusterSimulationManager`.
4. Añade el componente `ClusterSimulationManager`.
5. Rellena en el inspector:
   - lista de workers con IP y puerto
   - `gridColumns` y `gridRows`
   - `worldMin` y `worldMax`
   - `enemyCount`
   - `enemyPrefab`
6. Pulsa Play.

### 2.1. Loop de juego actual

- controlas un jugador con `WASD` o flechas
- los enemigos se simulan en los workers y reaccionan a tu posicion
- sobrevives mientras evitas el contacto
- la puntuacion es tiempo de supervivencia
- al perder, puedes reiniciar con `R`

### 3. Prueba de carga local

```bash
cd cluster-unity-prototype
python3 tools/load_test.py --spawn-local-workers --workers 4 --grid-columns 4 --grid-rows 4 --entities-per-zone 128 --ticks 120
```

### 4. Bateria simple de benchmarks

Con workers ya levantados en `7002` y `7003`:

```bash
cd cluster-unity-prototype
python3 tools/benchmark_matrix.py --workers 2 --base-port 7002
```

Comparar resultado completo frente a delta:

```bash
python3 tools/benchmark_matrix.py --spawn-local-workers --workers 2 --base-port 7400 --result-mode full
python3 tools/benchmark_matrix.py --spawn-local-workers --workers 2 --base-port 7500 --result-mode delta
```

Con workers temporales lanzados por el propio script:

```bash
python3 tools/benchmark_matrix.py --spawn-local-workers --workers 2 --base-port 7400
```

## Parametros importantes en Unity

- `simulationTickInterval`: frecuencia de simulacion.
- `requestTimeoutSeconds`: cuanto espera el maestro por zona antes de fallback local.
- `heartbeatInterval`: frecuencia de heartbeat a workers.
- `gridColumns` y `gridRows`: granularidad espacial del reparto.
- `worldMin` y `worldMax`: limites del mundo para asignar zonas.
- `renderSmoothing`: suavizado visual entre ticks.
- `defeatDistance`: distancia de derrota respecto al jugador.
- `avoiderRatio`: porcentaje de enemigos que huyen en vez de perseguir.
- `preferDeltaResults`: pide al worker resultados delta en vez de frames completos cuando el protocolo lo soporta.
- `adaptiveZoneAssignment`: activa balanceo adaptativo en vez de round-robin fijo.
- `rebalanceIntervalSeconds`: cada cuanto se permite recalcular owners.
- `ownerStickiness`: cuanto se favorece mantener el owner actual para evitar resnapshots constantes.

## Documentacion tecnica

- [Arquitectura](../../cluster-unity-prototype/docs/ARCHITECTURE.md)
- [Protocolo](../../cluster-unity-prototype/docs/PROTOCOL.md)
- [Operaciones y Carga](../../cluster-unity-prototype/docs/OPERATIONS.md)

## Limitaciones actuales

- El maestro sigue siendo autoritativo y unico punto de render.
- Los snapshots de resincronizacion siguen siendo completos; el ahorro fuerte esta en los resultados delta.
- No hay persistencia ni replay de snapshots en disco.
- La validacion de checksum detecta corrupcion o desincronizacion, pero no resuelve rollback.
- La IA sigue siendo deliberadamente simple para priorizar la prueba del paralelismo distribuido.

## Siguientes pasos naturales

- snapshots historicos y rollback si luego quieres lockstep o multiplayer
- IA mas rica por zona o migracion a ECS/DOTS si el lado Unity crece
- snapshots parciales de resincronizacion para no reenviar toda la zona
- metricas historicas por zona para hacer balanceo adaptativo aun mas fino
