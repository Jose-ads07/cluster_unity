# Fase 6: Snapshot Patch

Esta fase anade el siguiente paso natural despues de `RESULT_DELTA` y del balanceo adaptativo:

- `SNAPSHOT_PATCH` como nuevo mensaje binario
- resincronizacion parcial por zona cuando el owner no cambia
- fallback a `SNAPSHOT` completo si cambia el owner, hay timeout o se pierde la base
- smoke test especifico para comprobar altas y bajas de entidades sin reenviar toda la zona

Que mirar aqui:

- `ClusterProtocol.cs`: nuevo `messageType` y serializacion del patch.
- `ClusterWorkerClient.cs`: envio del patch desde Unity.
- `ClusterSimulationManager.cs`: criterio para decidir `snapshot completo` frente a `patch`.
- `protocol.py` y `worker.py`: aplicacion stateful del patch en el worker.
- `snapshot_patch_smoke.py`: validacion minima extremo a extremo.
- `CLUSTER_README.md`: copia del README global del proyecto en este punto.

Idea clave:

- `RESULT_DELTA` optimiza la vuelta worker -> maestro.
- `SNAPSHOT_PATCH` optimiza parte de la ida maestro -> worker cuando la zona sigue en el mismo nodo.

Limitacion deliberada:

- esto no implementa rollback ni historial persistente; solo reduce el coste de resincronizacion parcial dentro del modelo autoritativo actual.
