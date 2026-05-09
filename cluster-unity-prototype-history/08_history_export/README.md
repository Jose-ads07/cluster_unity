# Fase 8: History Export

Esta fase añade el siguiente paso natural despues del historial en memoria:

- export persistente del historial por zona a JSONL
- modo `metadata` o `full` para decidir si se guardan entidades
- herramienta offline para inspeccion o verificacion de checksums
- smoke test especifico del export

Que mirar aqui:

- `worker.py`: parametros `--history-export-path` y `--history-export-mode`.
- `zone_history_replay.py`: resumen y verificacion offline del export.
- `zone_history_export_smoke.py`: validacion minima extremo a extremo del export.
- `CLUSTER_README.md`: copia del README global del proyecto en este punto.

Idea clave:

- el historial ya no solo vive dentro del proceso del worker
- ahora tambien puede capturarse y revisarse despues de una prueba real

Limitacion deliberada:

- esto aun no es rollback en caliente ni replay visual dentro de Unity; es export y analisis offline.
