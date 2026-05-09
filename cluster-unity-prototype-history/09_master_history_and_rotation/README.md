# Fase 9: Master History And Rotation

Esta fase junta dos pasos naturales a la vez:

- historial autoritativo reciente por zona tambien en el maestro Unity
- export del historial del worker con rotacion y compresion

Que mirar aqui:

- `ClusterSimulationManager.cs`: snapshots/hitos recientes por zona para preparar rollback.
- `worker.py`: opciones de rotacion y compresion del export.
- `zone_history_replay.py`: lectura de fichero base y rotaciones `.N` o `.N.gz`.
- `zone_history_rotation_smoke.py`: validacion de rotacion comprimida.
- `CLUSTER_README.md`: copia del README global del proyecto en este punto.

Idea clave:

- el maestro ya no solo coordina, tambien conserva base reciente para un futuro rewind real
- el worker puede capturar sesiones largas sin que un solo fichero crezca sin control

Limitacion deliberada:

- todavia no existe un rollback jugable expuesto en Unity; solo hemos preparado la base autoritativa y el soporte operativo alrededor.
