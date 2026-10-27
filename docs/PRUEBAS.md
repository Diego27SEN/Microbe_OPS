# Pruebas y tareas del modo online

Este documento es para ustedes (los alumnos). El PR trae el modo online de Microbe_OPS, pero **solo se probó con pruebas automáticas y compilando en Unity: nunca se ejecutó contra Unity Gaming Services (UGS) real.** Su trabajo es probarlo, reportar qué pasó y seguir desarrollando.

Para entender el diseño antes de probar, lean en este orden: `Server/PROTOCOLO.md` → `Assets/Scripts/MicrobeOps/Core/MatchEngine.cs` → `Server/MicrobeOps.Module/MatchService.cs` → `Assets/Scripts/MicrobeOps/Online/OnlineGameManager.cs`.

## Cómo reportar

Por cada prueba, abran un *issue* o comenten en el PR con: **número de prueba, resultado (✔/✘), qué vieron y, si falló, el texto del error de la consola de Unity o de los logs de Cloud Code** (Dashboard ▸ Cloud Code ▸ Logs). Una captura ayuda. Si algo no está claro, esa también es retroalimentación valiosa.

---

## A. Pruebas sin servicios (empiecen por aquí)

| # | Qué probar | Pasos | Resultado esperado | Si falla, reporten |
|---|---|---|---|---|
| A1 | Pruebas automáticas | Instalar .NET SDK 10. `dotnet test Server/MicrobeOps.Tests` | 76 pruebas correctas | Salida completa del comando |
| A2 | Unity compila | Abrir el proyecto con Unity 6000.3.23f1 (la primera vez descarga paquetes) | Sin errores rojos en la consola | Los mensajes de error |
| A3 | El modo contra IA sigue igual | Abrir `Menu`, pulsar **Play**, jugar 3 turnos | Funciona como antes (dado, disparos, IA, reubicar) | Qué cambió respecto a `main` |
| A4 | Botones del menú | Pulsar **Quit** (en el Editor, parar el Play) y **Online** | Quit sale; Online abre la escena de juego con el panel "Partida online" | Captura del menú: ¿el botón Online queda bien ubicado? |
| A5 | Entender el replay | Leer `MatchEngineTests.cs`; ejecutar solo `Replaying_the_same_record_gives_the_same_game` | Pasa. Expliquen con sus palabras por qué el resultado no depende de cuándo se reproduce | Si no entienden algo, díganlo |

## B. Despliegue en UGS

Pasos en `Server/README.md` (sección *Puesta en marcha*). **No guarden credenciales en el repo.**

| # | Qué probar | Resultado esperado | Si falla, reporten |
|---|---|---|---|
| B1 | `./Server/build-module.ps1` | Se crea `Server/out/MicrobeOpsModule.ccm` | Error de compilación o de publicación |
| B2 | Vincular el proyecto a UGS y activar Authentication, Multiplayer Services, Cloud Code y Cloud Save | El Dashboard muestra los 4 servicios activos | Qué paso no pudieron hacer |
| B3 | `ugs deploy Server/out/MicrobeOpsModule.ccm Server/Deploy` | Aparecen en Cloud Code las funciones `CreateMatch`, `PlayMove`, `GetMatchState`, `GetGameConfig`, `SweepAbandonedMatches`, y el trigger | El mensaje de error de `ugs` |

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
| C7 | Dado | Anoten 30 tiradas de cada jugador | Las caras se reparten ~40 % 1 casilla, ~30 % 2, ~10 % misil, ~10 % reubicar, ~10 % "rayos X" (cuenta 1) | La tabla de tiradas |

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

## Tareas para seguir desarrollando

Elijan una por persona y abran su propio PR. Cada una tiene una pista para que lleguen por su cuenta.

| # | Tarea | Pista |
|---|---|---|
| T1 | **Rayos X.** Hoy la cara 10 cuenta como 1 casilla. Impleméntenla en el Core. | `Dice.FromFace`, `MatchSettings.RevealEnabled`. Decidan qué ve el rival y escriban primero la prueba |
| T2 | **Matchmaker.** Reemplazar el código de sala por una cola de Matchmaker. | `handoffId` ya es el punto de unión; el servidor debería comprobar el ticket antes de aceptar a alguien |
| T3 | **Remote Config.** Que `GetGameConfig` lea tiempos y reglas de Remote Config. | Los `MatchSettings` ya viajan dentro del registro: ¿qué pasa con partidas en curso si cambian? |
| T4 | **UI de sala.** Reemplazar el panel IMGUI por uGUI o UI Toolkit. | La lógica ya está separada en `OnlineGameManager`; muevan solo el dibujo |
| T5 | **Unificar el modo contra IA.** `Board`/`Organism` están duplicados con `GameBoard` del Core. | Que `Gamemanager` use `GameBoard` y borren lo duplicado, con pruebas antes y después |
| T6 | **Creación atómica.** Hoy dos creadores simultáneos pueden pisarse y se corrige releyendo. | Busquen una forma de "crear solo si no existe" en Cloud Save y expliquen por qué es mejor |
| T7 | **CI.** Un workflow de GitHub Actions que ejecute `dotnet test`. | Sin secretos en el repo |
| T8 | **Más pruebas.** Cubran reubicar un organismo ya dañado (se cura) y misil en esquinas. | `GameBoardTests` nuevo en `Server/MicrobeOps.Tests` |
| T9 | **Explicación.** Escriban en 10 líneas por qué el rival no puede ver el tablero ni predecir el dado. | Usen los nombres reales de clases y campos |
