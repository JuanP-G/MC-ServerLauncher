# Publica la app (self-contained, sin que el usuario instale .NET) y genera el instalador.
# Uso:  .\publish.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "==> Publicando MC Server Launcher (win-x64, self-contained)..." -ForegroundColor Cyan
dotnet publish "$root\McServerLauncher\McServerLauncher.csproj" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=false

# Buscar el compilador de Inno Setup
$iscc = $null
foreach ($p in @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
    if (Test-Path $p) { $iscc = $p; break }
}
if (-not $iscc) { $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue; if ($cmd) { $iscc = $cmd.Source } }

if ($iscc) {
    Write-Host "==> Generando instalador con Inno Setup..." -ForegroundColor Cyan
    & $iscc "$root\installer\MC-ServerLauncher.iss"
    Write-Host "==> Instalador creado en: $root\dist" -ForegroundColor Green
} else {
    Write-Host "Inno Setup no está instalado. Instálalo desde https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    Write-Host "y vuelve a ejecutar este script para generar el instalador." -ForegroundColor Yellow
}
