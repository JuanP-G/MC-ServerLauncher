# Firma uno o varios paquetes: escribe "<paquete>.sig" al lado de cada uno.
# Uso:  .\tools\signing\sign-package.ps1 -Path .\dist\MC-ServerLauncher-Setup-1.21.0.exe
#
# La firma es la de openssl (ECDSA P-256 sobre SHA-256) sobre los bytes del paquete, en base64 y en
# texto, que es lo que lee UpdateSignature en la app. Antes de darla por buena se comprueba con la
# clave pública guardada junto a la privada, así un fallo salta aquí y no en el equipo de un usuario.
param(
    [Parameter(Mandatory)][string[]]$Path,
    # Para quien ya pidió la contraseña (sign-release.ps1 la pide una vez para todos los paquetes).
    [string]$Passphrase
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

        Invoke-OpenSsl @("dgst", "-sha256", "-sign", $script:PrivateKeyPath,
            "-passin", "env:MCSL_KEY_PASS", "-out", $der, $file) | Out-Null
        Invoke-OpenSsl @("dgst", "-sha256", "-verify", $script:PublicKeyPath,
            "-signature", $der, $file) | Out-Null

        $sigPath = "$file.sig"
        [IO.File]::WriteAllText($sigPath,
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($der)) + "`n")
        Remove-Item $der
        Write-Host "==> Firmado: $(Split-Path $file -Leaf) -> $(Split-Path $sigPath -Leaf)" -ForegroundColor Green
    }
} finally {
    Remove-Item Env:\MCSL_KEY_PASS -ErrorAction SilentlyContinue
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
