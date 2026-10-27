# Microbe_OPS

Juego 2D de estrategia por turnos con temática biológica, en Unity 6 (`6000.3.23f1`): **Glóbulos Blancos** contra **Bacterias** sobre un tablero de 12x6. Cada jugador esconde cinco organismos con distinta forma; en su turno tira un dado de 10 caras que decide la acción.

| Cara | Resultado |
|---|---|
| 1–4 | 1 disparo |
| 5–7 | 2 disparos |
| 8 | Misil: bloque 2x2 |
| 9 | Reubicar un organismo (cuesta el turno; se cura al moverse) |
| 10 | Rayos X (pendiente: cuenta como 1 disparo) |

Gana quien elimina todos los organismos rivales. Cada turno dura 30 s.

## Modos de juego

* **Vs IA** (botón Play) y **Local** (modo `Local` en `Gamemanager`): todo ocurre en el equipo.
* **Online 1 contra 1** (botón Online): partida por turnos con **autoridad en el servidor** (módulo de Unity Cloud Code). El dado, los turnos, el reloj y el tablero rival los controla el servidor, de modo que el cliente no puede hacer trampa. Ver [Server/PROTOCOLO.md](Server/PROTOCOLO.md).

## Cómo ejecutar

1. Abrir la carpeta con **Unity 6000.3.23f1** (la primera vez descarga los paquetes de Unity Services).
2. Abrir la escena `Assets/Scenes/Menu.unity` y pulsar Play.
3. Para el modo Online hace falta desplegar el módulo en un proyecto de Unity Gaming Services propio: [Server/README.md](Server/README.md).

## Estructura

```
Assets/Scripts/MicrobeOps/
├── gameplay/   Modo local y contra IA (Gamemanager, Board, BoardView, EnemyAI, …)
├── Core/       Reglas puras SIN Unity, compartidas con el servidor (MatchEngine, GameBoard, Protocolo)
└── Online/     Cliente online (OnlineSession: red; OnlineGameManager: partida)
Assets/Editor/  OnlineSetup: configura las escenas para el modo online
Server/         Módulo de Cloud Code, pruebas y documentación del protocolo
```

## Pruebas

```bash
dotnet test Server/MicrobeOps.Tests
```

## Pendiente

* Desplegar y probar el módulo contra servicios reales (ver *Estado y límites conocidos* en `Server/README.md`).
* Implementar la cara “Rayos X” y la escena `HowToPlay`.
* Mover también el modo Vs IA al mismo motor de reglas (`Core`) y retirar `Board`/`Organism` duplicados.
