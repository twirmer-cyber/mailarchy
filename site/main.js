// MailArchy-Seite: Sprachwahl, Fallblatt-Überschriften, Anzeigetafel mit Suchvorführung (erfundene Demo-Daten).
// Ohne JavaScript bleibt alles lesbar und der Download-Knopf funktioniert; Bewegung entfällt bei „Bewegung reduzieren“.
(function () {
  'use strict';
  var root = document.documentElement;
  var I18N = window.MV_I18N || {};
  var reduce = !!(window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches);
  var TITLE_DE = 'MailArchy – Ihr E-Mail-Archiv für Windows';
  var DE = { board_pause: 'Vorführung anhalten', board_play: 'Vorführung fortsetzen' };

  // ------------------------------------------------------------ Demo-Daten (frei erfunden)
  var DEMO_DE = {
    queries: [
      { q: 'rechnung jahr:2024', words: ['rechnung'], year: 2024 },
      { q: 'von:schule elternabend', from: 'schule', words: ['elternabend'] },
      { q: 'anhang:ja versicherung', att: true, words: ['versicherung'] },
      { q: '"pflaumenkuchen"', words: ['pflaumenkuchen'] }
    ],
    mails: [
      { from: 'Stadtwerke Lindenau', subject: 'Ihre Jahresabrechnung 2024 – Strom und Gas', date: '2024-11-16', att: true, text: 'rechnung abschlag strom gas' },
      { from: 'Mobilfunk Nord', subject: 'Ihre Rechnung für Oktober 2024', date: '2024-11-07', att: true, text: 'rechnung' },
      { from: 'Mobilfunk Nord', subject: 'Ihre Rechnung für September 2024', date: '2024-10-07', att: true, text: 'rechnung' },
      { from: 'Mobilfunk Nord', subject: 'Ihre Rechnung für Dezember 2023', date: '2023-12-06', att: true, text: 'rechnung' },
      { from: 'Grundschule am Park', subject: 'Einladung zum Elternabend der Klasse 3b', date: '2024-09-12', att: false, text: 'elternabend schule' },
      { from: 'Grundschule am Park', subject: 'Elternabend: Protokoll und Termine', date: '2024-09-20', att: true, text: 'elternabend protokoll' },
      { from: 'Kfz-Versicherung Westfalia', subject: 'Ihr Versicherungsschein – Beitragsanpassung', date: '2024-10-28', att: true, text: 'versicherung versicherungsschein' },
      { from: 'Kfz-Versicherung Westfalia', subject: 'Schadenmeldung erhalten', date: '2022-06-14', att: false, text: 'versicherung schaden' },
      { from: 'Oma Hilde', subject: 'Rezept Pflaumenkuchen', date: '2021-08-29', att: false, text: 'pflaumenkuchen rezept' },
      { from: 'Thomas Berger', subject: 'Garantie Waschmaschine', date: '2022-05-03', att: true, text: 'rechnung garantie' }
    ]
  };
  function demoData(lang) { return (I18N[lang] && I18N[lang].demo) || DEMO_DE; }
  function t(key) {
    var d = I18N[root.lang];
    return (d && d.t && d.t[key] != null) ? d.t[key] : DE[key];
  }

  // ------------------------------------------------------------ Ein Takt für alle Klappen
  // Alles, was klappt, hängt an derselben Uhr; so bleibt die Bewegung ruhig und gleichmäßig.
  var clock = (function () {
    var jobs = [], timer = null;
    function tick() {
      jobs = jobs.filter(function (j) { return j() !== false; });
      if (!jobs.length) { clearInterval(timer); timer = null; }
    }
    return {
      add: function (job) { jobs.push(job); if (!timer) timer = setInterval(tick, 55); },
      clear: function (owner) { jobs = jobs.filter(function (j) { return j.owner !== owner; }); }
    };
  })();

  function flip(el) {
    // Zwei gleiche Animationen im Wechsel starten die Klappe neu, ohne das Layout zu erzwingen
    if (el.classList.contains('fl')) { el.classList.remove('fl'); el.classList.add('fl2'); }
    else { el.classList.remove('fl2'); el.classList.add('fl'); }
  }

  // ------------------------------------------------------------ Fallblatt-Überschriften
  var UP = 'ABCDEFGHIJKLMNOPRSTUVWZ', LOW = 'abcdefghiklmnoprstuvwz', NUM = '0123456789';
  function randFor(ch) {
    var set = /[a-zà-ÿ]/.test(ch) ? LOW : /[A-ZÀ-Þ]/.test(ch) ? UP : NUM;
    return set.charAt(Math.floor(Math.random() * set.length));
  }
  function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  function buildFlaps(h, animate) {
    var lines = [];
    var parts = h.querySelectorAll('.zeile');
    if (parts.length) parts.forEach(function (p) { lines.push(p.textContent.trim()); });
    else lines.push(h.textContent.trim());
    var full = lines.join(' ');
    var html = '<span class="sr">' + esc(full) + '</span><span class="klappen" aria-hidden="true">';
    lines.forEach(function (line) {
      html += '<span class="kz">';
      line.split(/\s+/).forEach(function (w) {
        html += '<span class="wort">';
        Array.from(w).forEach(function (c) { html += '<span class="k" data-c="' + esc(c) + '">' + esc(c) + '</span>'; });
        html += '</span>';
      });
      html += '</span>';
    });
    h.innerHTML = html + '</span>';
    h.classList.add('gebaut');
    var tiles = Array.prototype.slice.call(h.querySelectorAll('.k'));
    // Breite jeder Klappe auf ihr Zielzeichen festlegen (in em), damit nichts springt, wenn die Zeichen wechseln
    tiles.forEach(function (k) {
      var fs = parseFloat(getComputedStyle(k).fontSize) || 16;
      k.style.setProperty('width', (k.getBoundingClientRect().width / fs).toFixed(3) + 'em');
    });
    if (animate && !reduce) run(h, tiles);
  }

  function run(owner, tiles) {
    clock.clear(owner);
    var state = tiles.map(function (k, i) { return { k: k, n: 1 + Math.floor(i * 0.3) + Math.floor(Math.random() * 2) }; });
    state.forEach(function (s) { if (/\S/.test(s.k.dataset.c) && !/[\s.,!?–-]/.test(s.k.dataset.c)) { s.k.textContent = randFor(s.k.dataset.c); s.k.classList.add('roll'); } else s.n = 0; });
    var job = function () {
      var busy = false;
      state.forEach(function (s) {
        if (s.n <= 0) return;
        s.n--;
        s.k.textContent = s.n === 0 ? s.k.dataset.c : randFor(s.k.dataset.c);
        if (s.n === 0) s.k.classList.remove('roll');
        flip(s.k);
        busy = true;
      });
      return busy;
    };
    job.owner = owner;
    clock.add(job);
  }

  var heroTitle = document.getElementById('titel');
  var ctaTitle = document.getElementById('schluss-titel');
  var ctaSeen = false;

  function refreshTitles(animateHero) {
    if (heroTitle) buildFlaps(heroTitle, animateHero);
    if (ctaTitle) buildFlaps(ctaTitle, false);
  }

  if (ctaTitle && 'IntersectionObserver' in window && !reduce) {
    new IntersectionObserver(function (es, obs) {
      if (es.some(function (e) { return e.isIntersecting; }) && !ctaSeen) {
        ctaSeen = true; obs.disconnect();
        run(ctaTitle, Array.prototype.slice.call(ctaTitle.querySelectorAll('.k')));
      }
    }, { threshold: 0.6 }).observe(ctaTitle);
  }

  // ------------------------------------------------------------ Sprache
  var originals = null;
  function rememberOriginals() {
    originals = new Map();
    document.querySelectorAll('[data-i18n],[data-i18n-html]').forEach(function (el) { originals.set(el, el.innerHTML); });
    document.querySelectorAll('[data-i18n-attr]').forEach(function (el) {
      el.getAttribute('data-i18n-attr').split(';').forEach(function (pair) {
        var a = pair.split(':')[0];
        el.dataset['orig' + a.replace(/[^a-z]/gi, '')] = el.getAttribute(a) || '';
      });
    });
  }
  function textOf(html) { var d = document.createElement('div'); d.innerHTML = html; return d.textContent; }

  function applyLang(lang, animate) {
    if (!originals) rememberOriginals();
    var dict = lang === 'de' ? null : (I18N[lang] || null);
    if (!dict) lang = 'de';
    root.lang = lang;
    root.dataset.lang = lang;
    document.querySelectorAll('[data-i18n]').forEach(function (el) {
      var k = el.getAttribute('data-i18n');
      el.textContent = dict && dict.t[k] != null ? dict.t[k] : textOf(originals.get(el));
    });
    document.querySelectorAll('[data-i18n-html]').forEach(function (el) {
      var k = el.getAttribute('data-i18n-html');
      el.innerHTML = dict && dict.t[k] != null ? dict.t[k] : originals.get(el);
    });
    document.querySelectorAll('[data-i18n-attr]').forEach(function (el) {
      el.getAttribute('data-i18n-attr').split(';').forEach(function (pair) {
        var p = pair.split(':'), a = p[0], k = p[1];
        el.setAttribute(a, dict && dict.t[k] != null ? dict.t[k] : el.dataset['orig' + a.replace(/[^a-z]/gi, '')]);
      });
    });
    document.title = dict && dict.title ? dict.title : TITLE_DE;
    var note = document.querySelector('.recht-hinweis');
    if (note) note.hidden = lang === 'de';
    document.querySelectorAll('.sprachen button').forEach(function (b) {
      var on = b.dataset.lang === lang;
      b.setAttribute('aria-pressed', String(on));
    });
    refreshTitles(animate);
    setShots(lang);
    board.language(lang);
  }

  // Bildschirmfotos je Sprache (fehlt eines, bleibt das deutsche)
  function setShots(lang) {
    var img = document.querySelector('[data-shot]'), dark = document.querySelector('[data-shot-dark]');
    if (!img) return;
    var want = 'bilder/app-light-' + lang + '.png';
    if (img.getAttribute('src') === want) return;
    var probe = new Image();
    probe.onload = function () { img.src = want; if (dark) dark.srcset = 'bilder/app-dark-' + lang + '.png'; };
    probe.onerror = function () { img.src = 'bilder/app-light-de.png'; if (dark) dark.srcset = 'bilder/app-dark-de.png'; };
    probe.src = want;
  }

  document.querySelectorAll('.sprachen button').forEach(function (b) {
    b.addEventListener('click', function () {
      if (b.dataset.lang === root.lang) return;
      try { localStorage.setItem('mailvault.lang', b.dataset.lang); } catch (e) {}
      if (!reduce) flip(b);
      applyLang(b.dataset.lang, true);
    });
  });

  // ------------------------------------------------------------ Anzeigetafel: Suchvorführung
  var board = (function () {
    var input = document.getElementById('anzeige-eingabe');
    var list = document.getElementById('anzeige-liste');
    var count = document.getElementById('anzeige-zahl');
    var pauseBtn = document.getElementById('anzeige-pause');
    var pauseText = document.getElementById('anzeige-pause-text');
    if (!input || !list) return { language: function () {} };
    var rows = Array.prototype.slice.call(list.children);
    var data = DEMO_DE, qi = 0, timer = null, visible = false, paused = false, running = false;
    var OWNER = {};

    function matches(m, q) {
      var hay = (m.subject + ' ' + m.text + ' ' + m.from).toLowerCase();
      if (q.year && m.date.slice(0, 4) !== String(q.year)) return false;
      if (q.att && !m.att) return false;
      if (q.from && m.from.toLowerCase().indexOf(q.from) < 0) return false;
      return (q.words || []).every(function (w) { return hay.indexOf(w) >= 0; });
    }
    function mark(s, words) {
      var out = esc(s);
      (words || []).forEach(function (w) {
        out = out.replace(new RegExp('(' + esc(w).replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + ')', 'ig'), '<mark>$1</mark>');
      });
      return out;
    }
    function fmt(d) {
      var p = d.split('-');
      return root.lang === 'de' ? p[2] + '.' + p[1] + '.' + p[0] : p[2] + '/' + p[1] + '/' + p[0];
    }
    function hitsFor(q) {
      return data.mails.filter(function (m) { return matches(m, q); }).sort(function (a, b) { return a.date < b.date ? 1 : -1; });
    }
    function fillRow(li, m, q) {
      var cells = li.children;
      if (!m) {
        li.className = 'leer';
        cells[0].textContent = ''; cells[1].textContent = ''; cells[2].textContent = '';
        return;
      }
      li.className = '';
      cells[0].textContent = fmt(m.date);
      cells[1].textContent = m.from;
      cells[1].title = m.from;
      cells[2].innerHTML = (m.att ? '<svg aria-hidden="true"><use href="#i-clip"/></svg>' : '') + mark(m.subject, q.words);
    }
    function showStatic(q) {
      input.textContent = q.q;
      var hits = hitsFor(q);
      rows.forEach(function (li, i) { fillRow(li, hits[i], q); });
      count.textContent = String(hits.length);
    }

    // Genau eine Zeile bewegt sich: erst tippt das Suchfeld, dann klappen die Zeilen nacheinander um.
    function play(q, done) {
      var hits = hitsFor(q), i = 0, r = 0, wait = 0;
      input.textContent = '';
      var job = function () {
        if (paused) return true;
        if (i < q.q.length) { input.textContent = q.q.slice(0, ++i); return true; }
        if (wait < 4) { wait++; return true; }
        if (r < rows.length) {
          var li = rows[r];
          if (r === 0) count.textContent = String(hits.length);
          fillRow(li, hits[r], q);
          Array.prototype.forEach.call(li.children, flip);
          r++;
          wait = 2; // kleine Pause zwischen den Zeilen
          return true;
        }
        done();
        return false;
      };
      job.owner = OWNER;
      clock.add(job);
    }
    function cycle() {
      if (!running) return;
      var q = data.queries[qi % data.queries.length];
      qi++;
      play(q, function () { timer = setTimeout(next, 5200); });
    }
    function next() { if (!paused && visible) cycle(); else timer = null; }
    function stop() { running = false; clearTimeout(timer); timer = null; clock.clear(OWNER); }
    function start() {
      if (reduce || paused || !visible || running) return;
      running = true;
      timer = setTimeout(cycle, 2600);
    }

    function setPauseLabel() {
      if (pauseText) pauseText.textContent = paused ? t('board_play') : t('board_pause');
      if (pauseBtn) { pauseBtn.setAttribute('aria-pressed', String(paused)); pauseBtn.title = pauseText ? pauseText.textContent : ''; }
    }
    if (pauseBtn && !reduce) {
      pauseBtn.hidden = false;
      pauseBtn.addEventListener('click', function () {
        paused = !paused;
        setPauseLabel();
        if (paused) stop(); else start();
      });
    }

    if ('IntersectionObserver' in window && !reduce) {
      new IntersectionObserver(function (es) {
        visible = es.some(function (e) { return e.isIntersecting; });
        if (visible) start(); else stop();
      }).observe(list);
    }
    document.addEventListener('visibilitychange', function () { if (document.hidden) stop(); else start(); });

    return {
      language: function (lang) {
        stop();
        data = demoData(lang);
        qi = 1;
        showStatic(data.queries[0]);
        setPauseLabel();
        start();
      }
    };
  })();

  // Schriften abwarten, damit die Klappenbreiten stimmen
  var first = function () { applyLang(root.dataset.lang || 'de', true); };
  if (document.fonts && document.fonts.ready) {
    var done = false;
    document.fonts.ready.then(function () { if (!done) { done = true; first(); } });
    setTimeout(function () { if (!done) { done = true; first(); } }, 1200);
  } else first();
})();
