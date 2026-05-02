# Fase 2: Base de Cluster Binario

Esta carpeta es un corte didactico de la arquitectura binaria.

No es un snapshot historico exacto, pero si refleja el salto conceptual importante:

- protocolo binario en vez de JSON
- handshake
- snapshots por zona
- `tick`, `snapshotId` y `checksum`
- worker stateful
- profiling y pruebas de carga

Archivos clave:

- `ClusterProtocol.cs`
- `ClusterWorkerClient.cs`
- `ClusterProfilerMarkers.cs`
- `protocol.py`
- `worker.py`
- `load_test.py`
- `ARCHITECTURE.md`
- `PROTOCOL.md`
- `OPERATIONS.md`

Que mirar aqui:

- el formato de frames
- como se valida un resultado
- como el worker mantiene estado de zona
- por que esta base ya es mucho mas robusta que la fase JSON

Nota:

- en los archivos actuales ya veras rastro de mejoras posteriores, pero esta carpeta esta pensada para que te centres en la parte de infraestructura distribuida
