# Servidor de Microbe_OPS (módulo de Unity Cloud Code, patrón TTTXO)

```
Server/
├── MicrobeOps.Core/      Reglas (enlaza los fuentes de Assets/Scripts/MicrobeOps/Core: mismo código que el cliente)
├── MicrobeOps.Module/    Módulo de Cloud Code: CreateMatch / PlayMove / GetMatchState / SweepAbandonedMatches
├── MicrobeOps.Tests/     76 pruebas (xUnit): reglas, replay, trampas, vistas, servicio, concurrencia
├── Deploy/               Trigger programado del barrido (.tr y .sched)
├── build-module.ps1      Empaqueta MicrobeOpsModule.ccm
└── PROTOCOLO.md          Endpoints, validaciones, push y secuencia de un turno
```

Requisito: **.NET SDK 10**.

## Pruebas

```bash
dotnet test Server/MicrobeOps.Tests
```

## Empaquetar el módulo

```powershell
./Server/build-module.ps1   # genera Server/out/MicrobeOpsModule.ccm (ZIP de la publicación linux-x64)
```

El nombre del ensamblado (`MicrobeOpsModule`) debe coincidir con el del `.ccm`; es también el nombre de módulo que usa el cliente (`OnlineSession.ModuleName`).

## Puesta en marcha en Unity Gaming Services (pendiente: requiere su proyecto UGS)

1. En el [Dashboard de Unity](https://cloud.unity.com) crear/elegir un proyecto y un entorno; vincularlo en Unity (*Edit ▸ Project Settings ▸ Services*).
2. Activar **Authentication** (inicio de sesión anónimo), **Multiplayer Services** (sesiones, solo para emparejar con código), **Cloud Code** y **Cloud Save**.
3. Crear una **Service Account** con los roles de Cloud Code y de entornos, y configurar la CLI (`ugs`) con sus credenciales **sin guardarlas en el repo** (variables de entorno o secretos del pipeline).
4. Desplegar:

   ```bash
   ugs config set project-id <id-del-proyecto>
   ugs config set environment-name <entorno>
   ugs deploy Server/out/MicrobeOpsModule.ccm Server/Deploy
   ```
5. En Unity: menú **Microbe Ops ▸ Configurar modo online en las escenas** (ya aplicado en esta rama) y probar con dos clientes (Editor + build, perfiles distintos en la pantalla de conexión).

## Decisiones de seguridad

* **Cloud Save PRIVATE.** El registro de la partida contiene la semilla de colocación y las reubicaciones, es decir, el tablero del rival. Se guarda con `SetPrivateCustomItemAsync` (clase de acceso Private, solo accesible con el token del servicio). La clase Default se puede leer por la API REST con el token de cualquier jugador. Una prueba falla si alguien usa las llamadas de clase Default.
* **El dado y la semilla** salen de `RandomNumberGenerator` en el servidor; la cara queda en la jugada y la semilla nunca sale en ninguna respuesta.
* **El cliente no puede** elegir la cara, generar un `timeout`, jugar fuera de turno ni ver el tablero rival.

## Estado y límites conocidos

* **No se ha probado de extremo a extremo contra servicios reales.** La lógica está probada con almacén y avisos falsos (`IMatchStore`, `IPlayerNotifier`); falta desplegarlo para validar la forma exacta de las respuestas de Cloud Save (`409` en conflicto de *write lock*), el trigger programado y los push.
* **Creación simultánea**: si los dos jugadores crean a la vez, un registro puede pisar al otro; `CreateMatch` lo detecta (relee y se une) pero no es una operación atómica del almacén.
* **Emparejamiento solo por código de sala** (el id de la sala hace de `handoffId`); con Matchmaker habría que comprobar además el ticket. `GetGameConfig` aún no lee Remote Config.
* La pantalla de sala usa IMGUI provisional. La cara "Rayos X" sigue sin implementarse (cuenta como 1 casilla).
