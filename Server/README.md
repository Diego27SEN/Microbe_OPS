# Servidor de Microbe_OPS (módulo de Unity Cloud Code, patrón TTTXO)

```
Server/
├── MicrobeOps.Core/      Reglas (enlaza los fuentes de Assets/Scripts/MicrobeOps/Core: mismo código que el cliente)
├── MicrobeOps.Module/    Módulo de Cloud Code: CreateMatch / PlayMove / GetMatchState / SweepAbandonedMatches
├── MicrobeOps.Tests/     77 pruebas (xUnit): reglas, replay, trampas, vistas, servicio, concurrencia
├── Deploy/               Trigger programado del barrido (.tr y .sched)
├── build-module.ps1      Empaqueta MicrobeOpsModule.ccm
├── deploy-ugs.ps1        Pruebas, empaquetado, despliegue y verificación en UGS
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

## Despliegue en Unity Gaming Services

Requisitos: [.NET SDK 10](https://dotnet.microsoft.com/download), Node.js y la CLI de UGS (`npm install -g ugs`).

1. En el [Dashboard de Unity](https://cloud.unity.com) crear/elegir un proyecto y un entorno; vincularlo en Unity (*Edit ▸ Project Settings ▸ Services*).

   > **Entorno de desarrollo actual:** `development` (`48d5390d-be05-4128-a5a7-c4253d8597e0`) en el proyecto **MicrobeOPS** (`76a77e2c-e19d-48a2-bc14-0e012e22cb9b`). Unity debe apuntar a ese mismo proyecto y entorno. No despliegues a `production` hasta validar el flujo completo en `development`.

2. Activar **Authentication** (inicio de sesión anónimo), **Multiplayer Services** (sesiones, solo para emparejar con código), **Cloud Code** y **Cloud Save**.
3. Crear una **Service Account** y darle estos roles del proyecto:
   * Cloud Code (editor/publicador) y **Unity Environments Admin** — necesarios para el módulo.
   * En **LiveOps**: **Triggers Configuration Editor** y **Scheduler Configuration Editor** — solo si vas a desplegar el barrido programado.
4. Autenticar la CLI **sin guardar credenciales en el repo**:

   ```bash
   ugs login                                  # guarda la sesión en tu equipo
   # o, para CI:
   UGS_CLI_SERVICE_KEY_ID=... UGS_CLI_SERVICE_SECRET_KEY=... ugs login --service-key-id "$UGS_CLI_SERVICE_KEY_ID" --secret-key-stdin
   ugs config set project-id <id-del-proyecto>
   ugs config set environment-name <entorno>
   ```

5. Desplegar con el script (pruebas → empaquetado → módulo → trigger/scheduler → verificación):

   ```powershell
   ./Server/deploy-ugs.ps1                 # todo; lee el proyecto/entorno de `ugs config` o de UGS_CLI_*
   ./Server/deploy-ugs.ps1 -DryRun         # simula, no cambia nada
   ./Server/deploy-ugs.ps1 -SkipConfig     # solo el módulo (sin trigger ni scheduler)
   ./Server/deploy-ugs.ps1 -ConfigOnly -SkipBuild -SkipTests
   ```

   También se puede desplegar a mano:

   ```bash
   ./Server/build-module.ps1
   ugs deploy Server/out/MicrobeOpsModule.ccm Server/Deploy
   ```

6. Comprobar el resultado:

   ```bash
   ugs cloud-code modules get MicrobeOpsModule -p <id> -e <entorno>
   ugs triggers list  -p <id> -e <entorno>
   ugs scheduler list -p <id> -e <entorno>
   ```

7. En Unity: menú **Microbe Ops ▸ Configurar modo online en las escenas** (ya aplicado en esta rama) y probar con dos clientes (Editor + build, perfiles distintos en la pantalla de conexión).

### Despliegue automático (GitHub Actions)

`.github/workflows/deploy-ugs.yml` despliega en cada push a `main` que toque `Server/**`, y también a mano (*Actions ▸ Deploy UGS ▸ Run workflow*, con opción de simulación). Configura estos secretos en el repositorio:

| Secreto | Valor |
|---|---|
| `UGS_CLI_SERVICE_KEY_ID` | Key ID de la Service Account |
| `UGS_CLI_SERVICE_SECRET_KEY` | Secret de la Service Account |
| `UGS_CLI_PROJECT_ID` | ID del proyecto de Unity Cloud |
| `UGS_CLI_ENVIRONMENT_NAME` | Entorno (por defecto `development`) |

El trabajo del módulo es obligatorio; el de trigger/scheduler es independiente y, si a la cuenta le faltan esos roles (`403 Not authorized`), el módulo igual queda desplegado y el resumen del workflow lo indica.

> La autenticación por Unity Hub (`ugs login --unity-hub`) sirve para desplegar desde tu equipo, pero el CI necesita una **Service Account con acceso al proyecto MicrobeOPS**. Configura `UGS_CLI_PROJECT_ID` con el ID de ese proyecto y `UGS_CLI_ENVIRONMENT_NAME=development`.


## Decisiones de seguridad

* **Cloud Save PRIVATE.** El registro de la partida contiene la semilla de colocación y las reubicaciones, es decir, el tablero del rival. Se guarda con `SetPrivateCustomItemAsync` (clase de acceso Private, solo accesible con el token del servicio). La clase Default se puede leer por la API REST con el token de cualquier jugador. Una prueba falla si alguien usa las llamadas de clase Default.
* **El dado y la semilla** salen de `RandomNumberGenerator` en el servidor; la cara queda en la jugada y la semilla nunca sale en ninguna respuesta.
* **El cliente no puede** elegir la cara, generar un `timeout`, jugar fuera de turno ni ver el tablero rival.

## Estado y límites conocidos

* **Desplegado en `development`, pendiente de prueba E2E.** El módulo con sus 5 endpoints, el trigger y el scheduler (cada hora; UGS no admite frecuencias menores) están desplegados en el proyecto `MicrobeOPS`. La lógica está probada con almacén y avisos falsos (`IMatchStore`, `IPlayerNotifier`); falta validar contra los servicios reales la forma exacta de las respuestas de Cloud Save (`409` en conflicto de *write lock*) y los push.
* **Creación simultánea**: si los dos jugadores crean a la vez, un registro puede pisar al otro; `CreateMatch` lo detecta (relee y se une) pero no es una operación atómica del almacén.
* **Emparejamiento solo por código de sala** (el id de la sala hace de `handoffId`); con Matchmaker habría que comprobar además el ticket. `GetGameConfig` aún no lee Remote Config.
* La pantalla de partida sigue siendo provisional. El lobby de crear/unirse ya usa UI Toolkit (`Assets/UI/Online`); el dado tiene 9 caras (sin rayos X ni escudo), igual que el modo local.
