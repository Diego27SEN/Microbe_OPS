<#
.SYNOPSIS
    Empaqueta y despliega MicrobeOpsModule (Cloud Code) y su configuración (trigger + scheduler) en UGS.

.DESCRIPTION
    Orden de operaciones:
      1. (Opcional) pruebas del Core.
      2. build-module.ps1  ->  Server/out/MicrobeOpsModule.ccm
      3. ugs deploy del .ccm (módulo Cloud Code).
      4. ugs deploy de Server/Deploy (trigger y schedule).
      5. Verificación del módulo desplegado y de sus endpoints.

    El trigger y el scheduler son opcionales para jugar: si la Service Account
    todavía no tiene esos roles, el módulo queda desplegado y el script explica
    cómo terminar la configuración.

    Credenciales: NO se guardan en el repo. La CLI las toma de:
      - la configuración local de `ugs login`, o
      - las variables de entorno UGS_CLI_SERVICE_KEY_ID / UGS_CLI_SERVICE_SECRET_KEY.

.PARAMETER ProjectId
    ID del proyecto de Unity Cloud. Por defecto: $env:UGS_CLI_PROJECT_ID o `ugs config get project-id`.

.PARAMETER EnvironmentName
    Nombre del entorno. Por defecto: $env:UGS_CLI_ENVIRONMENT_NAME o `ugs config get environment-name`.

.PARAMETER ServiceKeyId
    Key ID de la Service Account. Por defecto: $env:UGS_CLI_SERVICE_KEY_ID.

.PARAMETER SecretKey
    Secret de la Service Account. Por defecto: $env:UGS_CLI_SERVICE_SECRET_KEY.
    Si se pasan ambas claves se hace `ugs login` (solo para esta máquina).

.EXAMPLE
    ./Server/deploy-ugs.ps1

.EXAMPLE
    ./Server/deploy-ugs.ps1 -DryRun -SkipTests

.EXAMPLE
    ./Server/deploy-ugs.ps1 -SkipConfig

.EXAMPLE
    ./Server/deploy-ugs.ps1 -ConfigOnly -SkipBuild -SkipTests

.EXAMPLE
    $env:UGS_CLI_SERVICE_KEY_ID = "..."
    $env:UGS_CLI_SERVICE_SECRET_KEY = "..."
    ./Server/deploy-ugs.ps1 -ProjectId 78b3... -EnvironmentName production
#>
[CmdletBinding()]
param(
    [string]$ProjectId = $env:UGS_CLI_PROJECT_ID,
    [string]$EnvironmentName = $env:UGS_CLI_ENVIRONMENT_NAME,
    [string]$ServiceKeyId = $env:UGS_CLI_SERVICE_KEY_ID,
    [string]$SecretKey = $env:UGS_CLI_SERVICE_SECRET_KEY,
    [string]$CcmPath,
    [string]$ConfigPath,
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$SkipConfig,
    [switch]$SkipModule,
    [switch]$ConfigOnly,
    [switch]$DryRun,
    [switch]$Reconcile
)

$ErrorActionPreference = "Continue"

if ($ConfigOnly) { $SkipModule = $true }

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$moduleName = "MicrobeOpsModule"
$defaultCcm = Join-Path $root "out/$moduleName.ccm"
$defaultConfig = Join-Path $root "Deploy"

if ([string]::IsNullOrWhiteSpace($CcmPath)) { $CcmPath = $defaultCcm }
if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $ConfigPath = $defaultConfig }

function Fail([string]$message) {
    Write-Host ""
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

function Get-UgsConfigValue([string]$key) {
    $value = & ugs config get $key 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return (($value | Select-Object -Last 1) -as [string]).Trim()
}

if ($SkipModule -and $SkipConfig) {
    Fail "No hay nada que desplegar: -SkipModule y -SkipConfig están activos."
}

# ---------- Prerrequisitos ----------

if (-not (Get-Command ugs -ErrorAction SilentlyContinue)) {
    Fail "No se encontró la CLI 'ugs'. Instálala con: npm install -g ugs (https://docs.unity.com/en-us/services/ugs-cli)."
}

if ([string]::IsNullOrWhiteSpace($ProjectId)) {
    $ProjectId = Get-UgsConfigValue "project-id"
}
if ([string]::IsNullOrWhiteSpace($EnvironmentName)) {
    $EnvironmentName = Get-UgsConfigValue "environment-name"
}

if ([string]::IsNullOrWhiteSpace($ProjectId)) {
    Fail "Falta el Project ID. Usa -ProjectId, la variable UGS_CLI_PROJECT_ID o 'ugs config set project-id <id>'."
}
if ([string]::IsNullOrWhiteSpace($EnvironmentName)) {
    Fail "Falta el entorno. Usa -EnvironmentName, la variable UGS_CLI_ENVIRONMENT_NAME o 'ugs config set environment-name <nombre>'."
}

# ---------- Autenticación ----------

if (-not [string]::IsNullOrWhiteSpace($ServiceKeyId) -and -not [string]::IsNullOrWhiteSpace($SecretKey)) {
    Write-Host "Autenticando con Service Account $ServiceKeyId..." -ForegroundColor Cyan
    $SecretKey | & ugs login --service-key-id $ServiceKeyId --secret-key-stdin 2>&1
    if ($LASTEXITCODE -ne 0) { Fail "Falló 'ugs login'. Revisa el key-id y el secret." }
}

$status = (& ugs status 2>&1) -join "`n"
Write-Host $status
if ($status -match "No .*credential|not logged|unauthenticated|no credentials") {
    Fail "La CLI no tiene credenciales. Ejecuta 'ugs login' o define UGS_CLI_SERVICE_KEY_ID y UGS_CLI_SERVICE_SECRET_KEY."
}

Write-Host "Proyecto: $ProjectId | Entorno: $EnvironmentName" -ForegroundColor Cyan

# ---------- Pruebas ----------

if (-not $SkipTests -and -not $SkipBuild) {
    Write-Host "Ejecutando pruebas del servidor..." -ForegroundColor Cyan
    & dotnet test (Join-Path $root "MicrobeOps.Tests") --nologo
    if ($LASTEXITCODE -ne 0) { Fail "Las pruebas fallaron: no se despliega." }
}

# ---------- Empaquetado ----------

if (-not $SkipBuild) {
    Write-Host "Empaquetando el módulo..." -ForegroundColor Cyan
    & (Join-Path $root "build-module.ps1")
    if ($LASTEXITCODE -ne 0) { Fail "Falló el empaquetado (build-module.ps1)." }
}

if (-not $SkipModule -and -not (Test-Path $CcmPath)) {
    Fail "No existe el paquete '$CcmPath'. Quita -SkipBuild o genera el .ccm primero."
}
if (-not $SkipConfig -and -not (Test-Path $ConfigPath)) {
    Fail "No existe la carpeta de configuración '$ConfigPath'."
}

# ---------- Despliegue ----------

$commonArgs = @("-p", $ProjectId, "-e", $EnvironmentName)
if ($DryRun) { $commonArgs += "--dry-run" }
if ($Reconcile) { $commonArgs += "--reconcile" }

$total = 0
if (-not $SkipModule) { $total++ }
if (-not $SkipConfig) { $total++ }
$step = 0

if (-not $SkipModule) {
    $step++
    Write-Host ""
    Write-Host "$step/$total  Desplegando módulo Cloud Code ($moduleName)..." -ForegroundColor Cyan
    & ugs deploy $CcmPath @commonArgs 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { Fail "Falló el despliegue del módulo." }
}

$configFailed = $false
if (-not $SkipConfig) {
    $step++
    Write-Host ""
    Write-Host "$step/$total  Desplegando trigger y scheduler (Server/Deploy)..." -ForegroundColor Cyan
    $configOutput = & ugs deploy $ConfigPath @commonArgs 2>&1
    $configOutput | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        $configFailed = $true
        if (($configOutput -join "`n") -match "Not authorized|403|Forbidden") {
            Write-Host ""
            Write-Warning "La Service Account no puede administrar Triggers/Scheduler (403 Not authorized)."
            Write-Host "El módulo de Cloud Code SÍ quedó desplegado y el juego funciona sin el barrido." -ForegroundColor Yellow
            Write-Host "Para completar el trigger y el scheduler tienes dos opciones:" -ForegroundColor Yellow
            Write-Host "  A) En el Dashboard (https://cloud.unity.com) ▸ Project Settings ▸ Service Accounts,"
            Write-Host "     agrega a esta cuenta, en LiveOps: Triggers Configuration Editor y"
            Write-Host "     Scheduler Configuration Editor (en Admin: Unity Environments Admin),"
            Write-Host "     y vuelve a ejecutar: ./Server/deploy-ugs.ps1 -SkipBuild -SkipTests"
            Write-Host "  B) Crea a mano en el Dashboard:"
            Write-Host "     - Scheduler: evento 'microbeops-sweep-tick', cron '*/5 * * * *'."
            Write-Host "     - Trigger: evento 'com.unity.services.scheduler.microbeops-sweep-tick.v1' ->"
            Write-Host "       Cloud Code MicrobeOpsModule/SweepAbandonedMatches."
        } else {
            Write-Warning "No se pudo desplegar Server/Deploy (trigger y scheduler)."
        }
    }
}

if ($DryRun) {
    Write-Host ""
    if ($configFailed) {
        Write-Host "Dry run del módulo completado; la configuración reportó un problema (ver arriba)." -ForegroundColor Yellow
        exit 2
    }
    Write-Host "Dry run completado: no se cambió nada." -ForegroundColor Yellow
    exit 0
}

if ($SkipModule) {
    if ($configFailed) { exit 2 }
    Write-Host ""
    Write-Host "OK: configuración desplegada." -ForegroundColor Green
    exit 0
}

# ---------- Verificación ----------

Write-Host ""
Write-Host "Verificando el módulo desplegado..." -ForegroundColor Cyan
$moduleJson = & ugs cloud-code modules get $moduleName --json -p $ProjectId -e $EnvironmentName 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Warning "El despliegue terminó, pero no se pudo verificar el módulo:`n$moduleJson"
    exit 0
}

Write-Host $moduleJson

$expected = @("CreateMatch", "PlayMove", "GetMatchState", "GetGameConfig", "SweepAbandonedMatches")
$missing = @()
foreach ($endpoint in $expected) {
    if (($moduleJson -join "`n") -notmatch [regex]::Escape($endpoint)) {
        $missing += $endpoint
    }
}

if ($missing.Count -gt 0) {
    Write-Warning "El módulo está desplegado, pero no se encontraron estos endpoints: $($missing -join ', '). Revisa el Dashboard ▸ Cloud Code."
    exit 0
}

Write-Host ""
Write-Host "OK: $moduleName desplegado con $($expected.Count) endpoints." -ForegroundColor Green
if ($configFailed) {
    Write-Host "Pendiente: trigger y scheduler (ver el aviso de permisos arriba)." -ForegroundColor Yellow
    exit 2
}
Write-Host "Comprueba trigger y scheduler:" -ForegroundColor Green
Write-Host "  ugs triggers list -p $ProjectId -e $EnvironmentName"
Write-Host "  ugs scheduler list -p $ProjectId -e $EnvironmentName"
