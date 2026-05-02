# Fase 1: Prototipo JSON con Unity Maestro

Esta fase representa el primer prototipo sencillo:

- Unity reparte trabajo por workers
- la comunicacion es JSON por TCP
- no hay protocolo binario tipado
- no hay snapshots stateful ni checksums serios
- el objetivo principal es demostrar el reparto de simulacion

Archivos clave:

- `DistributedSimulationManager.cs`
- `DistributedWorkerClient.cs`
- `EnemyView.cs`

Que aprender aqui:

- como Unity construye requests por lote
- como se reparte la simulacion entre workers activos
- como se hace fallback local cuando no hay workers
- por que JSON era suficiente para prototipar, pero no para escalar bien
