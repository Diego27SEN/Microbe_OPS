# Pruebas y tareas del modo online

Este documento es para ustedes (los alumnos). El PR trae el modo online de Microbe_OPS, pero **solo se probó con pruebas automáticas y compilando en Unity: nunca se ejecutó una partida de extremo a extremo contra Unity Gaming Services (UGS) real**. El módulo, el trigger y el scheduler ya están desplegados en el entorno `development` del proyecto **MicrobeOPS**. Su trabajo es probarlo, reportar qué pasó y seguir desarrollando.

Para entender el diseño antes de probar, lean en este orden: `Server/PROTOCOLO.md` → `Assets/Scripts/MicrobeOps/Core/MatchEngine.cs` → `Server/MicrobeOps.Module/MatchService.cs` → `Assets/Scripts/MicrobeOps/Online/OnlineGameManager.cs`.

## Cómo reportar

Por cada prueba, abran un *issue* o comenten en el PR con: **número de prueba, resultado (✔/✘), qué vieron y, si falló, el texto del error de la consola de Unity o de los logs de Cloud Code** (Dashboard ▸ Cloud Code ▸ Logs). Una captura ayuda. Si algo no está claro, esa también es retroalimentación valiosa.

---

## A. Pruebas sin servicios (empiecen por aquí)

| # | Qué probar | Pasos | Resultado esperado | Si falla, reporten |
|---|---|---|---|---|
| A1 | Pruebas automáticas | Instalar .NET SDK 10. `dotnet test Server/MicrobeOps.Tests` | 77 pruebas correctas | Salida completa del comando |
| A2 | Unity compila | Abrir el proyecto con Unity 6000.3.23f1 (la primera vez descarga paquetes) | Sin errores rojos en la consola | Los mensajes de error |
| A3 | El modo contra IA sigue igual | Abrir `Menu`, pulsar **Play**, jugar 3 turnos | Funciona como antes (dado, disparos, IA, reubicar) | Qué cambió respecto a `main` |
| A4 | Botones del menú | Pulsar **Quit** (en el Editor, parar el Play) y **Online** | Quit sale; Online abre la escena de juego con el panel "Partida online" | Captura del menú: ¿el botón Online queda bien ubicado? |
| A5 | Entender el replay | Leer `MatchEngineTests.cs`; ejecutar solo `Replaying_the_same_record_gives_the_same_game` | Pasa. Expliquen con sus palabras por qué el resultado no depende de cuándo se reproduce | Si no entienden algo, díganlo |

## B. Despliegue en UGS

Pasos en `Server/README.md` (sección *Despliegue en Unity Gaming Services*). **No guarden credenciales en el repo.** El despliegue automático vive en `.github/workflows/deploy-ugs.yml` (secretos del repositorio).

| # | Qué probar | Resultado esperado | Si falla, reporten |
|---|---|---|---|
| B1 | `./Server/deploy-ugs.ps1 -DryRun` | Pasan las 77 pruebas, se genera `Server/out/MicrobeOpsModule.ccm` y la simulación no cambia nada en UGS | Error de compilación, de publicación o de credenciales |
| B2 | Vincular el proyecto a UGS y activar Authentication, Multiplayer Services, Cloud Code y Cloud Save | El Dashboard muestra los 4 servicios activos | Qué paso no pudieron hacer |
| B3 | `./Server/deploy-ugs.ps1` | El Dashboard muestra el módulo con `CreateMatch`, `PlayMove`, `GetMatchState`, `GetGameConfig` y `SweepAbandonedMatches` | El mensaje de error de `ugs` |
| B4 | Trigger y scheduler | `ugs triggers list` y `ugs scheduler list` muestran el barrido cada hora (`0 * * * *`; UGS Scheduler no admite frecuencias menores). Si la Service Account no tiene los roles *Triggers Configuration Editor* y *Scheduler Configuration Editor*, `ugs` responde `403 Not authorized` y el script lo explica | El mensaje de error completo |
| B5 | Workflow de CI | En GitHub ▸ Actions ▸ **Deploy UGS** el trabajo del módulo queda en verde | El paso que falló y su log |

## C. Partida entre dos clientes (flujo normal)

Abran dos instancias: Editor + una *build*, o dos Editores. **En la pantalla "Partida online" pongan un perfil distinto en cada una** (por ejemplo `j1` y `j2`) para que sean dos jugadores.

| # | Qué probar | Pasos | Resultado esperado | Si falla, reporten |
|---|---|---|---|---|
| C1 | Conectar | Pulsar **Conectar** en ambos | Pasa a "Crear sala / Unirse" sin errores | Error de consola |
| C2 | Crear y unirse | J1 **Crear sala** (muestra un código); J2 escribe el código y **Unirse** | J1 pasa de "esperando" a la partida sin pulsar nada; empieza el jugador con id menor | ¿Cuánto tardó J1 en enterarse? ¿Fue por push o por el sondeo de 5 s? (logs `[Online]`) |
| C3 | Cada uno ve lo suyo | Mirar el tablero de cada cliente | Cada jugador ve sus 5 organismos y **nada** de los del rival | Captura de ambos |
| C4 | Un turno completo | Tirar el dado, disparar, ver que el turno pasa | Dado y disparos se reflejan en el otro cliente en pocos segundos | Qué se vio tarde o no se vio |
| C5 | Misil y reubicar | Jugar hasta que salga cada cara | Misil: 2x2. Reubicar: el rival solo ve el aviso, no la nueva posición, y cuesta el turno | Qué pasó |
| C6 | Fin de partida | Hundir todos los organismos de uno | Aparece "¡Ganaste!/Perdiste" y se revela el tablero rival | Qué mensaje salió |
| C7 | Dado | Anoten 30 tiradas de cada jugador | Las caras se reparten ~44 % 1 casilla, ~33 % 2, ~11 % misil, ~11 % reubicar (dado de 9 caras, igual que en local) | La tabla de tiradas |

## D. Robustez

| # | Qué probar | Pasos | Resultado esperado | Si falla, reporten |
|---|---|---|---|---|
| D1 | Reconexión | A mitad de partida cerrar un cliente, abrirlo, **Reanudar partida anterior** | Vuelve a la partida con el mismo estado | Qué estado quedó mal |
| D2 | Turno vencido | En su turno, no hacer nada 35 s | El turno pasa al rival. Pasa aunque nadie haya llamado a `PlayMove` (lo resuelve el sondeo) | ¿Quién lo detectó y cuánto tardó? |
| D3 | Abandono | Un jugador no juega 3 turnos seguidos (~90 s) | Gana el otro por "dejó de jugar" | Qué mensaje salió |
| D4 | Los dos abandonan | Dejar la partida sin tocar nada ~2 min | Termina sin ganador (`abandoned_both`) | Estado final |
| D5 | Salir | Pulsar **Abandonar** en turno ajeno | Pierde quien abandona; el otro ve la victoria | Qué pasó |
| D6 | Sin push | Comentar la suscripción en `OnlineSession.SubscribeAsync` y repetir C4 | Todo funciona, solo más lento (hasta 5 s por el sondeo) | Latencia observada |
| D7 | Sala llena | Un tercer cliente intenta unirse al mismo código | Mensaje "partida completa" | Qué salió |

## E. Trampas (el servidor debe rechazarlas)

Escriban un pequeño script de Editor (o usen la consola de pruebas de Cloud Code en el Dashboard) que llame al módulo con `CloudCodeService.Instance.CallModuleEndpointAsync<MatchResponse>("MicrobeOpsModule", "PlayMove", ...)`.

| # | Intento | Resultado esperado (`Code`) |
|---|---|---|
| E1 | `PlayMove` con `kind="shoot"` sin haber tirado el dado | `WrongPhase` |
| E2 | `PlayMove` `roll` cuando no es tu turno | `NotYourTurn` |
| E3 | `PlayMove` con `kind="timeout"` o `"cheat"` | `UnknownMove` |
| E4 | Disparar fuera del tablero (`x=-1`, `x=99`) | `OutOfBounds` |
| E5 | Disparar dos veces a la misma casilla | `AlreadyShot` |
| E6 | Un tercer jugador llama `GetMatchState` de una partida ajena | `NotInMatch` y sin estado |
| E7 | Buscar en la respuesta de `GetMatchState` el id de un organismo rival antes de hundirlo | **No debe aparecer** |

**Cloud Save (importante):** en el Dashboard ▸ Cloud Save ▸ Custom Data, el registro de la partida debe aparecer en la clase **Private**. Intenten leerlo con el token de un jugador por la API REST de Cloud Save (clase Default): no debe existir. Si lo pueden leer, es un fallo grave: reporten.

---

## F. Arreglo del modo online (PR de corrección)

Estas pruebas comprueban el PR que restaura la guarda de `Gamemanager` y alinea el dado a 9 caras.

| # | Qué probar | Pasos | Resultado esperado | Si falla, reporten |
|---|---|---|---|---|
| F1 | El modo local no corre en online | Menú ▸ **Online** ▸ entrar a la escena de juego | **No** se ven los botones RANDOM / READY ni el mensaje de randomizar; solo el panel "Partida online" | Captura de la escena y la consola |
| F2 | Modo Local intacto | Menú ▸ Play ▸ **Local**; colocar con RANDOM, pulsar READY, jugar 3 turnos | Funciona igual que antes del PR | Qué cambió |
| F3 | Modo vs IA intacto | Menú ▸ Play ▸ **IA**; jugar 3 turnos | La IA juega; el humano son los Glóbulos Blancos | Qué cambió |
| F4 | Volver del online al local | Entrar a **Online**, volver al menú, entrar a **Local** | El modo local arranca normal (no arrastra el modo Online) | Qué pasó |
| F5 | Dado de 9 caras | `dotnet test Server/MicrobeOps.Tests` | 77 pruebas correctas, incluidas `The_die_has_nine_faces_like_the_local_game` y `A_forged_face_is_rejected(10)` | Salida del comando |
| F6 | Archivos de Unity fuera de git | Abrir el proyecto en Unity y mirar `git status` | No aparecen `Assets/CloudCode/` ni `*.slnx` como cambios | Qué archivos aparecen |

---

## Tareas para seguir desarrollando

Elijan una por persona y abran su propio PR. Cada una tiene una pista para que lleguen por su cuenta.

| # | Tarea | Pista |
|---|---|---|
| T1 | **Un solo dado.** `DiceController.OutcomeFromFace` y `Dice.FromFace` (Core) repiten la misma regla. | Hagan que el modo local use el del Core y borren la copia; la prueba `Dice_maps_faces_like_the_local_game` debe seguir pasando |
| T2 | **Matchmaker.** Reemplazar el código de sala por una cola de Matchmaker. | `handoffId` ya es el punto de unión; el servidor debería comprobar el ticket antes de aceptar a alguien |
| T3 | **Remote Config.** Que `GetGameConfig` lea tiempos y reglas de Remote Config. | Los `MatchSettings` ya viajan dentro del registro: ¿qué pasa con partidas en curso si cambian? |
| T4 | **Pulir el lobby.** El lobby de crear/unirse ya usa UI Toolkit (`Assets/UI/Online`). | Añadan validación del código, estado de conexión y un botón para copiar el código; mantengan la lógica en `OnlineGameManager` y el dibujo en `OnlineLobbyView` |
| T5 | **Unificar el modo contra IA.** `Board`/`Organism` están duplicados con `GameBoard` del Core. | Que `Gamemanager` use `GameBoard` y borren lo duplicado, con pruebas antes y después |
| T6 | **Creación atómica.** Hoy dos creadores simultáneos pueden pisarse y se corrige releyendo. | Busquen una forma de "crear solo si no existe" en Cloud Save y expliquen por qué es mejor |
| T7 | **CI de pruebas.** Agregar un workflow que ejecute `dotnet test` en cada PR, sin secretos. | El de despliegue ya existe (`.github/workflows/deploy-ugs.yml`), pero necesita credenciales; uno de solo pruebas puede correr en cualquier PR |
| T8 | **Más pruebas.** Cubran reubicar un organismo ya dañado (se cura) y misil en esquinas. | `GameBoardTests` nuevo en `Server/MicrobeOps.Tests` |
| T9 | **Explicación.** Escriban en 10 líneas por qué el rival no puede ver el tablero ni predecir el dado. | Usen los nombres reales de clases y campos |
