# Cómo contribuir

> 🇬🇧 Prefer English? Read the [English version](contributing.md).

¡Gracias por querer ayudar! Esta guía explica cómo compilar el proyecto y cómo hacer los cambios más
habituales.

## Requisitos

- **Windows** 10/11 (la interfaz usa WPF, exclusivo de Windows).
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
1. Añade el control + etiqueta/descripción en `Views/ServerConfigDialog.xaml` (y enlázalo en su
   code-behind).
2. Lee/escribe la clave con `ServerPropertiesService.Read` / `Update`, que conserva el resto del
   archivo, los comentarios y el orden.

### Añadir un diálogo o servicio nuevo
- **Diálogo:** crea `Views/MiDialogo.xaml` + `.xaml.cs` como `ui:FluentWindow`, localiza sus textos
  con `{loc:Loc ...}` y ábrelo desde un comando del ViewModel (mira `WhatsNewDialog` o
  `PlayitApiKeyDialog` como ejemplos pequeños).
- **Servicio:** añade una clase centrada en `Services/`, sin interfaz, e instánciala desde el
  ViewModel correspondiente (mira cómo `ServerViewModel` compone sus servicios).

### Sacar una versión nueva
1. Sube `<Version>` en `McServerLauncher/McServerLauncher.csproj` **y** `MyAppVersion` en
   `installer/MC-ServerLauncher.iss`.
2. Ejecuta `publish.ps1` (publish self-contained + instalador de Inno Setup en `dist/`).
3. `gh release create vX.Y.Z dist/MC-ServerLauncher-Setup-X.Y.Z.exe` con notas **bilingües**.
4. El actualizador de la app busca el asset `.exe` en la última release, así que adjúntalo siempre.

## Sitio de documentación (GitHub Pages)

La referencia de API + estos artículos se publican automáticamente en GitHub Pages mediante
`.github/workflows/docs.yml` en cada push a `main`. El sitio se genera con **DocFX** a partir de los
comentarios `///` y el markdown de `docs/`.

> **Paso único del propietario del repo:** activar Pages en **Settings → Pages → Source: “GitHub
> Actions”**. A partir de ahí, cada push actualiza el sitio solo.
