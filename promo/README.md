# Vídeo promocional

El anuncio de MC Server Launcher, hecho con código: [Remotion](https://www.remotion.dev/) para la imagen,
Python para la música y el propio programa (grabado sin pantalla) para las imágenes de la app.

Tres montajes, cada uno en cada idioma:

| Composición | Formato | Duración | Para |
|---|---|---|---|
| `promo45-<idioma>` | 1920 × 1080 | 45 s | YouTube, la web, el README |
| `vertical30-<idioma>` | 1080 × 1920 | 30 s | TikTok, Reels, Shorts |
| `short15-<idioma>` | 1080 × 1920 | 15 s | Anuncios e historias |

La estructura de los tres está en [`src/edits.json`](src/edits.json): secciones medidas en pulsos a 150 BPM.
La música se compone a partir del mismo fichero, así que cada golpe cae en un corte.

## Ponerlo en marcha

```bash
npm install
node scripts/index-footage.mjs   # después de grabar
python audio/compose.py          # música y efectos (numpy y scipy)
npx remotion studio              # vista previa
npx remotion render promo45-es out/promo45-es.mp4
```

Las skills oficiales de Remotion para Claude Code no van en git; se instalan con
`npx skills add remotion-dev/skills -a claude-code -s '*' -y --copy`.

Lo generado no se guarda en git: `public/footage/` (las grabaciones), `public/audio/` (la música) y `out/`.

## Las imágenes de la app

[`recorder/`](recorder/) abre las vistas reales del programa sin pantalla, con servidores inventados, y las
maneja con ratón y teclado simulados. Cada fotograma sale al doble de resolución (3200 × 1800), para poder
acercar la cámara sin que se pixele.

```bash
dotnet build recorder -c Release
recorder/bin/Release/net9.0/Recorder.exe public/footage es hero create mods internet bedrock players backups sleep console
node scripts/index-footage.mjs
```

Escenas: `hero` (la ventana quieta), `create`, `mods` (busca en Modrinth de verdad: necesita Internet),
`internet`, `bedrock`, `players`, `backups`, `sleep` y `console`. Graba cada idioma por separado.

## Añadir un idioma

1. Copia [`src/i18n/en.ts`](src/i18n/en.ts) como `src/i18n/<idioma>.ts`, traduce los textos y añádelo a
   `LANGUAGES` en [`src/i18n/index.ts`](src/i18n/index.ts).
2. Graba las escenas en ese idioma (el grabador entiende `es`, `en`, `pt`, `fr` y `de`). Sin grabación propia,
   el vídeo usa la inglesa.
3. Renderiza `promo45-<idioma>`, `vertical30-<idioma>` y `short15-<idioma>`.

Los títulos usan la letra del juego: solo en tamaños múltiplos de 8 px, o se emborrona. Si un título no cabe,
acórtalo en el fichero del idioma antes de tocar el tamaño.

## La música

[`audio/compose.py`](audio/compose.py) sintetiza todo: bombo, caja, bajo con sidechain, arpegios de chiptune,
acordes supersaw, subidas de ruido y efectos (clic, golpe, bloques). No hay muestras ni pistas de nadie, así
que el vídeo se puede publicar y monetizar en YouTube sin reclamaciones de Content ID.
[`audio/analyze_mix.py`](audio/analyze_mix.py) dibuja el espectrograma y el volumen por sección.

Para usar otra pista: cámbiala en [`src/Edit.tsx`](src/Edit.tsx) y ajusta `bpm` en `edits.json` a su tempo.
