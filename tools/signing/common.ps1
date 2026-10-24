# Piezas compartidas por los scripts de firma de las actualizaciones. No se ejecuta solo:
# new-key.ps1, sign-package.ps1 y sign-release.ps1 lo cargan con ". $PSScriptRoot\common.ps1".
#
# Las firmas son ECDSA P-256 sobre SHA-256 con openssl; la app las comprueba con
# McServerLauncher/Services/UpdateSignature.cs y la explicación entera está en
# docs/articles/signing.md.

# Dónde vive la clave. Fuera del repositorio, y fuera de GitHub: ese es todo el sentido de firmar.
# MCSL_SIGNING_DIR la mueve (un USB, una carpeta cifrada); por defecto, el perfil del usuario.
$script:SigningDir = if ($env:MCSL_SIGNING_DIR) { $env:MCSL_SIGNING_DIR }
                     else { Join-Path $env:USERPROFILE ".mcsl-signing" }
$script:PrivateKeyPath = Join-Path $script:SigningDir "update-signing.key.pem"
$script:PublicKeyPath  = Join-Path $script:SigningDir "update-signing.pub.pem"

function Find-OpenSsl {
    $cmd = Get-Command openssl -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    # Git for Windows lo trae aunque no esté en el PATH.
    foreach ($p in @("C:\Program Files\Git\usr\bin\openssl.exe", "C:\Program Files (x86)\Git\usr\bin\openssl.exe")) {
        if (Test-Path $p) { return $p }
    }
    throw "No encuentro openssl. Instala Git for Windows (lo incluye) o añade openssl al PATH."
}

# Ejecuta openssl y falla con su salida si devuelve error. La contraseña de la clave viaja en una
# variable de entorno del propio proceso (-passin env:...), no en la línea de comandos.
function Invoke-OpenSsl {
    param([string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"   # PowerShell 5.1 trata el stderr de un .exe como error
    try {
        $output = & (Find-OpenSsl) @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) { throw "openssl $($Arguments -join ' ') falló:`n$output" }
        return $output
    } finally { $ErrorActionPreference = $previous }
}

function Read-SigningPassphrase {
    param([string]$Prompt = "Contraseña de la clave de firma")
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# La clave pública tal como la espera UpdateSignature.TrustedKeys: el SubjectPublicKeyInfo (el
# cuerpo del PEM) en una sola línea de base64.
function Get-PublicKeyBase64 {
    param([string]$PublicKeyPem = $script:PublicKeyPath)
    $lines = Get-Content $PublicKeyPem | Where-Object { $_ -and $_ -notmatch "^-----" }
    return ($lines -join "")
}
