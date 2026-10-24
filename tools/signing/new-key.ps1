# Genera la clave con la que se firman las actualizaciones de la app (ECDSA P-256).
# Uso:  .\tools\signing\new-key.ps1
#
# Se ejecuta UNA vez (y otra cuando haya que rotarla, ver docs/articles/signing.md). Pide una
# contraseña, deja la clave privada cifrada en $env:USERPROFILE\.mcsl-signing\ (o en MCSL_SIGNING_DIR)
# y enseña la clave pública que hay que pegar en UpdateSignature.TrustedKeys.
#
# La clave privada NO va al repositorio ni a un secreto de GitHub: si estuviera en GitHub, quien
# comprometa GitHub podría firmar, y todo esto no serviría de nada. Guarda una copia de seguridad
# fuera del equipo: sin ella, las apps ya instaladas no podrán volver a actualizarse solas.
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\common.ps1"

if (Test-Path $script:PrivateKeyPath) {
    throw "Ya existe una clave en $script:PrivateKeyPath. Para rotarla, muévela antes: este script no sobrescribe."
}
New-Item -ItemType Directory -Force $script:SigningDir | Out-Null

$pass  = Read-SigningPassphrase "Contraseña nueva para la clave"
$again = Read-SigningPassphrase "Repítela"
if ($pass -ne $again) { throw "Las contraseñas no coinciden." }
if ($pass.Length -lt 12) { throw "Usa una contraseña de al menos 12 caracteres." }

$env:MCSL_KEY_PASS = $pass
try {
    Invoke-OpenSsl @("genpkey", "-algorithm", "EC", "-pkeyopt", "ec_paramgen_curve:P-256",
        "-aes-256-cbc", "-pass", "env:MCSL_KEY_PASS", "-out", $script:PrivateKeyPath) | Out-Null
    Invoke-OpenSsl @("pkey", "-in", $script:PrivateKeyPath, "-passin", "env:MCSL_KEY_PASS",
        "-pubout", "-out", $script:PublicKeyPath) | Out-Null
} finally {
    Remove-Item Env:\MCSL_KEY_PASS -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Clave privada (cifrada): $script:PrivateKeyPath" -ForegroundColor Green
Write-Host "Clave pública (PEM):     $script:PublicKeyPath" -ForegroundColor Green
Write-Host ""
Write-Host "Pega esto en McServerLauncher/Services/UpdateSignature.cs, en TrustedKeys:" -ForegroundColor Cyan
Write-Host ("    `"" + (Get-PublicKeyBase64) + "`"")
Write-Host ""
Write-Host "Ahora haz una copia de seguridad de la clave privada FUERA de este equipo." -ForegroundColor Yellow
