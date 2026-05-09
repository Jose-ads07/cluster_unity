# Historia del Prototipo

Este paquete esta pensado para ayudarte a entender la evolucion del sistema por etapas.

Importante:

- `01_json_unity_proto` es una fase real conservada en disco.
- `04_estado_actual_delta` y `05_balanceo_adaptativo` son snapshots reales de hitos importantes.
- `02_cluster_binario_base` y `03_juego_minimo_distribuido` son cortes didacticos montados a partir de los archivos actuales para que la lectura sea progresiva.
- `06_snapshot_patch` recoge el siguiente salto natural: resincronizacion parcial por zona con patches binarios.
- `07_zone_history` anade historial acotado por zona y consulta binaria para diagnostico.
- `08_history_export` anade export persistente del historial y herramienta offline de inspeccion.
- `09_master_history_and_rotation` anade base de rollback en el maestro y export rotado/comprimido.
- Como no habia repositorio git con commits historicos, esto no es un historial exacto de cada paso intermedio, sino una reconstruccion util para aprender.

Orden recomendado de lectura:

1. `01_json_unity_proto`
2. `02_cluster_binario_base`
3. `03_juego_minimo_distribuido`
4. `04_estado_actual_delta`
5. `05_balanceo_adaptativo`
6. `06_snapshot_patch`
7. `07_zone_history`
8. `08_history_export`
9. `09_master_history_and_rotation`

Que mirar en cada fase:

- Fase 1: idea general y primer reparto de trabajo desde Unity.
- Fase 2: salto a protocolo binario, snapshots, checksums y tolerancia a fallos.
- Fase 3: convertir la simulacion en un juego minimo de supervivencia.
- Fase 4: optimizacion de trafico con resultados delta y herramientas de benchmark.
- Fase 5: balanceo adaptativo de zonas segun carga estimada.
- Fase 6: snapshot patches para resincronizar una zona sin reenviar todo si el owner no cambia.
- Fase 7: historial reciente por zona para inspeccion y futura base de replay.
- Fase 8: export del historial a JSONL y herramienta de replay/analisis offline.
- Fase 9: historial autoritativo en el maestro y export con rotacion/compresion.

Consejo practico:

- compara primero los nombres de clases y responsabilidades
- luego mira como cambian los mensajes de red
- despues mira como crece `ClusterSimulationManager`
- deja la optimizacion delta, el balanceo adaptativo, los snapshot patches, el historial por zona, el export offline y la base de rollback para el final
