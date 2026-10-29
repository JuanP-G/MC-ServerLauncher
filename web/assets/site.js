// Comportamiento de la landing y de las guías. Es la fuente: web/index.html lo enlaza y
// web/_i18n/build.py le pone la versión a la URL.
(function(){
  // Lo que cambia con el idioma de la página lo pone build.py en atributos de este <script>:
  // el archivo es el mismo para todas las versiones y no puede llevar textos traducidos.
  var script = document.currentScript, cfg = script.dataset;
  var reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
  // Lo decorativo (estrellas, paisaje) se dibuja cuando el navegador queda libre, no antes del
  // primer pintado: así el titular y los botones no esperan al trabajo de los fondos.
  var idle = window.requestIdleCallback
    ? function(fn){ requestIdleCallback(fn, { timeout: 1200 }); }
    : function(fn){ setTimeout(fn, 300); };

  // --- estrellas del cielo nocturno ---
  var stars = document.getElementById('stars');
  idle(function(){
  var frag = document.createDocumentFragment();
  // Menos estrellas en pantallas pequeñas: son 90 elementos animados y en un móvil lento
  // solo suman trabajo de estilos.
  for (var i = 0, n = innerWidth < 700 ? 40 : 90; i < n; i++) {
    var s = document.createElement('i');
    s.className = 'star';
    s.style.left = (Math.random() * 100) + '%';
    s.style.top = (Math.random() * 70) + '%';
    s.style.animationDelay = (Math.random() * 4) + 's';
    frag.appendChild(s);
  }
  stars.appendChild(frag);
  });

  // --- titulares que se montan palabra a palabra ---
  // Recorre los nodos hijos en vez de partir el HTML en crudo: así las etiquetas
  // internas (p. ej. la palabra resaltada) sobreviven intactas.
  document.querySelectorAll('.build').forEach(function(el){
    var out = document.createDocumentFragment(), i = 0;
    [].slice.call(el.childNodes).forEach(function(node){
      if (node.nodeType === 3) {
        node.textContent.split(/(\s+)/).forEach(function(tok){
          if (!tok) return;
          if (!tok.trim()) { out.appendChild(document.createTextNode(tok)); return; }
          var s = document.createElement('span');
          s.style.setProperty('--i', i++);
          s.textContent = tok;
          out.appendChild(s);
        });
      } else {
        var w = document.createElement('span');
        w.style.setProperty('--i', i++);
        w.appendChild(node.cloneNode(true));
        out.appendChild(w);
      }
    });
    el.textContent = '';
    el.appendChild(out);
  });

  // --- aparición al entrar en pantalla (bloque colocado / chunk cargado) ---
  var io = new IntersectionObserver(function(entries){
    entries.forEach(function(e){
      if (!e.isIntersecting) return;
      var el = e.target;
      if (el.classList.contains('grid') || el.parentElement.classList.contains('grid') || el.parentElement.classList.contains('os')) {
        var sibs = [].slice.call(el.parentElement.children);
        el.style.animationDelay = (sibs.indexOf(el) * 0.09) + 's';
      }
      el.classList.add('in');
      io.unobserve(el);
      if (el.dataset.count !== undefined) countUp(el);
      el.querySelectorAll && el.querySelectorAll('[data-count]').forEach(countUp);
    });
  }, { threshold: 0.18, rootMargin: '0px 0px -60px 0px' });

  document.querySelectorAll('.reveal, .build, .chunk').forEach(function(el){
    if (reduced) { el.classList.add('in'); el.querySelectorAll('[data-count]').forEach(countUp); return; }
    io.observe(el);
  });

  // --- contadores ---
  function countUp(el){
    if (el.dataset.done) return;
    el.dataset.done = '1';
    var target = parseInt(el.dataset.count, 10), suffix = el.dataset.suffix || '', t0 = null;
    if (reduced || target === 0) { el.textContent = target + suffix; return; }
    requestAnimationFrame(function step(ts){
      if (!t0) t0 = ts;
      var k = Math.min((ts - t0) / 900, 1);
      el.textContent = Math.round(target * (1 - Math.pow(1 - k, 3))) + suffix;
      if (k < 1) requestAnimationFrame(step);
    });
  }

  // --- scroll: barra de XP, ciclo día/noche, parallax del terreno ---
  var xp = document.getElementById('xp'), orbit = document.getElementById('orbit'),
      root = document.documentElement, ticking = false;

  // amanece en el primer tercio y anochece en el último: el día vive en el centro
  function dayAt(p){
    var d = p < 0.35 ? p / 0.35 : (p > 0.78 ? 1 - (p - 0.78) / 0.22 : 1);
    return Math.max(0, Math.min(1, d));
  }

  // Leer scrollHeight justo después de escribir estilos obliga al navegador a recalcular todo el
  // layout de golpe (122 ms en móvil lento). Se mide aquí, fuera del scroll, con un ResizeObserver
  // que avisa cuando el layout ya está hecho y cuando cambia el alto de la página.
  var docMax = 0;
  new ResizeObserver(function(){ docMax = document.body.scrollHeight - innerHeight; onScroll(); })
    .observe(document.body);
  function onScroll(){
    var max = docMax;
    var p = max > 0 ? Math.min(scrollY / max, 1) : 0;
    // La barra de XP es un indicador de progreso, no un adorno: se actualiza siempre,
    // también con "movimiento reducido" activado.
    xp.style.setProperty('--p', (p * 100) + '%');
    // El ciclo día/noche es un cambio de color ligado a la posición, no un movimiento:
    // se aplica también con "movimiento reducido" para que nadie se pierda el efecto.
    var d = dayAt(p);
    root.style.setProperty('--day', d.toFixed(3));
    // El naranja del amanecer/atardecer se enciende a medio camino entre noche y día.
    root.style.setProperty('--dusk', (1 - Math.abs(2 * d - 1)).toFixed(3));
    // Sol y luna: una vuelta completa a lo largo de la página, sincronizada con el ciclo.
    // A 180° la luna está en lo alto (noche, arriba del todo); a 0° lo está el sol
    // (mediodía, mitad de la página); al final vuelve a 180° y anochece otra vez.
    // Va ligado a la posición del scroll, igual que el color del cielo, así que también
    // se aplica con "movimiento reducido": si no, el cielo cambiaba sin que el sol se moviera.
    orbit.style.setProperty('--orbit', (180 + p * 360).toFixed(1) + 'deg');
    if (reduced) drawClouds(0);
    ticking = false;
  }
  addEventListener('scroll', function(){
    if (!ticking) { ticking = true; requestAnimationFrame(onScroll); }
  }, { passive: true });
  onScroll();

  // --- paisaje del fondo con las texturas del juego ---
  // Junto al propio script, sea cual sea la página que lo cargue.
  var MC = new URL('mc/', script.src).href;
  var TEX = ['grass_side','dirt','stone','sand','gravel','oak_log','oak_leaves','short_grass','fern',
    'tall_grass_bottom','tall_grass_top','poppy','dandelion','cornflower','oxeye_daisy','azure_bluet','sugar_cane'];
  var img = {};
  function load(name){
    return new Promise(function(ok){
      var i = new Image(); i.onload = function(){ img[name] = i; ok(); }; i.onerror = ok; i.src = MC + name + '.webp';
    });
  }

  // Generador con semilla: el paisaje sale igual en cada visita y en cada idioma.
  function rng(seed){ return function(){ seed = (seed * 16807) % 2147483647; return (seed - 1) / 2147483646; }; }

  // --- nubes en 3D ---
  // Cámara en el suelo mirando al horizonte; el mapa de nubes es un techo a 128 bloques
  // por encima (el juego las pone a la altura 192 y el jugador anda cerca de la 64). Se
  // pinta de lejos a cerca: cara inferior al 70 % de luz, laterales al 80 % y la cara que
  // mira al jugador al 90 %, las sombras que pone el juego. Las celdas seguidas de una fila
  // van en un solo trozo y cada fila pisa un poco la siguiente, para que el suavizado del
  // canvas no deje juntas.
  var cloudCv = document.getElementById('clouds'), cloudG = cloudCv.getContext('2d'), cloudMap = null;
  var CELL = 12, CH = 128, CT = 4, ZFAR = 1400;
  function cloudAt(i, j){ return cloudMap[((j & 255) * 256 + (i & 255)) * 4 + 3] > 128; }
  function lerp(a, b, k){ return [a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k, a[2] + (b[2] - a[2]) * k]; }
  function css(c, k, fog){ var m = lerp(c, fog, k); return 'rgb(' + (m[0] | 0) + ',' + (m[1] | 0) + ',' + (m[2] | 0) + ')'; }
  function drawClouds(t){
    var w = innerWidth, h = innerHeight, dpr = Math.min(devicePixelRatio || 1, 1.5);
    if (!w || !h || !cloudMap) return;
    if (cloudCv.width !== Math.round(w * dpr) || cloudCv.height !== Math.round(h * dpr)) {
      cloudCv.width = Math.round(w * dpr); cloudCv.height = Math.round(h * dpr);
    }
    var g = cloudG; g.setTransform(dpr, 0, 0, dpr, 0, 0); g.clearRect(0, 0, w, h);
    var day = parseFloat(root.style.getPropertyValue('--day')) || 0;
    // Niebla del color del horizonte del cielo de cada momento; de noche, nubes grises.
    var fog = lerp([16, 30, 56], [36, 90, 138], day);
    var white = lerp([62, 70, 94], [255, 255, 255], day);
    var under = white.map(function(v){ return v * .7; }), side = white.map(function(v){ return v * .8; }), front = white.map(function(v){ return v * .9; });
    // El horizonte se esconde detrás de las colinas del fondo.
    var horizon = h - land.offsetHeight * .45, f = Math.max(h, w * .55) * .75, cx = w / 2;
    var off = (reduced ? 0 : t / 1000 * .8) / CELL, oi = Math.floor(off), of = off - oi;
    function P(x, y, z){ return [cx + f * x / z, horizon - f * y / z]; }
    function quad(a, b, c, d, col){ g.fillStyle = col; g.beginPath(); g.moveTo(a[0], a[1]); g.lineTo(b[0], b[1]); g.lineTo(c[0], c[1]); g.lineTo(d[0], d[1]); g.closePath(); g.fill(); }
    var yb = CH, yt = CH + CT;
    for (var j = Math.ceil(ZFAR / CELL); j >= 1; j--) {
      var z0 = j * CELL, z1 = z0 + CELL, zz = z1 + CELL * .08;
      var k = Math.min(1, Math.pow(z0 / ZFAR, .55));
      if (k > .97) continue;
      var cu = css(under, k, fog), cs = css(side, k, fog), cf = css(front, k, fog);
      var maxI = Math.ceil((w / 2) / f * z1 / CELL) + 2;
      for (var i = -maxI; i <= maxI; i++) {
        if (!cloudAt(i + oi, j + 37)) continue;
        var a = i; while (i + 1 <= maxI && cloudAt(i + 1 + oi, j + 37)) i++;
        var x0 = (a - of) * CELL, x1 = (i + 1 - of) * CELL;
        if (x0 > 0) quad(P(x0, yb, z0), P(x0, yt, z0), P(x0, yt, z1), P(x0, yb, z1), cs);
        if (x1 < 0) quad(P(x1, yb, z0), P(x1, yt, z0), P(x1, yt, z1), P(x1, yb, z1), cs);
        for (var q = a; q <= i; q++) {
          if (cloudAt(q + oi, j - 1 + 37)) continue;
          var r0 = q; while (q + 1 <= i && !cloudAt(q + 1 + oi, j - 1 + 37)) q++;
          var fx0 = (r0 - of) * CELL - .3, fx1 = (q + 1 - of) * CELL + .3;
          quad(P(fx0, yb, z0), P(fx1, yb, z0), P(fx1, yt, z0), P(fx0, yt, z0), cf);
        }
        quad(P(x0 - .3, yb, z0), P(x1 + .3, yb, z0), P(x1 + .3, yb, zz), P(x0 - .3, yb, zz), cu);
      }
    }
  }
  var clouds = new Image();
  clouds.onload = function(){
    var t = document.createElement('canvas'); t.width = t.height = 256;
    var tg = t.getContext('2d'); tg.drawImage(clouds, 0, 0);
    cloudMap = tg.getImageData(0, 0, 256, 256).data;
    drawClouds(performance.now());
    // Se mueven muy despacio: 15 fotogramas por segundo bastan. Con «reducir movimiento»
    // solo se redibujan al cambiar el cielo (ver onScroll) o el tamaño.
    var last = 0;
    if (!reduced) requestAnimationFrame(function tick(t){
      requestAnimationFrame(tick);
      if (document.hidden || t - last < 66) return;
      last = t; drawClouds(t);
    });
  };
  clouds.src = MC + 'clouds.webp';
  addEventListener('resize', function(){ drawClouds(performance.now()); });

  var land = document.getElementById('land'), lastW = 0;

  function drawLand(){
    var W = innerWidth, small = W < 700 || innerHeight < 700;
    // Con la pestaña oculta o en una vista previa el ancho puede ser 0: se espera al
    // siguiente resize en vez de fallar al dibujar un canvas vacío.
    if (!W) return;
    lastW = W;
    var B = small ? 32 : 48;                 // un bloque: 2 o 3 px por píxel de la textura
    // En pantallas pequeñas el paisaje se dibuja a 1x: el canvas ya se amplía con
    // image-rendering:pixelated, así que los bloques salen igual de nítidos, pero son hasta 4 veces
    // menos píxeles que rellenar en un móvil, que era la mayor parte del trabajo de arranque.
    var dpr = small ? 1 : Math.min(devicePixelRatio || 1, 2), r = rng(20261014);
    var FLOWERS = ['poppy','dandelion','cornflower','oxeye_daisy','azure_bluet'];

    // Tres capas: las de atrás con bloques más pequeños y neblina del color del cielo.
    var layers = [
      { b: Math.round(B * .5),  base: 6, spread: 3, fog: .55, trees: .05, front: false },
      { b: Math.round(B * .72), base: 3, spread: 2, fog: .3,  trees: .07, front: false },
      { b: B,                   base: 2, spread: 1, fog: 0,   trees: 0,   front: true }
    ];
    var parts = [], top = 0, overlays = [];
    layers.forEach(function(L){
      var cols = Math.ceil(W / L.b) + 1, h = L.base, cells = [];
      for (var x = 0; x < cols; x++) {
        var q = r();
        if (q < .2) h--; else if (q > .8) h++;
        h = Math.max(L.base - L.spread + (L.front ? 1 : 0), Math.min(L.base + L.spread, h));
        cells.push({ h: h });
      }
      // Charca con arena en las orillas y cañas de azúcar, solo en la capa de delante.
      var pond = null;
      if (L.front && cols >= 16) {
        var len = cols >= 30 ? 6 : 4, s = Math.floor(cols * .58);
        pond = { s: s, len: len };
        for (var p = s - 1; p <= s + len; p++) {
          cells[p].h = L.base;
          cells[p].sand = true;
        }
        for (p = s; p < s + len; p++) { cells[p].h = L.base - 1; cells[p].water = true; }
        cells[s + len].cane = 2 + Math.floor(r() * 2);
      }
      // Árboles: en la capa de delante solo uno, a la derecha, para no quedar detrás del texto.
      var trees = [];
      cells.forEach(function(c, x){
        if (c.water || c.sand || x < 2 || x > cols - 3) return;
        var want = L.front ? x === cols - 4 : r() < L.trees;
        if (want && !trees.some(function(t){ return Math.abs(t.x - x) < 5; })) trees.push({ x: x, h: c.h, t: 4 + Math.floor(r() * 2) });
      });
      cells.forEach(function(c, x){
        if (c.water || c.cane || trees.some(function(t){ return t.x === x; })) return;
        var q = r();
        if (q < .28) c.plant = 'short_grass';
        else if (q < .34) c.plant = 'fern';
        else if (q < .38) c.plant = 'tall';
        else if (q < .46) c.plant = FLOWERS[Math.floor(r() * FLOWERS.length)];
      });
      var layerTop = 0;
      cells.forEach(function(c){ layerTop = Math.max(layerTop, (c.h + 2) * L.b); });
      trees.forEach(function(t){ layerTop = Math.max(layerTop, (t.h + t.t + 2) * L.b); });
      top = Math.max(top, layerTop);
      parts.push({ L: L, cells: cells, trees: trees, pond: pond });
    });

    var H = top;
    var canvas = document.createElement('canvas');
    canvas.width = Math.round(W * dpr); canvas.height = Math.round(H * dpr);
    canvas.style.width = W + 'px'; canvas.style.height = H + 'px';
    var g = canvas.getContext('2d');

    parts.forEach(function(part){
      var L = part.L, b = L.b;
      var lc = document.createElement('canvas');
      lc.width = canvas.width; lc.height = canvas.height;
      var c = lc.getContext('2d');
      c.setTransform(dpr, 0, 0, dpr, 0, 0);
      c.imageSmoothingEnabled = false;
      function block(name, x, y){ if (img[name]) c.drawImage(img[name], x * b, H - (y + 1) * b, b, b); }
      part.cells.forEach(function(cell, x){
        for (var y = 0; y < cell.h; y++) {
          var name = y === cell.h - 1 ? (cell.sand ? 'sand' : (cell.water ? 'gravel' : 'grass_side'))
                   : (y >= cell.h - 3 ? (cell.sand || cell.water ? 'sand' : 'dirt') : 'stone');
          block(name, x, y);
        }
        if (cell.plant === 'tall') { block('tall_grass_bottom', x, cell.h); block('tall_grass_top', x, cell.h + 1); }
        else if (cell.plant) block(cell.plant, x, cell.h);
        if (cell.cane) for (var k = 0; k < cell.cane; k++) block('sugar_cane', x, cell.h + k);
      });
      // Roble como los del juego: tronco de 4 o 5 bloques y copa de dos capas anchas y dos
      // estrechas. Las hojas tienen huecos, así que se ve el tronco por detrás.
      part.trees.forEach(function(t){
        for (var y = 0; y < t.t; y++) block('oak_log', t.x, t.h + y);
        var tt = t.h + t.t;
        [[-2, 2, tt - 2], [-2, 2, tt - 1], [-1, 1, tt], [-1, 1, tt + 1]].forEach(function(row){
          for (var dx = row[0]; dx <= row[1]; dx++) {
            if (row[2] === tt + 1 && dx !== 0 && r() < .5) continue;
            block('oak_leaves', t.x + dx, row[2]);
          }
        });
        if (!reduced) for (var n = 0; n < (L.front ? 3 : 1); n++) {
          var lp = Math.max(3, Math.round(b * 5 / 16));
          var leaf = document.createElement('i');
          leaf.className = 'leaf';
          leaf.style.left = ((t.x - 2 + r() * 5) * b) + 'px';
          leaf.style.bottom = ((tt - 2 + r()) * b) + 'px';
          leaf.style.setProperty('--lp', lp + 'px');
          leaf.style.setProperty('--fall', ((tt - 2 - t.h + 1) * b) + 'px');
          leaf.style.backgroundImage = 'url(' + MC + 'leaf_particles.webp)';
          leaf.style.backgroundSize = (lp * 12) + 'px ' + lp + 'px';
          leaf.style.backgroundPosition = (-lp * Math.floor(r() * 12)) + 'px 0';
          leaf.style.animationDuration = (6 + r() * 5).toFixed(1) + 's';
          leaf.style.animationDelay = (-r() * 10).toFixed(1) + 's';
          if (L.fog) leaf.style.opacity = '.7';
          overlays.push(leaf);
        }
      });
      if (L.fog) {
        c.setTransform(1, 0, 0, 1, 0, 0);
        c.globalCompositeOperation = 'source-atop';
        c.fillStyle = 'rgba(42,92,140,' + L.fog + ')';      // el azul del horizonte de día
        c.fillRect(0, 0, lc.width, lc.height);
      }
      g.drawImage(lc, 0, 0);
      // El agua va aparte, encima del lecho de la charca, para animarla con CSS.
      if (part.pond) {
        var w = document.createElement('div');
        w.className = 'water';
        w.style.left = (part.pond.s * b) + 'px';
        w.style.width = (part.pond.len * b) + 'px';
        w.style.bottom = ((L.base - 1) * b) + 'px';
        w.style.height = Math.round(b * 14 / 16) + 'px';      // el agua del juego no llena el bloque
        w.style.setProperty('--b', b + 'px');
        w.style.backgroundImage = 'url(' + MC + 'water.webp)';
        w.style.backgroundSize = b + 'px ' + (b * 32) + 'px';
        w.style.opacity = '.85';
        overlays.push(w);
      }
    });

    land.textContent = '';
    land.style.height = H + 'px';
    land.appendChild(canvas);
    overlays.forEach(function(o){ land.appendChild(o); });
    // Alto del suelo de delante: el pie deja este hueco para que su texto quede sobre la hierba.
    root.style.setProperty('--ground', (layers[2].base * B) + 'px');
  }

  Promise.all(TEX.map(load)).then(function(){ idle(drawLand); });
  // En móvil la barra de direcciones cambia el alto al hacer scroll: solo se redibuja si
  // cambia el ancho, que es lo que altera el paisaje.
  var landTimer;
  addEventListener('resize', function(){
    if (innerWidth === lastW) return;
    clearTimeout(landTimer); landTimer = setTimeout(drawLand, 200);
  });

  // --- menú para pantallas estrechas: los mismos enlaces de la barra ---
  var menu = document.getElementById('menu'), menuBtn = document.getElementById('menuBtn');
  [].forEach.call(document.querySelectorAll('#sections a'), function(a){ menu.appendChild(a.cloneNode(true)); });
  function setMenu(open){ menu.hidden = !open; menuBtn.setAttribute('aria-expanded', open); }
  menuBtn.addEventListener('click', function(e){ e.stopPropagation(); setMenu(menu.hidden); });
  menu.addEventListener('click', function(e){ if (e.target.closest('a')) setMenu(false); });
  document.addEventListener('click', function(e){ if (!menu.hidden && !menu.contains(e.target)) setMenu(false); });
  document.addEventListener('keydown', function(e){ if (e.key === 'Escape') setMenu(false); });

  // --- selector de idioma: el botón enseña el idioma marcado y abre la lista ---
  var langBtn = document.getElementById('langBtn'), langList = document.getElementById('langList');
  var cur = langList.querySelector('a.on');
  if (cur) {
    var flag = cur.querySelector('svg').cloneNode(true); flag.setAttribute('class', 'flag');
    var cs = document.getElementById('langCur');
    cs.parentNode.insertBefore(flag, cs);
    cs.textContent = cur.getAttribute('hreflang').split('-')[0].toUpperCase();
    // El nombre accesible tiene que empezar por el texto visible (WCAG 2.5.3).
    langBtn.setAttribute('aria-label', cs.textContent + ' · ' + langBtn.getAttribute('aria-label'));
  }
  function setLangs(open){ langList.hidden = !open; langBtn.setAttribute('aria-expanded', open); }
  langBtn.addEventListener('click', function(e){ e.stopPropagation(); setLangs(langList.hidden); });
  document.addEventListener('click', function(e){ if (!langList.hidden && !langList.contains(e.target)) setLangs(false); });
  document.addEventListener('keydown', function(e){ if (e.key === 'Escape') setLangs(false); });

  // --- la barra se compacta solo si no cabe (ver .tight y .compact en el CSS) ---
  var navEl = document.querySelector('.nav'), navWrap = navEl.querySelector('.wrap');
  function fitNav(){
    navEl.classList.remove('tight', 'compact');
    if (navWrap.scrollWidth > navWrap.clientWidth) navEl.classList.add('tight');
    if (navWrap.scrollWidth > navWrap.clientWidth) navEl.classList.add('compact');
  }
  fitNav();
  addEventListener('resize', fitNav);
  if (document.fonts) document.fonts.ready.then(fitNav);

  // --- el botón fijo de descarga del móvil se aparta al llegar a las descargas ---
  var dock = document.getElementById('dock');
  new IntersectionObserver(function(entries){
    dock.classList.toggle('away', entries[0].isIntersecting);
  }, { rootMargin: '0px 0px -30% 0px' }).observe(document.getElementById('descargas'));

  // --- versiones y descargas reales, desde la API de GitHub ---
  // Sin autenticar son 60 peticiones/hora por IP; si falla (límite, sin red, repo privado)
  // los enlaces se quedan como están, apuntando a la página de releases. Nunca rompe la página.
  var REPO = 'JuanP-G/MC-ServerLauncher';
  var MATCH = {
    win:   function(n){ return /\.exe$/i.test(n); },
    linux: function(n){ return /\.AppImage$/i.test(n); },
    mac:   function(n){ return /AppleSilicon.*\.dmg$/i.test(n) || /\.dmg$/i.test(n); }
  };

  fetch('https://api.github.com/repos/' + REPO + '/releases?per_page=8')
    .then(function(r){ if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
    .then(function(releases){
      if (!Array.isArray(releases) || !releases.length) throw new Error('sin versiones');
      var published = releases.filter(function(r){ return !r.draft; });
      var latest = published.find(function(r){ return !r.prerelease; }) || published[0];
      if (latest) document.getElementById('footVer').textContent = 'MC-ServerLauncher ' + latest.tag_name.replace(/^v/, '');

      // Solo las tarjetas por sistema operativo descargan el archivo: los botones
      // generales (nav, hero y cierre) llevan a esta sección para que el usuario elija
      // el suyo, en vez de bajarle el de Windows sin preguntar.
      if (latest && latest.assets) {
        Object.keys(MATCH).forEach(function(os){
          var asset = latest.assets.find(function(a){ return MATCH[os](a.name); });
          var btn = document.querySelector('[data-dl="' + os + '"]');
          if (asset && btn) {
            btn.href = asset.browser_download_url;
            btn.setAttribute('download', '');
            btn.title = asset.name + ' · ' + (asset.size / 1048576).toFixed(0) + ' MB';
          }
        });
      }

      // Listado de versiones publicadas.
      var list = document.getElementById('releaseList');
      if (!list) return;
      list.innerHTML = '';
      published.slice(0, 4).forEach(function(rel, i){
        var li = document.createElement('li');
        if (rel === latest) li.className = 'latest';
        var tag = document.createElement('span');
        tag.className = 'rel-tag'; tag.textContent = rel.tag_name;
        var name = document.createElement('a');
        name.className = 'rel-name';
        // La dirección viene de la respuesta de la API: solo se sigue si es de GitHub y por https.
        name.href = /^https:\/\/github\.com\//.test(rel.html_url)
          ? rel.html_url : 'https://github.com/' + REPO + '/releases';
        // Muchos títulos repiten el número de versión al principio; nos quedamos con la parte útil.
        name.textContent = (rel.name || rel.tag_name).replace(/^MC Server Launcher\s*[\d.]*\s*[—-]\s*/, '');
        var date = document.createElement('span');
        date.className = 'rel-date';
        date.textContent = new Date(rel.published_at).toLocaleDateString(cfg.locale,
          { day: 'numeric', month: 'short', year: 'numeric' });
        li.appendChild(tag); li.appendChild(name); li.appendChild(date);
        if (rel === latest) {
          var b = document.createElement('span');
          b.className = 'rel-badge'; b.textContent = cfg.latest;
          li.appendChild(b);
        }
        list.appendChild(li);
      });
    })
    .catch(function(){
      var list = document.getElementById('releaseList');
      if (!list) return;
      var li = document.createElement('li'), more = document.createElement('a');
      li.className = 'release-empty';
      more.href = 'https://github.com/' + REPO + '/releases';
      more.textContent = cfg.loadErrorLink;
      li.append(cfg.loadError + ' ', more, '.');
      list.replaceChildren(li);
    });

  // --- la captura se inclina siguiendo el ratón ---
  var shot = document.getElementById('shot');
  if (shot && !reduced && matchMedia('(pointer:fine)').matches) {
    addEventListener('mousemove', function(e){
      var k = (e.clientX / innerWidth - 0.5);
      var j = (e.clientY / innerHeight - 0.5);
      shot.style.setProperty('--ry', (-5 + k * 8).toFixed(2) + 'deg');
      shot.style.setProperty('--rx', (3 - j * 5).toFixed(2) + 'deg');
    }, { passive: true });
  }
})();
