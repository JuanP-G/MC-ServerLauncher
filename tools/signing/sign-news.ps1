# Firma el archivo de avisos de la web: escribe "<archivo>.sig" al lado de web/news.json.
# Uso:  .\tools\signing\sign-news.ps1            (firma web\news.json)
#       .\tools\signing\sign-news.ps1 -Path .\otro\news.json
#
# Lo que lee la app es NewsFeedService, que comprueba la firma con UpdateSignature.VerifyNews antes de
# mirar siquiera el contenido. Se firma un mensaje corto, igual que UpdateSignature.NewsMessage:
#
#     mcsl-news-v1
#     <sha256 en minúsculas de los bytes del archivo, tal cual están>
#
# La cabecera es distinta de la de los paquetes (mcsl-update-v1) para que la misma clave sirva a las dos
# cosas sin que una firma de un instalador valga como la de un archivo de avisos, ni al revés. La firma
# va en base64 y en texto, y antes de darla por buena se comprueba con la clave pública guardada junto a
# la privada, así un fallo salta aquí y no en el equipo de un usuario.
#
# Se firman los BYTES, no el JSON leído y vuelto a escribir. Por eso el script se niega a firmar un
# archivo con retornos de carro (CRLF) o con marca de orden de bytes: en esta máquina git convierte
# los finales de línea, y una firma hecha sobre la copia CRLF no verificaría sobre el LF que sube a
# la web. Lo que impide que git los convierta es la línea "web/news.json -text" de .gitattributes.
#
# También se niega a firmar lo que la app descartaría sin avisar: un aviso de incidencia o de
# seguridad sin "until", o uno "resolved" sin "resolvedAt". Es preferible un error aquí a un aviso que
# nadie ve.
#
# El archivo CADUCA. El campo "expires" (firmado con el resto, así que nadie que sirva el archivo puede
# alargarlo) dice hasta cuándo da fe de ser el último: pasada esa fecha la app lo trata como desfasado
# y lo dice en pantalla, en vez de dejar que una copia vieja pase por la actual. Este script lo escribe
# solo, a -ExpiresInDays días desde ahora (60 por defecto, 90 como mucho), ANTES de firmar: renovar el
# archivo es volver a ejecutarlo, sin tocar nada más. No cambia "issued" (es la fecha por defecto de
# "from" de los avisos) ni "seq": súbelo a mano antes de ejecutarlo, también al renovar (la app acepta el
# mismo número, pero entonces el archivo de antes de la renovación podría volver a servirse). Hay que volver a firmar antes de que caduque: la fecha sale impresa al final.
param(
    [string]$Path,
    # Para quien ya pidió la contraseña.
    [string]$Passphrase,
    # Cuánto tiempo da fe el archivo de ser el último. Más de 90 días sería no caducar nunca de hecho.
    [ValidateRange(1, 90)]
    [int]$ExpiresInDays = 60
)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\common.ps1"

if (-not $Path) { $Path = Join-Path $PSScriptRoot "..\..\web\news.json" }
$file = (Resolve-Path $Path).Path
$bytes = [IO.File]::ReadAllBytes($file)

if ($bytes.Length -eq 0) { throw "$file está vacío." }
if ($bytes.Length -gt 65536) { throw "$file pesa más de 64 KiB: la app lo rechazaría." }
if ($bytes -contains 13) {
    throw "$file tiene retornos de carro (CRLF). Guárdalo con finales de línea LF: la firma se hace sobre los bytes y la web los sirve tal cual."
}
if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
    throw "$file empieza con una marca de orden de bytes (BOM). Guárdalo como UTF-8 sin BOM."
}

# La caducidad se calcula ANTES de mirar nada más, porque cambia los bytes que se firman: sustituye
# la línea "expires" si ya existe y, si no, la añade debajo de "issued". Solo toca esa línea. Se hace
# en memoria: el archivo no se escribe hasta que la firma se ha hecho Y verificado, de modo que una
# contraseña equivocada o un fallo de openssl no dejan un news.json con fecha nueva junto a un .sig viejo.
$expires = [DateTime]::UtcNow.AddDays($ExpiresInDays).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
    [Globalization.CultureInfo]::InvariantCulture)
$text = [Text.Encoding]::UTF8.GetString($bytes)
if ($text -match '"expires"\s*:\s*"[^"]*"') {
    $text = [regex]::Replace($text, '("expires"\s*:\s*)"[^"]*"', {
        param($m) $m.Groups[1].Value + '"' + $expires + '"' })
} elseif ($text -match '(?m)^[ \t]*"issued"\s*:\s*"[^"]*",?[ \t]*$') {
    $text = [regex]::Replace($text, '(?m)^([ \t]*)("issued"\s*:\s*"[^"]*"),?[ \t]*$', {
        param($m) $m.Groups[1].Value + $m.Groups[2].Value + ",`n" + $m.Groups[1].Value + '"expires": "' + $expires + '",' })
} else {
    throw "No encuentro la línea ""issued"" para añadir debajo ""expires"". Añade a mano una línea ""expires"": ""AAAA-MM-DDThh:mm:ssZ""."
}
$bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($text)

# Que sea JSON y que cumpla lo que la app exige; lo demás lo comprueba la app al leerlo.
try { $feed = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json }
catch { throw "$file no es JSON válido: $($_.Exception.Message)" }
if ($feed.schema -ne 1) { throw "schema debe ser 1 (es '$($feed.schema)')." }
if (-not $feed.seq) { throw "Falta seq: cada versión del archivo lleva un número mayor que la anterior." }
$issuedText = [regex]::Match($text, '"issued"\s*:\s*"([^"]*)"').Groups[1].Value
$issuedAt = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse($issuedText, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$issuedAt)) {
    throw "issued ('$issuedText') no es una fecha ISO 8601: la app rechazaría el archivo."
}
if ([DateTimeOffset]::Parse($expires, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal) -le $issuedAt) {
    throw "expires ($expires) no es posterior a issued ($issuedText): la app rechazaría el archivo."
}
foreach ($item in @($feed.items)) {
    $isUrgent = $item.kind -in @("incident", "security")
    if ($isUrgent -and -not $item.until) {
        throw "El aviso '$($item.id)' es de tipo $($item.kind) y no tiene 'until': la app lo descartaría."
    }
    if ($isUrgent -and $item.status -eq "resolved" -and -not $item.resolvedAt) {
        throw "El aviso '$($item.id)' está resuelto y no tiene 'resolvedAt': la app lo descartaría."
    }
}

if (-not (Test-Path $script:PrivateKeyPath)) {
    throw "No hay clave de firma en $script:PrivateKeyPath. Genérala con tools\signing\new-key.ps1."
}
if (-not $Passphrase) { $Passphrase = Read-SigningPassphrase }

$env:MCSL_KEY_PASS = $Passphrase
$work = Join-Path ([IO.Path]::GetTempPath()) ("mcsl-sign-" + [IO.Path]::GetRandomFileName())
New-Item -ItemType Directory $work | Out-Null
try {
    # El resumen es el de los bytes que se van a escribir (aún no están en el disco).
    $sha = [Security.Cryptography.SHA256]::Create()
    $hash = ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLower()
    $sha.Dispose()
    $message = Join-Path $work "message.txt"
    $der = Join-Path $work "sig.der"

    # UTF-8 sin BOM y sin salto final, tal cual lo arma la app.
    [IO.File]::WriteAllBytes($message,
        (New-Object Text.UTF8Encoding($false)).GetBytes("mcsl-news-v1`n$hash"))

    Invoke-OpenSsl @("dgst", "-sha256", "-sign", $script:PrivateKeyPath,
        "-passin", "env:MCSL_KEY_PASS", "-out", $der, $message) | Out-Null
    Invoke-OpenSsl @("dgst", "-sha256", "-verify", $script:PublicKeyPath,
        "-signature", $der, $message) | Out-Null

    # Solo ahora, con la firma ya verificada, se escribe el archivo (con su fecha) y su firma.
    [IO.File]::WriteAllBytes($file, $bytes)
    $sigPath = "$file.sig"
    [IO.File]::WriteAllText($sigPath,
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($der)) + "`n")
    Write-Host "==> Firmado: $(Split-Path $file -Leaf) (seq $($feed.seq), $(@($feed.items).Count) avisos) -> $(Split-Path $sigPath -Leaf)" -ForegroundColor Green
    Write-Host "    Caduca el ${expires}: vuelve a ejecutar este script y sube el archivo y su .sig antes de esa fecha." -ForegroundColor Yellow
} finally {
    Remove-Item Env:\MCSL_KEY_PASS -ErrorAction SilentlyContinue
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
