# Fase 3: Juego Minimo sobre el Cluster

Esta carpeta representa el momento en que la simulacion deja de ser solo una demo tecnica y pasa a ser un juego minimo.

Objetivos introducidos:

- jugador controlado por teclado
- enemigos distribuidos que reaccionan al jugador
- supervivencia y puntuacion
- derrota y reinicio
- HUD simple

Archivos clave:

- `ClusterSimulationManager.cs`
- `ClusterPlayerController.cs`
- `ClusterGameHUD.cs`
- `EnemyView.cs`

Que mirar aqui:

- como Unity mantiene la autoridad del gameplay
- como la posicion del jugador entra en la simulacion distribuida
- como conviven render local y calculo remoto

Nota:

- esta fase se apoya ya en la base binaria de la fase 2
