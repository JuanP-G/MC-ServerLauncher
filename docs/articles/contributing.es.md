# Cómo contribuir

> 🇬🇧 Prefer English? Read the [English version](contributing.md).

¡Gracias por querer ayudar! Esta guía explica cómo compilar el proyecto y cómo hacer los cambios más
habituales.

## Requisitos

- **Windows**, **Linux** o **macOS** (la interfaz usa Avalonia, multiplataforma).
- **.NET 9 SDK**.
- *(Solo para generar el instalador)* **Inno Setup 6**.

## Compilar y ejecutar

```powershell
git clone https://github.com/JuanP-G/MC-ServerLauncher.git
cd MC-ServerLauncher
dotnet run --project McServerLauncher
```

Generar el sitio de documentación en local (esta página):

```powershell
dotnet tool install -g docfx   # solo la primera vez
.\docs\build-docs.ps1          # compila y sirve en http://localhost:8080
```

## Convenciones

- **Los comentarios y nombres del código van en inglés.** El texto que ve el usuario **no** se
  escribe a mano: pasa por el sistema de localización (ver abajo).
- Mantén la separación **MVVM**: lógica en `Services/`, estado/comandos enlazables en `ViewModels/`,
  y solo code-behind ligero en `Views/`.
- **Nada de rutas absolutas del equipo.** Usa `Environment.GetFolderPath(...)` y `%APPDATA%`.
- Los tipos/miembros públicos llevan un resumen XML `///` — es lo que alimenta la referencia de API.

## Recetas paso a paso

### Añadir un idioma
1. Copia `Resources/Strings.resx` a `Resources/Strings.<código>.resx` (p. ej. `Strings.it.resx`) y
   traduce cada `<value>`.
2. Añade el código a `<SatelliteResourceLanguages>` en `McServerLauncher.csproj`.
3. Añádelo a la lista `Languages` de `MainViewModel` para que salga en el selector de la barra lateral.

### Añadir un texto traducible
1. Añade la misma entrada `<data name="MiClave">` a **los cinco** archivos `.resx` con la traducción.
2. Úsalo desde XAML como `{loc:Loc MiClave}`, o desde código como `Localizer.Get("MiClave")`
   (usa `string.Format(Localizer.Get("MiClave"), arg)` si tiene huecos `{0}`).

### Añadir un ajuste de `server.properties` al editor visual
1. Añade el control + etiqueta/descripción en `Views/ServerConfigDialog.axaml` (y enlázalo en su
   code-behind).
2. Lee/escribe la clave con `ServerPropertiesService.Read` / `Update`, que conserva el resto del
   archivo, los comentarios y el orden.

### Añadir un diálogo o servicio nuevo
- **Diálogo:** crea `Views/MiDialogo.axaml` + `.axaml.cs` como una `Window` normal de Avalonia,
  localiza sus textos con `{loc:Loc ...}` y ábrelo desde un comando del ViewModel (mira
  `WhatsNewDialog` o `PlayitApiKeyDialog` como ejemplos pequeños).
- **Servicio:** añade una clase centrada en `Services/`, sin interfaz, e instánciala desde el
  ViewModel correspondiente (mira cómo `ServerViewModel` compone sus servicios).

### Sacar una versión nueva
1. Sube `<Version>` en `McServerLauncher/McServerLauncher.csproj` — es la **fuente única de verdad**.
   `publish.ps1` la lee y se la pasa a Inno Setup. Mantén alineado el fallback `MyAppVersion` del
   `.iss` solo para que una compilación manual/directa con Inno Setup no genere un nombre antiguo.
2. Añade una entrada de **novedades** para que el diálogo de actualización tenga algo que mostrar:
   - Una nueva tupla al principio de `Entries` en `Services/Changelog.cs` (la más nueva primero),
     p. ej. `(new Version(1, 6, 0), "Whatsnew_1_6_0")`.
   - La clave `Whatsnew_x_y_z` correspondiente en **los cinco** archivos `.resx` (el texto en español
     en el neutral `Strings.resx`, las traducciones en el resto). Mira *Añadir un texto traducible*
     más arriba.
3. Ejecuta `publish.ps1`. Publica el build self-contained `win-x64`, genera el instalador de Inno
   Setup en `dist/` **y** escribe `dist/SHA256SUMS.txt` junto a él.
4. Crea la release con **ambos** assets y notas **bilingües**:
   ```powershell
   gh release create vX.Y.Z dist/MC-ServerLauncher-Setup-X.Y.Z.exe dist/SHA256SUMS.txt
   ```
   - El actualizador de la app busca el asset `.exe` (así que adjúntalo siempre) **y** el
     `SHA256SUMS.txt`, que usa para verificar el instalador antes de ejecutarlo
     (`UpdateService.CheckAsync`). La verificación es **obligatoria**: sin ese archivo el
     actualizador rechaza la instalación silenciosa y solo abre la página de la release — no lo
     omitas nunca.
5. Publicar la release dispara automáticamente los workflows de **Linux** (`release-linux.yml`) y
   **macOS** (`release-macos.yml`), que generan y adjuntan el `.AppImage` y los dos `.dmg`. No los
   subas a mano — basta con esperar a que terminen los workflows.

## Sitio de documentación (GitHub Pages)

La referencia de API + estos artículos se publican automáticamente en GitHub Pages mediante
`.github/workflows/docs.yml` en cada push a `main`. El sitio se genera con **DocFX** a partir de los
comentarios `///` y el markdown de `docs/`.

> **Paso único del propietario del repo:** activar Pages en **Settings → Pages → Source: “GitHub
> Actions”**. A partir de ahí, cada push actualiza el sitio solo.
