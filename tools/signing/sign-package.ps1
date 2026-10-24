# Firma uno o varios paquetes: escribe "<paquete>.sig2" (y "<paquete>.sig") al lado de cada uno.
# Uso:  .\tools\signing\sign-package.ps1 -Version 1.21.2 -Path .\dist\MC-ServerLauncher-Setup-1.21.2.exe
#
# .sig2 es lo que lee UpdateSignature en la app: la firma de openssl (ECDSA P-256 sobre SHA-256) de un
# mensaje corto, no de los bytes del paquete solos. El mensaje lleva el formato, la versión, el nombre
# del archivo y su SHA-256, uno por línea y sin salto final (igual que UpdateSignature.SignedMessage):
#
#     mcsl-update-v1
#     1.21.2
#     MC-ServerLauncher-Setup-1.21.2.exe
#     <sha256 en minúsculas>
#
# Con la versión dentro, la firma de una release antigua no vale como la de una nueva, y con el nombre
# dentro, la de un .dmg no vale como la del .exe. La firma va en base64 y en texto. Antes de darla por
# buena se comprueba con la clave pública guardada junto a la privada, así un fallo salta aquí y no en
# el equipo de un usuario.
#
# .sig es el formato anterior (los bytes del paquete solos) que leen las apps 1.21.1; se sigue
# escribiendo para que puedan actualizarse. -NoLegacy lo omite, cuando ya no quede nadie en la 1.21.1.
param(
    [Parameter(Mandatory)][string[]]$Path,
    # La versión de la release, sin la "v": 1.21.2. Es la que la app ve en la etiqueta.
    [Parameter(Mandatory)][string]$Version,
    # Para quien ya pidió la contraseña (sign-release.ps1 la pide una vez para todos los paquetes).
    [string]$Passphrase,
    [switch]$NoLegacy
)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\common.ps1"

if (-not (Test-Path $script:PrivateKeyPath)) {
    throw "No hay clave de firma en $script:PrivateKeyPath. Genérala con tools\signing\new-key.ps1."
}
if (-not $Passphrase) { $Passphrase = Read-SigningPassphrase }

$env:MCSL_KEY_PASS = $Passphrase
$work = Join-Path ([IO.Path]::GetTempPath()) ("mcsl-sign-" + [IO.Path]::GetRandomFileName())
New-Item -ItemType Directory $work | Out-Null
try {
    foreach ($file in $Path) {
        $file = (Resolve-Path $file).Path
        $der = Join-Path $work "sig.der"
        $name = Split-Path $file -Leaf

        # .sig2: el mensaje con versión, nombre y hash. UTF-8 sin BOM, tal cual lo arma la app.
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
        $message = Join-Path $work "message.txt"
        [IO.File]::WriteAllBytes($message,
            (New-Object Text.UTF8Encoding($false)).GetBytes("mcsl-update-v1`n$Version`n$name`n$hash"))

        Invoke-OpenSsl @("dgst", "-sha256", "-sign", $script:PrivateKeyPath,
            "-passin", "env:MCSL_KEY_PASS", "-out", $der, $message) | Out-Null
        Invoke-OpenSsl @("dgst", "-sha256", "-verify", $script:PublicKeyPath,
            "-signature", $der, $message) | Out-Null

        $sigPath = "$file.sig2"
        [IO.File]::WriteAllText($sigPath,
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($der)) + "`n")
        Remove-Item $der, $message
        Write-Host "==> Firmado: $name -> $(Split-Path $sigPath -Leaf)" -ForegroundColor Green

        # .sig: el formato de la 1.21.1, la firma de los bytes del paquete solos.
        if (-not $NoLegacy) {
            Invoke-OpenSsl @("dgst", "-sha256", "-sign", $script:PrivateKeyPath,
                "-passin", "env:MCSL_KEY_PASS", "-out", $der, $file) | Out-Null
            Invoke-OpenSsl @("dgst", "-sha256", "-verify", $script:PublicKeyPath,
                "-signature", $der, $file) | Out-Null
            $legacyPath = "$file.sig"
            [IO.File]::WriteAllText($legacyPath,
                [Convert]::ToBase64String([IO.File]::ReadAllBytes($der)) + "`n")
            Remove-Item $der
            Write-Host "==> Firmado: $name -> $(Split-Path $legacyPath -Leaf) (formato 1.21.1)" -ForegroundColor DarkGray
        }
    }
} finally {
    Remove-Item Env:\MCSL_KEY_PASS -ErrorAction SilentlyContinue
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
