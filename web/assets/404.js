/* Página de error 404 (web/404.html). Sin dependencias, sin cookies, sin almacenamiento y sin
   peticiones de red: solo elige el idioma, escribe la dirección que falló y maneja el selector
   de idioma. */
(function () {
  'use strict';
  var root = document.documentElement;
  var LANGS = ['es', 'en', 'de', 'fr', 'pt'];

  /* Dónde vive el sitio: la carpeta que contiene assets/. Es "/" en la raíz de Vercel y
     "/MC-ServerLauncher/" en GitHub Pages; la 404 se sirve desde cualquier dirección, así que
     no puede saberlo mirando la suya. */
  var base = '/';
  try { base = new URL('../', document.currentScript.src).pathname; } catch (e) { /* se queda en "/" */ }

  /* El idioma sale de la dirección, igual que en el resto de la web: /en/, /de/, /fr/ o /pt/ al
     principio; lo demás (la raíz, /guias/...) es español. */
  var path = location.pathname;
  var rel = path.indexOf(base) === 0 ? path.slice(base.length) : path.replace(/^\//, '');
  var first = rel.split('/')[0].toLowerCase();
  var lang = LANGS.indexOf(first) > 0 ? first : 'es';

  function apply(l) {
    root.setAttribute('data-l', l);
    root.lang = l === 'pt' ? 'pt-BR' : l;
  }
  apply(lang);          // antes del primer pintado: no hay destello en español

  document.addEventListener('DOMContentLoaded', function () {
    var brand = document.getElementById('brand');
    var langs = document.getElementById('langs');
    var btn = document.getElementById('langBtn');
    var list = document.getElementById('langList');
    var cur = document.getElementById('langCur');

    /* La dirección pedida, tal cual la escribió quien llegó, sin la consulta. textContent: nunca
       se interpreta como HTML aunque la dirección traiga etiquetas. Recortada si es kilométrica.
       La línea está oculta en el HTML y solo se muestra cuando ya tiene algo que decir. */
    var shown = path;
    try { shown = decodeURIComponent(path); } catch (e) { /* secuencia % mal formada: se muestra cruda */ }
    if (shown.length > 90) { shown = shown.slice(0, 89) + '…'; }
    Array.prototype.forEach.call(document.querySelectorAll('.path'), function (el) {
      el.textContent = shown;
      if (el.parentNode) { el.parentNode.hidden = false; }
    });

    /* Idioma de la página: el texto, el título, la etiqueta del botón y adónde lleva el logo. */
    function show(l) {
      apply(l);
      var block = document.querySelector('.msg [data-l="' + l + '"]');
      if (block) {
        document.title = block.getAttribute('data-title');
        btn.setAttribute('aria-label', block.getAttribute('data-lang-label'));
        var home = block.querySelector('.btn-primary');
        if (home) { brand.setAttribute('href', home.getAttribute('href')); }
      }
      cur.textContent = l.toUpperCase();
      Array.prototype.forEach.call(list.querySelectorAll('button'), function (b) {
        if (b.getAttribute('data-set') === l) { b.setAttribute('aria-current', 'true'); }
        else { b.removeAttribute('aria-current'); }
      });
    }
    show(lang);

    /* Selector: cambia el idioma de ESTA página (no lleva a otra dirección). */
    langs.hidden = false;
    function open(o) { list.hidden = !o; btn.setAttribute('aria-expanded', o ? 'true' : 'false'); }
    btn.addEventListener('click', function (e) { e.stopPropagation(); open(list.hidden); });
    list.addEventListener('click', function (e) {
      var b = e.target.closest ? e.target.closest('button[data-set]') : null;
      if (!b) { return; }
      show(b.getAttribute('data-set'));
      open(false);
      btn.focus();
    });
    document.addEventListener('click', function (e) { if (!list.hidden && !list.contains(e.target)) { open(false); } });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && !list.hidden) { open(false); btn.focus(); } });
  });
})();
