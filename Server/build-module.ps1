# Empaqueta el módulo de Cloud Code como MicrobeOpsModule.ccm (un ZIP con otra extensión).
# Requisitos: .NET SDK 10. Despliegue: ver Server/README.md (ugs deploy).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root "out"
$publish = Join-Path $out "publish"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# Cloud Code ejecuta en Linux x64.
dotnet publish (Join-Path $root "MicrobeOps.Module/MicrobeOps.Module.csproj") `
    -c Release -r linux-x64 --self-contained false -o $publish

$zip = Join-Path $out "MicrobeOpsModule.zip"
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip
Move-Item $zip (Join-Path $out "MicrobeOpsModule.ccm")
Write-Host "Listo: $out\MicrobeOpsModule.ccm"
