# Fase 7: Zone History

Esta fase añade el siguiente paso natural despues de `SNAPSHOT_PATCH`:

- historial acotado por zona dentro del worker
- nuevo request/respuesta binario para consultar ese historial
- herramientas para inspeccion y smoke test
- base mejor preparada para replay o rollback futuros

Que mirar aqui:

- `ClusterProtocol.cs`: nuevos tipos `ZONE_HISTORY_REQUEST` y `ZONE_HISTORY_RESPONSE`.
- `ClusterWorkerClient.cs`: helper para pedir historial de una zona.
- `ClusterSimulationManager.cs`: acepta respuestas de historial sin romper el loop normal.
- `protocol.py` y `worker.py`: almacenamiento y serializacion del historial reciente.
- `zone_history_query.py`: consulta manual del historial de una zona.
- `zone_history_smoke.py`: validacion minima extremo a extremo.
- `CLUSTER_README.md`: copia del README global del proyecto en este punto.

Idea clave:

- los `RESULT_DELTA` optimizan la vuelta worker -> maestro
- los `SNAPSHOT_PATCH` optimizan resincronizaciones parciales
- el `ZONE_HISTORY` no acelera el frame, pero mejora mucho la observabilidad y prepara replay

Limitacion deliberada:

- el historial actual es en memoria y acotado; todavia no hay persistencia en disco ni rollback automatico.
