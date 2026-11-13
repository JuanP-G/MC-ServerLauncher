# Anuncios

> Read this in [English](announcements.md).

Lo que muestra la sección **Novedades** además de las notas de cada versión: avisos que el proyecto puede
publicar a todas las apps instaladas (una caída de un servicio del que depende la app, un asunto de
seguridad, un consejo), cómo se firman y se leen, qué pide la app para recibirlos y cómo se publica uno.

Para qué existe: cuando falla Playit.gg, quien usa la app no tiene forma de saber que no es la app ni su
servidor. Un aviso lo dice, dentro de la app, sin que nadie tenga que actualizar.

## Qué ve el usuario

- **Avisos en Novedades.** Encima de las versiones: primero lo que sigue en marcha y después lo que
  terminó (en verde, durante una semana). Cada uno es una tarjeta con un anillo y un icono del color de su
  tipo (ámbar para una *incidencia*, rojo para *seguridad*, azul para un *consejo*) y, si lo tiene, un
  botón **Más información**.
- **Una banda bajo la barra de título** para una incidencia o un aviso de seguridad que siga en marcha y
  que el usuario no haya leído ni cerrado. **Ver aviso** abre Novedades, y abrirla cuenta como leerlo, así
  que la banda se va; la cruz la cierra solo para ese aviso sin abrir nada (sigue en la lista). También se
  va sola cuando el aviso se resuelve o caduca.
- **Una notificación**, una sola vez, para una incidencia o un aviso de seguridad nuevos, y otra cuando se
  resuelve una incidencia de la que ya se avisó. No sale cuando la banda ya está delante del usuario con la
  ventana abierta, y respeta el interruptor general de notificaciones.
- **Una marca en el menú** mientras haya algo sin leer, la misma que usan las versiones.
- **Una tarjeta cuando no se pudo comprobar el archivo**: *No se pudo comprobar si hay avisos. Última copia:
  hace 3 h.* Una mirada que falla nunca se enseña como «no hay avisos», porque es justo entonces cuando
  podría haber una incidencia sin ver.
- **Una tarjeta cuando el archivo ha caducado**: *La lista de avisos ha caducado y puede estar
  desactualizada. Última comprobación: hace 3 h.* Un mensaje distinto para algo distinto: la comprobación
  funcionó, y lo que encontró (o lo que tiene la app) está desfasado (ver *El archivo caduca*).

Los consejos nunca usan la banda ni las notificaciones.

**Ajustes → Anuncios** tiene: *Mostrar anuncios* (apagado: ninguna petición, ninguna marca, ninguna banda,
ninguna notificación, y Novedades solo lista versiones), cómo marca el menú lo que está sin leer, dónde sale
la banda (en toda la app, solo en Túneles o en ninguna), si un aviso urgente lanza además una notificación y
si se quieren incidencias y consejos. Los avisos de seguridad no tienen interruptor propio: solo los calla
*Mostrar anuncios*.

## Qué pide la app, y qué ve de ti

Dos direcciones sirven el mismo archivo, y se prueban en orden:

1. `https://mc-server-launcher.vercel.app/news.json`
2. `https://juanp-g.github.io/MC-ServerLauncher/news.json`

Cada mirada es una petición del archivo y otra de su firma (`news.json.sig`), o una sola petición
condicional que responde *no modificado* si no ha cambiado nada. La app mira una vez poco después de
arrancar, cada 5 minutos mientras sigue abierta, y al volver la ventana al frente o al abrir Novedades si
la copia que tiene se confirmó hace más de 2 minutos. Una mirada que no encuentra nada nuevo es una
respuesta *no modificado* sin cuerpo, unos cientos de bytes, así que un aviso urgente llega a una app
abierta en unos cinco minutos y cuesta casi nada.

La petición **no lleva versión, idioma ni identificador**: el servidor ve una dirección y el User-Agent de la
app, como en cualquier descarga. Qué avisos tocan a este equipo se decide **dentro de la app**, a partir del
archivo (ver *Para quién es un aviso*); nada de lo que hace el usuario sale del equipo.

## El archivo

`web/news.json`, UTF-8 con finales de línea **LF** y sin marca de orden de bytes, junto a
`web/news.json.sig`. La web y el workflow de Pages publican la carpeta `web/` tal cual.

```json
{
  "schema": 1,
  "seq": 42,
  "issued": "2026-11-07T14:20:00Z",
  "expires": "2027-01-06T14:20:00Z",
  "items": [
    {
      "id": "playit-2026-11-07",
      "kind": "incident",
      "status": "active",
      "from": "2026-11-07T14:00:00Z",
      "until": "2026-11-09T14:00:00Z",
      "only": { "usesTunnels": true },
      "link": "https://playit.gg/status",
      "text": {
        "es": { "title": "Playit.gg tiene problemas", "body": "Los túneles pueden no conectar." },
        "en": { "title": "Playit.gg is having problems", "body": "Tunnels may fail to connect." }
      }
    }
  ]
}
```

| Campo | Significado |
| --- | --- |
| `schema` | Siempre `1`. Un archivo con otro número se rechaza entero. |
| `seq` | Número entero, mayor en cada versión del archivo. La app guarda el más alto que ha aceptado y **rechaza uno menor**; uno igual se acepta. |
| `issued` | Cuándo se escribió esta versión. |
| `expires` | **Obligatorio.** Cuándo el archivo deja de dar fe de ser el último (ver *El archivo caduca*). Tiene que ser posterior a `issued`; un archivo sin él, o con otro tipo de valor, se rechaza entero. Lo escribe `sign-news.ps1`. |
| `id` | Nombra el aviso para siempre: lo leído, lo cerrado y lo anunciado se recuerda por él. Hasta 64 caracteres entre letras, cifras, `.`, `_` y `-`. |
| `kind` | `incident`, `security` o `tip`. |
| `status` | `active` (por defecto) o `resolved`. Un consejo no tiene nada que resolver: sale mientras esté en el archivo. |
| `from` / `until` | El aviso se muestra desde `from` (por defecto, desde `issued`) hasta `until`. **`until` es obligatorio en una incidencia y en un aviso de seguridad**, y se limita a 30 días desde `from`, así un aviso olvidado no puede volverse permanente. Un consejo puede no tenerlo. |
| `resolvedAt` | Obligatorio cuando `status` es `resolved`. El aviso se retira siete días después, o en `until` si llega antes. |
| `only` | Para quién es (abajo). Si falta, es para todos. |
| `link` | Adónde va *Más información*: `https`, y solo `github.com`, `playit.gg`, `mc-server-launcher.vercel.app` o `juanp-g.github.io`. Cualquier otra cosa se descarta. |
| `text` | `es`, `en`, `de`, `fr` y `pt`, cada uno con un `title` (80 caracteres como mucho) y un `body` (400 como mucho). |

Como mucho 64 KiB por archivo, y la app lee los primeros 50 avisos. Un aviso que no es válido se salta por
su cuenta y se usa el resto del archivo; si dos avisos comparten `id`, cuenta el primero.

El idioma es el de la app; si no está, inglés; si tampoco, el que tenga el aviso.

### Para quién es un aviso

`only` acota un aviso; cada condición que falta no acota nada:

| Condición | Significado |
| --- | --- |
| `usesTunnels` | `true`: solo quien tenga una cuenta de Playit o un servidor configurado con túnel. `false`: solo quien no tenga ninguna de las dos cosas. |
| `os` | Una lista de `windows`, `linux`, `macos`. Una lista vacía a propósito es un error y el aviso se descarta. |
| `minVersion` / `maxVersion` | Las versiones de la app a las que se aplica, ambas incluidas. |

Para los túneles no hace falta que el agente esté conectado: una caída importa a quien depende del
servicio, esté activo o no.

## Cómo se comprueba

Todo lo que sigue ocurre antes de mirar el contenido, y no se escribe nada en disco hasta que pasa.

1. El archivo tiene que llegar entero y dentro del límite de tamaño.
2. Su firma tiene que verificarse, sobre los bytes exactos recibidos, con una de las claves incluidas en la
   app (las mismas que para las actualizaciones; ver [Actualizaciones firmadas](signing.es.md)).
3. Tiene que leerse bien, y su `seq` no puede ser menor que el más alto aceptado alguna vez.
4. Su `expires` no puede haber pasado. Un archivo que ya lo pasó se rechaza como *caducado*, que no es lo
   mismo que una comprobación fallida: ver la sección siguiente.
5. Solo entonces se sustituyen el archivo y su firma, y el ETag se guarda lo último: un ETag guardado para
   un cuerpo que luego se rechazó haría que toda petición posterior respondiera *no modificado* y una firma
   corregida no se recogería nunca.

La copia que guarda la app en su carpeta `cache` (`news.json`, `news.json.sig`, `news.stamp`) se **vuelve a
verificar cada vez que se lee**: esa carpeta puede escribirla cualquier cosa del equipo. Lo leído, lo
cerrado y lo anunciado está en `news-state.json`, aparte de los ajustes.

Si falla cualquier paso se rechaza el archivo entero y se queda la copia anterior; el usuario ve la
tarjeta de arriba. Que las dos direcciones respondan *no encontrado* no es un error: no se ha publicado
nada y no hay avisos.

### El archivo caduca

Una firma vale para siempre, así que por sí sola no distingue el último archivo de uno viejo que se firmó
bien en su día. `expires` va firmado con el resto, de modo que quien sirva el archivo no puede estirarlo, y
la app lo juzga **cada vez que lee el archivo**, no una sola vez al guardarlo:

- Un archivo que llega ya caducado se rechaza y no sustituye nada.
- Un *no modificado* para una copia caducada no es una confirmación: el servidor dice que no hay nada más
  nuevo, y eso no consuela cuando lo que hay está desfasado.
- Una copia que estaba bien al guardarse se da por desfasada en cuanto pasa su fecha, sin tocar la red, y
  también tras reiniciar.

En todos los casos el usuario ve en Novedades una tarjeta que dice que **la lista de avisos ha caducado y
puede estar desactualizada**, con cuándo se confirmó por última vez. Es un mensaje distinto de «no se pudo
comprobar si hay avisos»: la comprobación funcionó, lo viejo es el archivo. Los avisos de la copia caducada
**no se esconden** (una incidencia acaba por su propio `until`, como mucho 30 días después de empezar): se
listan con el aviso, de modo que un archivo caducado nunca hace desaparecer una incidencia en curso. La
banda y las notificaciones se portan como con cualquier copia. Renovar es volver a firmar el archivo con una
fecha posterior; basta el mismo `seq`, y la tarjeta se va en la siguiente comprobación.

Si las dos direcciones discrepan (una sirve un archivo caducado y la otra uno vigente) gana el vigente; si
una no se puede alcanzar el resultado es una comprobación fallida y no un caducado, porque no saber pesa
más que saber qué falla.

## Publicar un aviso

1. Edita `web/news.json` y sube `seq`. Mantén finales de línea **LF** y sin BOM (`.gitattributes` marca el
   archivo y su firma como `-text` para que git no los convierta).
2. Ejecuta `.\tools\signing\sign-news.ps1`. Pide la contraseña de la clave, escribe `expires` (60 días desde
   ahora; `-ExpiresInDays` admite de 1 a 90, porque más sería no caducar nunca en la práctica) y escribe
   `web/news.json.sig`. No toca `issued`, que es el `from` por defecto de los avisos, ni `seq`. Se niega a
   firmar un archivo con CR o BOM, de más de 64 KiB, con una incidencia o aviso de seguridad sin `until`, o
   uno resuelto sin `resolvedAt`: mejor un error aquí que un aviso que nadie ve. La fecha que imprime al
   final es la de renovar.
3. Sube los dos archivos en un pull request. Una vez fusionado, comprueba que las dos direcciones sirven el
   archivo nuevo y que la firma verifica a mano ([Actualizaciones firmadas](signing.es.md#comprobar-una-release-a-mano)).
4. Para terminar una incidencia, no la borres: pon `status` en `resolved` con `resolvedAt`, sube `seq` y
   firma otra vez. La app dice que se resolvió (la banda se va, una notificación, tarjeta verde durante una
   semana). Un aviso que simplemente se quita desaparece sin la buena noticia.
5. **Renueva antes de que caduque**, aunque no haya nada que decir: ejecuta otra vez el script y publica el
   archivo y su firma (un pull request, como en el paso 3). Un buen momento es cada release. Un archivo que
   se deja caducar no hace nada peor que enseñar a todo el mundo la tarjeta de *desactualizada*, pero se la
   enseña a todo el mundo.

## Lo que esto no cubre

- **La frescura, entera.** Una firma demuestra quién escribió el archivo, no que sea el último. `seq`
  impide que se sirva un archivo más viejo tras haber visto uno más nuevo, y `expires` acota el resto:
  quien pueda bloquear la red puede dejar a una app con el último archivo que vio, pero solo hasta que ese
  archivo caduque, y entonces la app lo dice en vez de darlo por vigente. Nada lo impide en los días
  anteriores.
- **Una clave filtrada.** Firma por igual actualizaciones y avisos, así que quien la tenga podría mostrar
  avisos falsos hasta que una release la quite de las claves de confianza. Lo que puede hacer un aviso está
  limitado: texto y un enlace a cuatro sitios. Nunca ejecuta ni descarga nada.
- **Dirigirse a personas.** `only` es grueso a propósito y se decide en el equipo del usuario; no hay forma
  de dirigirse a una sola persona.
