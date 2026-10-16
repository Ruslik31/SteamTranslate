// Скрипт, который SteamTranslate.exe встраивает в страницы Steam (магазин, сообщество).
// Рисует кнопку «Перевести» и переводит страницу через Google Translate.
//
// Как переводит (так же, как расширение «Translate Web Pages»): текст собирается не кусочками,
// а АБЗАЦАМИ. Все текстовые узлы одного блока уходят в Google одним HTML-фрагментом с метками
// <a i=0>…</a><a i=1>…</a>, поэтому Google видит предложение целиком (даже если оно разбито
// тегами <b>, <a>), а потом расставляет перевод обратно по меткам.
// Сам в сеть скрипт не ходит: запросы делает хост через привязку window.sbTranslate.
(() => {
  'use strict';
  if (window.top !== window) return;
  if (window.__sbTranslateLoaded) return;
  const cfg = window.__SB_TR_CFG || { tl: 'ru', all: false };
  if (!/^https?:$/.test(location.protocol)) return;
  if (!cfg.all && !/(^|\.)(steampowered\.com|steamcommunity\.com)$/.test(location.hostname)) return;
  window.__sbTranslateLoaded = true;

  const SKIP = 'script,style,noscript,textarea,input,select,option,code,pre,svg,[translate="no"],.notranslate,[contenteditable=""],[contenteditable="true"]';
  const cyrTarget = () => /^(ru|uk|bg|be|sr|mk|kk)/.test(cfg.tl); // cfg.tl хост может поменять на лету
  const CHUNK_CHARS = 6000, CHUNK_PIECES = 30, CONCURRENCY = 3;

  const state = { on: false, busy: false, gen: 0 };
  const done = new Map(); // текстовый узел -> { orig, tr }
  const cache = new Map(); // "язык|исходный HTML" -> ответ Google
  const calls = new Map(); // id -> { res, rej }
  let seq = 0;

  const lsGet = (k) => { try { return localStorage.getItem(k); } catch (e) { return null; } };
  const lsSet = (k, v) => { try { localStorage.setItem(k, v); } catch (e) { /* ignore */ } };

  // ---------- связь с хостом ----------
  window.__sbTrDone = (id, ok, data) => {
    const c = calls.get(id);
    if (!c) return;
    calls.delete(id);
    ok ? c.res(data) : c.rej(new Error(String(data)));
  };

  function callHost(q) {
    if (typeof window.sbTranslate !== 'function') return Promise.reject(new Error('нет связи с SteamTranslate'));
    return new Promise((res, rej) => {
      const id = ++seq;
      calls.set(id, { res, rej });
      setTimeout(() => {
        if (calls.delete(id)) rej(new Error('timeout'));
      }, 45000);
      window.sbTranslate(JSON.stringify({ id, tl: cfg.tl, q }));
    });
  }

  // ---------- HTML-обмен с Google ----------
  const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  const ENT = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };
  const unesc = (s) =>
    s.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (m, e) => {
      if (e[0] === '#') {
        try {
          const c = e[1] === 'x' || e[1] === 'X' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
          return c ? String.fromCodePoint(c) : m;
        } catch (err) { return m; }
      }
      const v = ENT[e.toLowerCase()];
      return v === undefined ? m : v;
    });

  function pieceSource(p) {
    const parts = p.items.map((it) => (it.lead ? ' ' : '') + it.core + (it.trail || it.vt ? ' ' : ''));
    const inner = parts.length === 1 ? esc(parts[0]) : parts.map((t, i) => '<a i=' + i + '>' + esc(t) + '</a>').join('');
    return '<pre>' + inner + '</pre>';
  }

  // Разбор ответа Google: убираем <pre>, оригинал предложений (<i>) и раскладываем текст по меткам <a i=N>.
  // Возвращает массив переводов по номерам меток или null, если метки потеряны.
  function parseResult(result, n) {
    if (result.indexOf('<pre') !== -1) {
      result = result.replace('</pre>', '');
      result = result.slice(result.indexOf('>') + 1);
    }
    const sentences = [];
    let idx = 0;
    for (;;) {
      const s = result.indexOf('<b>', idx);
      if (s === -1) break;
      const e = result.indexOf('<i>', s);
      if (e === -1) { sentences.push(result.slice(s + 3)); break; }
      sentences.push(result.slice(s + 3, e));
      idx = e;
    }
    if (sentences.length) result = sentences.join(' ');
    result = result.replace(/<\/b>/g, '');
    if (n === 1) return [unesc(result.replace(/<\/?a(\s[^>]*)?>/g, ''))];

    // Текст вне меток достаётся следующей метке, но знаки препинания в его начале ("слово </a> .") принадлежат предыдущей.
    const outside = (text, segs) => {
      text = text.replace(/<\/a>/g, '');
      const p = /^\s*([.,;:!?…)»\]]+)/.exec(text);
      if (p && segs.length) {
        segs[segs.length - 1].t += p[1];
        text = text.slice(p[0].length);
      }
      return text;
    };
    const re = /<a\s+i=(\d+)>([^<]*)/g;
    const segs = [];
    let last = 0, m;
    while ((m = re.exec(result))) {
      segs.push({ i: +m[1], t: outside(result.slice(last, m.index), segs) + m[2] });
      last = m.index + m[0].length;
    }
    if (!segs.length) return null;
    const tail = outside(result.slice(last), segs); // отдельной строкой: outside() может дописать знак в последнюю метку
    segs[segs.length - 1].t += tail;
    const fin = [];
    for (const s of segs) fin[s.i] = fin[s.i] === undefined ? s.t : fin[s.i] + ' ' + s.t;
    return fin.map((t) => (t === undefined ? undefined : unesc(t)));
  }

  // ---------- сбор абзацев ----------
  function eligible(s) {
    if (s.length < 2) return false;
    const letters = s.match(/\p{L}/gu);
    if (!letters) return false;
    if (cyrTarget()) {
      const cyr = (s.match(/[Ѐ-ӿ]/g) || []).length;
      if (cyr / letters.length > 0.5) return false;
    }
    return true;
  }

  function makeBlockOf() {
    const cache = new Map();
    return (el) => {
      for (let e = el; e && e !== document.documentElement; e = e.parentElement) {
        let b = cache.get(e);
        if (b === undefined) {
          const d = getComputedStyle(e).display;
          b = d !== 'inline' && d !== 'contents';
          cache.set(e, b);
        }
        if (b) return e;
      }
      return document.body;
    };
  }

  // Абзац = подряд идущие текстовые узлы одного блочного элемента (разрыв по <br> и вложенным блокам).
  function collectPieces(roots) {
    const blockOf = makeBlockOf();
    const seen = new Set();
    const pieces = [];
    for (let root of roots) {
      if (!root || !root.isConnected) continue;
      if (root.nodeType === 3) {
        if (!root.parentElement || root.parentElement.closest(SKIP)) continue;
        root = blockOf(root.parentElement);
      }
      if (root.nodeType !== 1 || root.closest(SKIP)) continue;
      const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
        acceptNode: (n) => (n.nodeType === 1 && n.matches(SKIP) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT),
      });
      let cur = null;
      for (let n = w.nextNode(); n; n = w.nextNode()) {
        if (n.nodeType === 1) {
          if (n.nodeName === 'BR') cur = null;
          continue;
        }
        const p = n.parentElement;
        if (!p) continue;
        if (seen.has(n)) { cur = null; continue; }
        const raw = n.nodeValue;
        if (!/\S/.test(raw)) {
          // пробел между строчными тегами: Google должен знать, что слова разделены
          if (cur && cur.items.length && blockOf(p) === cur.block) cur.items[cur.items.length - 1].vt = true;
          continue;
        }
        seen.add(n);
        const blk = blockOf(p);
        if (!cur || cur.block !== blk) {
          cur = { block: blk, items: [] };
          pieces.push(cur);
        }
        let orig = raw, isDone = false;
        const rec = done.get(n);
        if (rec) {
          if (n.nodeValue === rec.tr) { orig = rec.orig; isDone = true; } else done.delete(n);
        }
        const m = /^(\s*)([\s\S]*?)(\s*)$/.exec(orig);
        const core = m[2].replace(/\s+/g, ' ');
        cur.items.push({ node: n, orig, lead: m[1], trail: m[3], core, vt: false, todo: !isDone && eligible(core) });
      }
    }
    return pieces.filter((p) => p.items.some((it) => it.todo));
  }

  // ---------- перевод ----------
  function applyPiece(p, fin) {
    p.items.forEach((it, j) => {
      if (!it.todo) return;
      const t = fin[j];
      if (typeof t !== 'string' || !t.trim()) return; // Google слил слот с соседним: оставляем оригинал
      if (!it.node.isConnected || it.node.nodeValue !== it.orig) return;
      const val = (it.lead || (/^\s/.test(t) ? ' ' : '')) + t.trim().replace(/\s+/g, ' ') + (it.trail || (/\s$/.test(t) ? ' ' : ''));
      done.set(it.node, { orig: it.orig, tr: val });
      it.node.nodeValue = val;
    });
  }

  async function translatePieces(pieces, onProgress, isRetry) {
    const gen = state.gen;
    const key = (src) => cfg.tl + '|' + src;
    const bySrc = new Map();
    for (const p of pieces) {
      p.src = pieceSource(p);
      if (!bySrc.has(p.src)) bySrc.set(p.src, []);
      bySrc.get(p.src).push(p);
    }
    const retry = [];
    const applySrc = (src) => {
      for (const p of bySrc.get(src)) {
        const fin = parseResult(cache.get(key(src)), p.items.length);
        if (fin) applyPiece(p, fin);
        else if (!isRetry) {
          // метки потеряны: переводим узлы этого абзаца по одному
          for (const it of p.items) if (it.todo) retry.push({ block: p.block, items: [Object.assign({}, it, { vt: false })] });
        }
      }
    };

    const need = [];
    for (const src of bySrc.keys()) (cache.has(key(src)) ? applySrc(src) : need.push(src));

    const chunks = [];
    let cur = [], chars = 0;
    for (const src of need) {
      if (cur.length && (cur.length >= CHUNK_PIECES || chars + src.length > CHUNK_CHARS)) {
        chunks.push(cur);
        cur = [];
        chars = 0;
      }
      cur.push(src);
      chars += src.length;
    }
    if (cur.length) chunks.push(cur);

    let next = 0, finished = 0, failed = 0;
    const worker = async () => {
      while (next < chunks.length && gen === state.gen) {
        const chunk = chunks[next++];
        try {
          const res = await callHost(chunk);
          if (gen !== state.gen) return;
          chunk.forEach((src, i) => {
            if (typeof res[i] !== 'string') return;
            cache.set(key(src), res[i]);
            applySrc(src);
          });
        } catch (e) {
          failed++;
          console.warn('[steam-translate]', e.message);
        }
        finished++;
        if (onProgress) onProgress(finished / chunks.length);
      }
    };
    await Promise.all(Array.from({ length: Math.min(CONCURRENCY, chunks.length) }, worker));
    if (retry.length && gen === state.gen) {
      const r = await translatePieces(retry, null, true);
      failed += r.failed;
    }
    return { chunks: chunks.length, failed };
  }

  function revert() {
    state.gen++;
    state.on = false;
    state.busy = false;
    stopObserver();
    for (const [node, rec] of done) {
      if (node.isConnected && node.nodeValue === rec.tr) node.nodeValue = rec.orig;
    }
    done.clear();
    render();
  }

  async function translatePage() {
    state.on = true;
    state.busy = true;
    state.err = false;
    const gen = ++state.gen;
    render(0);
    try {
      const r = await translatePieces(collectPieces([document.body]), (p) => render(p));
      if (gen !== state.gen) return;
      state.err = r.chunks > 0 && r.failed >= r.chunks;
    } catch (e) {
      console.warn('[steam-translate]', e.message);
      state.err = true;
    }
    if (gen !== state.gen) return;
    state.busy = false;
    if (state.err) {
      state.on = false;
    } else {
      startObserver();
    }
    render();
  }

  // ---------- подгружаемый контент (отзывы, «ещё», вкладки) ----------
  let mo = null, flushTimer = 0;
  const queue = new Set();
  function flush() {
    flushTimer = 0;
    if (!state.on) return;
    const roots = [];
    for (const n of queue) {
      if (!n.isConnected) continue;
      const rec = n.nodeType === 3 ? done.get(n) : null;
      if (rec && rec.tr === n.nodeValue) continue; // это наша собственная правка
      roots.push(n);
    }
    queue.clear();
    if (!roots.length) return;
    const pieces = collectPieces(roots);
    if (pieces.length) translatePieces(pieces);
  }
  function startObserver() {
    if (mo) return;
    mo = new MutationObserver((muts) => {
      for (const m of muts) {
        if (m.type === 'characterData') queue.add(m.target);
        else m.addedNodes.forEach((n) => (n.nodeType === 1 || n.nodeType === 3) && queue.add(n));
      }
      if (queue.size && !flushTimer) flushTimer = setTimeout(flush, 500);
    });
    mo.observe(document.body, { childList: true, subtree: true, characterData: true });
  }
  function stopObserver() {
    if (mo) mo.disconnect();
    mo = null;
    queue.clear();
    clearTimeout(flushTimer);
    flushTimer = 0;
  }

  // ---------- кнопка ----------
  let btn, auto, host;
  function render(progress) {
    if (!btn) return;
    if (state.busy) {
      btn.textContent = '⏳ Перевожу… ' + Math.round((progress || 0) * 100) + '%';
      btn.className = 'btn busy';
    } else if (state.on) {
      btn.textContent = '↩ Оригинал';
      btn.className = 'btn on';
    } else if (state.err) {
      btn.textContent = '⚠ Ошибка, ещё раз';
      btn.className = 'btn err';
    } else {
      btn.textContent = '🌐 Перевести';
      btn.className = 'btn';
    }
  }

  function mount() {
    host = document.createElement('div');
    host.id = 'sb-translate-host';
    host.className = 'notranslate';
    host.setAttribute('translate', 'no');
    host.style.cssText = 'all:initial;position:fixed;right:16px;bottom:16px;z-index:2147483647;';
    const root = host.attachShadow({ mode: 'open' });
    root.innerHTML =
      '<style>' +
      '.box{display:flex;align-items:center;gap:8px;font:13px/1.2 "Motiva Sans",Arial,sans-serif}' +
      '.btn{cursor:pointer;border:0;border-radius:3px;padding:9px 14px;color:#fff;font:inherit;font-weight:bold;' +
      'background:linear-gradient(90deg,#06bfff,#2d73ff);box-shadow:0 2px 8px rgba(0,0,0,.5);opacity:.92}' +
      '.btn:hover{opacity:1;filter:brightness(1.1)}' +
      '.btn.on{background:linear-gradient(90deg,#5c7e10,#78a516)}' +
      '.btn.busy{background:#3b4e66;cursor:progress}' +
      '.btn.err{background:linear-gradient(90deg,#a33,#c55)}' +
      '.auto{display:flex;align-items:center;gap:4px;color:#c6d4df;background:rgba(23,26,33,.85);' +
      'border-radius:3px;padding:7px 9px;cursor:pointer;user-select:none}' +
      '.auto input{margin:0;cursor:pointer}' +
      '</style>' +
      '<div class="box"><label class="auto" title="Переводить каждую страницу сразу при открытии">' +
      '<input type="checkbox"> авто</label><button class="btn"></button></div>';
    btn = root.querySelector('.btn');
    auto = root.querySelector('input');
    auto.checked = lsGet('sbTrAuto') === '1';
    auto.addEventListener('change', () => lsSet('sbTrAuto', auto.checked ? '1' : '0'));
    btn.addEventListener('click', () => {
      if (state.busy) return;
      state.on ? revert() : translatePage();
    });
    document.body.appendChild(host);
    render();
    // Steam иногда пересобирает body: возвращаем кнопку на место.
    setInterval(() => {
      if (!host.isConnected && document.body) document.body.appendChild(host);
    }, 2000);
    if (auto.checked) setTimeout(translatePage, 300);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount, { once: true });
  else mount();
})();
