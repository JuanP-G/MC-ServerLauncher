# Firma los paquetes de una release ya publicada y sube las firmas.
# Uso:  .\tools\signing\sign-release.ps1 -Tag v1.21.0
#
# Los workflows de Linux y macOS construyen el AppImage y los .dmg en GitHub Actions, y la clave no
# puede estar allí. Así que la firma se hace aquí, en cuanto esos workflows terminan: este script
# espera a que estén todos los paquetes, comprueba cada uno contra su .sha256, los firma con la clave
# local y sube los .sig. Mientras no estén subidos, la app lleva a quien actualice a la página de la
# release en vez de instalar.
#
# Firma lo que GitHub Actions ha construido. Si el repositorio o un workflow estuvieran comprometidos,
# firmarías un paquete malicioso. Por eso el script imprime el hash de cada paquete antes de firmarlo
# y los workflows lo dejan también en su log: si algo te huele raro, compáralos antes de escribir la
# contraseña (ver docs/articles/signing.md).
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Repo = "JuanP-G/MC-ServerLauncher"
)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\common.ps1"

# El instalador de Windows se firma en publish.ps1, donde se construye; aquí solo lo que construye
# GitHub Actions.
$wanted = @(
    "MC-ServerLauncher-x86_64.AppImage",
    "MC-ServerLauncher-AppleSilicon.dmg",
    "MC-ServerLauncher-Intel.dmg"
)

$work = Join-Path ([IO.Path]::GetTempPath()) ("mcsl-release-" + [IO.Path]::GetRandomFileName())
New-Item -ItemType Directory $work | Out-Null
try {
    # Hasta cinco minutos por los workflows de Linux y macOS.
    $deadline = (Get-Date).AddMinutes(15)
    do {
        $assets = (gh release view $Tag --repo $Repo --json assets | ConvertFrom-Json).assets.name
        $missing = $wanted | Where-Object { $_ -notin $assets }
        if ($missing) {
            if ((Get-Date) -gt $deadline) { throw "Faltan en la release ${Tag}: $($missing -join ', ')" }
            Write-Host "Esperando a: $($missing -join ', ')..." -ForegroundColor Yellow
            Start-Sleep -Seconds 20
        }
    } while ($missing)

    foreach ($name in $wanted) { gh release download $Tag --repo $Repo --pattern $name --dir $work }
    gh release download $Tag --repo $Repo --pattern "*.sha256" --dir $work

    # El .sha256 de cada paquete tiene que coincidir con el paquete descargado: lo mínimo antes de
    # poner la firma encima.
    foreach ($name in $wanted) {
        $actual = (Get-FileHash (Join-Path $work $name) -Algorithm SHA256).Hash.ToLower()
        $expected = (Get-Content (Join-Path $work "$name.sha256") |
            Where-Object { $_ -match [regex]::Escape($name) } | Select-Object -First 1) -split "\s+" |
            Select-Object -First 1
        if ($expected -ne $actual) { throw "$name no coincide con su checksum (esperado $expected, obtenido $actual). No firmo." }
        Write-Host "$name  $actual" -ForegroundColor DarkGray
    }

    $files = $wanted | ForEach-Object { Join-Path $work $_ }
    & "$PSScriptRoot\sign-package.ps1" -Path $files

    $sigs = $wanted | ForEach-Object { Join-Path $work "$_.sig" }
    gh release upload $Tag @sigs --clobber --repo $Repo
    Write-Host "==> Firmas subidas a $Tag." -ForegroundColor Green
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
