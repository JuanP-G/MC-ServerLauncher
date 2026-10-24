# Actualizaciones firmadas

> Read this in [English](signing.md).

Cómo sabe la app que una actualización la ha publicado quien mantiene el proyecto, y qué hacer cuando la
clave que lo demuestra se pierde, se filtra o toca cambiarla. Seguimiento en
[#33](https://github.com/JuanP-G/MC-ServerLauncher/issues/33).

## Por qué un checksum no bastaba

El actualizador descarga el paquete de la release de GitHub y lo compara con un checksum
(`SHA256SUMS.txt`, o `<paquete>.sha256`) que viene **de la misma release**. Eso detecta una descarga
dañada o truncada. No detecta una cuenta o una release comprometidas: quien pueda subir el paquete puede
subir también su hash, y en Windows el instalador se ejecuta después en silencio y con elevación.

Una firma cierra ese hueco porque la clave que la hace **no vive en GitHub**.

## Qué se firma y cómo se comprueba

| | |
| --- | --- |
| Algoritmo | ECDSA sobre P-256 con SHA-256 |
| Qué se firma | Los bytes del paquete: el `.exe`, el `.AppImage` o cada `.dmg` |
| Dónde está la firma | Un asset `<paquete>.sig` en la misma release: la firma DER, en base64, como texto |
| Dónde está la clave pública | En la app: `UpdateSignature.TrustedKeys` (SubjectPublicKeyInfo, base64) |
| Dónde está la clave privada | En el equipo de quien mantiene el proyecto, cifrada, fuera del repositorio y de GitHub |

ECDSA P-256 es lo que trae .NET, así que el camino de actualización **no añade ningún paquete**, y es lo
que producen `openssl` y `cosign`, así que cualquiera puede comprobar una release a mano (más abajo).

En `MainViewModel.UpdateNow`, antes de instalar nada:

1. Se leen el checksum y la firma. **Los dos son obligatorios**: si falta alguno o no se puede leer, la
   actualización se rechaza y se abre la página de la release, como ya pasaba con el checksum.
2. Se descarga el paquete y se comprueba su checksum.
3. `UpdateSignature.Verify` comprueba la firma contra cada clave de confianza. Si ninguna la acepta, el
   paquete se borra, se avisa al usuario y se abre la página de la release. No se instala nada.

`Verify` nunca lanza una excepción por una entrada mala: el archivo de firma se descarga del mismo sitio
donde escribiría un atacante, y un fallo sería una forma de saltarse la comprobación.

## Publicar una release

1. `publish.ps1` construye el instalador y, si existe la clave, lo firma: `dist/…Setup-X.Y.Z.exe.sig`.
   Adjúntalo a la release junto al `.exe` y al `SHA256SUMS.txt`.
2. Publicar la release dispara los workflows de Linux y macOS, que adjuntan el AppImage, los dos `.dmg` y
   sus `.sha256`. **No pueden firmar**: eso pondría la clave en GitHub.
3. Cuando terminen, ejecuta `tools\signing\sign-release.ps1 -Tag vX.Y.Z`. Espera a los paquetes, los
   descarga, comprueba cada uno contra su `.sha256`, imprime los hashes, pide una vez la contraseña de la
   clave, los firma y sube los `.sig`.

Entre el paso 2 y el 3 (unos cinco minutos) quien pulse Actualizar en Linux o macOS irá a la página de
la release en vez de actualizarse: el fallo seguro.

Los hashes que imprime el paso 3 están también en los logs de los workflows. Este flujo firma **lo que
GitHub Actions construyó**, así que si dudas alguna vez de la build, compáralos antes de escribir la
contraseña.

## La clave

Se crea una vez con `tools\signing\new-key.ps1`. Pide una contraseña (12 caracteres como mínimo), escribe
la clave privada cifrada en `%USERPROFILE%\.mcsl-signing\` (o en `MCSL_SIGNING_DIR`) e imprime la clave
pública que hay que pegar en `UpdateSignature.TrustedKeys`.

Guarda una **copia de seguridad fuera del equipo** (un adjunto en un gestor de contraseñas, un disco
cifrado). Si la clave se pierde, todas las apps instaladas siguen confiando en ella y no se puede firmar
nada con ella: nadie podría actualizar desde la app y todos tendrían que reinstalar a mano.

### Cambiarla

`TrustedKeys` es una lista para poder sustituir una clave sin dejar a nadie atrás. Una app instalada solo
confía en las claves con las que se publicó, así que el orden importa:

1. Crea la clave nueva (en otro `MCSL_SIGNING_DIR`; el script se niega a sobrescribir).
2. **Release N**: `TrustedKeys = [nueva, vieja]`, y los paquetes se firman con la clave **vieja**, porque
   las apps que van a instalar la N solo confían en esa.
3. **Release N+1**: firmada con la **nueva**. Las apps que estén en la N confían en las dos.
4. Cuando ya no quede nadie en la N−1 o anteriores, quita la clave vieja de la lista.

### Si la clave se filtra

Las apps instaladas siguen confiando en ella, así que quien la tenga puede firmar una actualización que
aceptarían. La única solución es una release que la quite de `TrustedKeys`, firmada con la última vez que
se use la clave filtrada, así que hay que sacarla rápido; publica además un aviso de seguridad. Por eso la
clave va cifrada y fuera de GitHub.

## Lo que esto no cubre

- **Las versiones anteriores a la primera firmada** (la 1.21.1, publicada el 2026-10-24, es la primera)
  no comprueban nada. La protección empieza con la actualización *posterior* a la que lleva la clave;
  instalar esa primera sigue dependiendo del checksum.
- **Volver a una versión vieja.** Una release antigua, bien firmada, sigue estando bien firmada. El
  actualizador solo ofrece versiones más nuevas que la que corre, pero no ata la firma a un número de
  versión.
- **Authenticode** (firmar el `.exe` de Windows con un certificado de código, lo que además quita el aviso
  de SmartScreen) es un paso aparte y opcional que **no está hecho**. Necesita un certificado que no es
  gratuito, salvo programas para proyectos de código abierto.
- **La app de macOS solo lleva firma ad hoc**, como dice el workflow; la notarización necesita una cuenta
  de Apple Developer.
- **Lo que firma quien mantiene el proyecto es lo que produjo la build.** Una firma demuestra quién
  publicó, no que la build estuviera limpia.

## Comprobar una release a mano

Con la clave pública escrita en un PEM (`BEGIN PUBLIC KEY`, el base64 de `UpdateSignature.cs` cortado en
líneas de 64 columnas, `END PUBLIC KEY`):

```bash
base64 -d MC-ServerLauncher-x86_64.AppImage.sig > sig.der
openssl dgst -sha256 -verify public.pem -signature sig.der MC-ServerLauncher-x86_64.AppImage
```

Imprime `Verified OK` cuando el paquete es el que se firmó.
