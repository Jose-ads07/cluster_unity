# Arquitectura

## Objetivo

Separar render y coordinacion en Unity del calculo de simulacion distribuido en varias Raspberry Pi, de forma que el cluster acelere logica de juego y no el render.

## Componentes

- `ClusterSimulationManager` en Unity:
  - autoritativo sobre el estado global
  - decide la asignacion espacial a zonas
  - gestiona conexiones, timeouts y fallback local
  - aplica reglas de partida, derrota, reinicio y score
- `ClusterPlayerController` en Unity:
  - recoge input local
  - mueve al jugador dentro de los limites del mundo
- `ClusterGameHUD` en Unity:
  - muestra estado de partida, puntuacion y estado del cluster
- `ClusterWorkerClient`:
  - una conexion TCP por worker
  - handshake, envio de snapshots y jobs de simulacion
  - recepcion de resultados completos o delta, heartbeats y errores
- `worker.py`:
  - mantiene el estado por zona que se le asigna
  - aplica snapshots completos cuando el maestro lo indica
  - simula ticks de zonas y devuelve resultados
  - calcula la reaccion de enemigos frente a la posicion del jugador
- `load_test.py`:
  - emula al maestro para medir latencia, throughput y robustez

## Modelo de autoridad

El maestro Unity es autoritativo.

Motivo:

- Unity necesita coherencia local para render, depuracion y gameplay.
- Si un worker cae, el maestro puede continuar simulando sin pedir consenso al cluster.
- Es mas simple de operar que un modelo peer-to-peer entre Raspberry Pi.

En la version actual eso significa:

- Unity decide el estado de la partida y la posicion del jugador.
- Los workers no toman decisiones de victoria, derrota o UI.
- Los workers solo resuelven simulacion de enemigos por zona.

## Modelo de particion

Se usa rejilla 2D fija.

Motivo:

- mejora la localidad espacial frente a repartir solo por indice
- hace mas natural crecer a mapas mas complejos
- permite que cada zona tenga objetivos o IA distinta
- es sencilla de visualizar, depurar y documentar

Evolucion actual:

- el reparto ya puede ser adaptativo
- usa tamaño de zona y `averageProcessingMs` del worker como heuristica
- aplica `stickiness` para no cambiar de owner con demasiada facilidad

Tradeoff:

- sigue siendo heuristico, no un scheduler perfecto
- no usa todavia historico profundo por zona ni prediccion a largo plazo

## Sincronizacion

Cada zona tiene:

- `snapshotId`
- `tick`
- `checksum`
- `ownerWorkerName`

Flujo:

1. Unity calcula que entidades pertenecen a una zona.
2. Si cambia la pertenencia, el owner, el balanceador o el worker se ha reconectado, Unity manda un snapshot.
3. Luego manda `Simulate` con `snapshotId`, `tick` y posicion del jugador.
4. El worker responde con resultado para ese `snapshotId` y ese `tick`.
5. Unity valida `zoneId`, `snapshotId`, `tick` y `checksum`.

Esto evita aplicar resultados viejos o desincronizados.

## Tolerancia a fallos

Si un job de una zona no vuelve antes del timeout:

- el worker se marca como no confiable
- la zona se marca como `snapshotDirty`
- Unity simula esa zona localmente
- al reconectar, el worker recibe snapshot nuevo

Decisiones:

- se prioriza continuidad del juego sobre esperar al cluster
- no se intenta rollback automatico, porque complica mucho la base actual

## Protocolo y serializacion

Se eligio protocolo binario con frames prefijados por longitud.

Motivo:

- JSON era simple pero caro en CPU y red
- fixed-size records por entidad simplifican parseo y reducen garbage
- es facil medir bytes reales enviados y recibidos
- los `RESULT_DELTA` reducen la carga de la ruta worker -> maestro sin cambiar la autoridad

Tradeoff:

- el protocolo es menos amigable para inspeccion manual
- hay que mantener versionado y documentacion mas estrictos
- la cuantizacion delta puede ahorrar trafico a costa de algo mas de CPU en serializacion

## Errores

Los workers pueden devolver errores tipados:

- version no soportada
- mensaje invalido
- zona desconocida
- `snapshotId` incorrecto
- tick fuera de orden
- demasiadas entidades
- error interno

Se eligio error tipado en lugar de texto libre como canal principal porque:

- facilita reaccion programatica
- simplifica profiling y observabilidad
- permite evolucionar la documentacion operativa

## Profiling

Se instrumenta en dos capas:

- Unity:
  - `Cluster.Update`
  - `Cluster.DrainNetwork`
  - `Cluster.Tick`
  - `Cluster.SnapshotSerialize`
  - `Cluster.SimulateSerialize`
  - `Cluster.ApplyResult`
  - `Cluster.LocalFallback`
  - `Cluster.VisualUpdate`
- herramientas Python:
  - latencia p50, p95, p99
  - throughput de entidades por segundo
  - bytes enviados y recibidos
  - stats por worker

## Camino de evolucion recomendado

1. snapshots parciales e historicos por zona
2. rollback o validacion fuerte si se busca multijugador
3. metricas mas finas por zona para mejorar aun mas el balanceo
4. IA mas rica o migracion a ECS/DOTS si el lado Unity crece
