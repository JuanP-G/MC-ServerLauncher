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

# La versión vive en el .csproj (fuente única de verdad); se la pasamos a Inno Setup para que el
# nombre del instalador y su AppVersion siempre coincidan sin tocar el .iss a mano.
[xml]$csproj = Get-Content "$root\McServerLauncher\McServerLauncher.csproj"
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1

# Una release no debe salir sin sus notas de novedades: comprobamos que Changelog.cs tiene la
# entrada de esta versión (su clave Whatsnew_x_y_z en los 5 .resx va de la mano de esa entrada).
$verParts = $version.Split('.')
$entry = "new Version($($verParts[0]), $($verParts[1]), $($verParts[2]))"
$changelog = Get-Content "$root\McServerLauncher\Services\Changelog.cs" -Raw
if ($changelog -notlike "*$entry*") {
    throw ("Services/Changelog.cs no tiene entrada para la versión $version ($entry). " +
           "Añádela al principio de Entries (y su clave Whatsnew_$($verParts -join '_') en los 5 .resx) antes de publicar.")
}

if ($iscc) {
    Write-Host "==> Generando instalador con Inno Setup (versión $version)..." -ForegroundColor Cyan
    & $iscc "/DMyAppVersion=$version" "$root\installer\MC-ServerLauncher.iss"
    Write-Host "==> Instalador creado en: $root\dist" -ForegroundColor Green

    # SHA256SUMS.txt: el actualizador in-app (UpdateService) lo busca como asset de la release para
    # verificar el .exe antes de ejecutarlo. Lo generamos para el instalador recién creado.
    $setup = "MC-ServerLauncher-Setup-$version.exe"
    $setupPath = Join-Path "$root\dist" $setup
    if (Test-Path $setupPath) {
        $hash = (Get-FileHash $setupPath -Algorithm SHA256).Hash.ToLower()
        "$hash  $setup" | Out-File -FilePath "$root\dist\SHA256SUMS.txt" -Encoding ascii -NoNewline
        Write-Host "==> SHA256SUMS.txt generado para $setup" -ForegroundColor Green
    } else {
        Write-Host "AVISO: no encontré $setup en dist\; no se generó SHA256SUMS.txt." -ForegroundColor Yellow
    }
} else {
    Write-Host "Inno Setup no está instalado. Instálalo desde https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    Write-Host "y vuelve a ejecutar este script para generar el instalador." -ForegroundColor Yellow
}
