'use strict';

// Geometry (CSS px). Must match OverlayForm.CssSize / CssGlassInset.
// DWELL_MS: how long to rest on a slice before it joins the chain (must match .dwell in wheel.css).
const RO = 150, RI = 62, C = 170, GAP = 2.4, DWELL_MS = 540;

const IC = {
  doc: 'M7 3h7l5 5v13H7z M14 3v5h5 M10 13h6 M10 17h4',
  text: 'M5 6h14 M5 10h14 M5 14h10 M5 18h7',
  image: 'M4 5h16v14H4z M4 16l5-5 4 4 3-3 4 4 M15.5 9h.01',
  pdf: 'M7 3h7l5 5v13H7z M14 3v5h5 M9.5 16v-4h1.5a1.3 1.3 0 0 1 0 2.6H9.5',
  compress: 'M9 4v5H4 M15 4v5h5 M9 20v-5H4 M15 20v-5h5',
  split: 'M12 3v18 M4 7h5v10H4z M15 7h5v10h-5z',
  merge: 'M6 4v5a6 6 0 0 0 12 0V4 M12 15v6',
  lock: 'M6 11h12v9H6z M8 11V8a4 4 0 0 1 8 0v3',
  hide: 'M3 12s3.5-6 9-6 9 6 9 6-3.5 6-9 6-9-6-9-6z M9.5 12a2.5 2.5 0 0 0 5 0 M4 4l16 16',
  rotate: 'M20 12a8 8 0 1 1-2.3-5.6 M20 4v5h-5',
  crop: 'M6 2v16h16 M2 6h16v16',
  erase: 'M4 20h16 M7 16l9-9 3 3-9 9H7z',
  resize: 'M4 14v6h6 M20 10V4h-6 M4 20l7-7 M20 4l-7 7',
  pinoff: 'M12 21s-6-5.5-6-11a6 6 0 0 1 9.5-4.9 M17.6 8.5c.3.5.4 1 .4 1.5 0 5.5-6 11-6 11 M4 4l16 16',
  chat: 'M4 5h16v11H9l-5 4z',
  mail: 'M3 6h18v12H3z M3 7l9 6 9-6',
  phone: 'M8 3h8v18H8z M11 18h2',
  link: 'M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1 M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1'
};
// UI language, set by the host ("lang" message). Strings sit side by side where they are used.
let LANG = 'tr';
const t = (tr, en) => LANG === 'tr' ? tr : en;

const BADGE = { pdf: '#FF8A65', docx: '#7EA6FF', txt: '#C9D1E0', png: '#5FD0FF', jpg: '#FFC861', jpeg: '#FFC861', bmp: '#9FB4D8', webp: '#9BE8B0' };
const modeName = m => ({ convert: t('Dönüştür', 'Convert'), tools: t('Araçlar', 'Tools'), send: t('Gönder', 'Send') })[m];

// `ready` marks what the host can really do in this build; the rest says "yakında".
// `tool` opens a panel on drop instead of running at once, so it never joins a chain.
function actionsFor(file, mode) {
  const kind = file ? file.kind : 'other';
  if (mode === 'send') return [
    { id: 'wa', label: 'WhatsApp', icon: IC.chat, send: true, prev: t('Sohbete göre boyutlanır', 'Sized for the chat') },
    { id: 'mail', label: 'Mail', icon: IC.mail, send: true, prev: t('Ek sınırına göre sıkışır', 'Fits the attachment limit') },
    { id: 'tel', label: t('Telefona', 'To phone'), icon: IC.phone, send: true, prev: t('Aynı ağdaki telefona', 'To a phone on the same network') },
    { id: 'path', label: t('Yolu kopyala', 'Copy path'), icon: IC.link, send: true, ready: true, prev: t('Panoya kopyalanır', 'Copied to the clipboard') }
  ];
  if (mode === 'tools') {
    if (kind === 'img') return [
      { id: 'crop', label: t('Kırp', 'Crop'), icon: IC.crop, ready: true, tool: true, prev: S.count > 1 ? t('Tek resim kırpılır', 'Crop one image at a time') : t('Bırak: kırpma paneli açılır', 'Drop: opens the crop panel') },
      { id: 'cmp', label: t('Sıkıştır', 'Compress'), icon: IC.compress, ready: true, prev: t('JPG, kalite %70', 'JPG, quality 70%') },
      { id: 'bg', label: t('Arka planı sil', 'Cut out'), icon: IC.erase, prev: t('Şeffaf PNG', 'Transparent PNG') },
      { id: 'loc', label: t('Konumu sil', 'Remove location'), icon: IC.pinoff, radar: true, ready: true, prev: t('Konum ve cihaz bilgisi silinir', 'Removes location and device info') },
      { id: 'rs', label: t('Boyutlandır', 'Resize'), icon: IC.resize, ready: true, prev: t('1080 px genişlik', '1080 px wide') },
      { id: 'rot', label: t('Döndür', 'Rotate'), icon: IC.rotate, ready: true, prev: t('90° sağa', '90° clockwise') }
    ];
    if (kind === 'pdf') return [
      { id: 'cmp', label: t('Sıkıştır', 'Compress'), icon: IC.compress, ready: true, prev: t('Görseller küçültülür', 'Images are downscaled') },
      { id: 'hide', label: t('Gizle', 'Hide'), icon: IC.hide, radar: true, prev: t('Hassas alanlar kapatılır', 'Sensitive areas are covered') },
      { id: 'lock', label: t('Şifrele', 'Lock'), icon: IC.lock, ready: true, tool: true, prev: t('Bırak: parola sorulur', 'Drop: asks for a password') },
      { id: 'split', label: t('Böl', 'Split'), icon: IC.split, ready: true, prev: t('Her sayfa ayrı dosya', 'One file per page') },
      { id: 'merge', label: t('Birleştir', 'Merge'), icon: IC.merge, ready: true, prev: S.count > 1 ? t(`${S.count} PDF tek dosya`, `${S.count} PDFs into one file`) : t("Başka PDF'lerle tek dosya", 'One file with other PDFs') },
      { id: 'rot', label: t('Döndür', 'Rotate'), icon: IC.rotate, ready: true, prev: t('Tüm sayfalar 90°', 'All pages 90°') }
    ];
    // Documents and other files: compress and lock both produce a .zip (lock: AES-256, with a password panel).
    const zipTools = [
      { id: 'cmp', label: t('Sıkıştır', 'Compress'), icon: IC.compress, ready: true, prev: S.count > 1 ? t(`${S.count} dosya tek .zip`, `${S.count} files into one .zip`) : t('.zip içine sıkıştırılır', 'Compressed into a .zip') },
      { id: 'lock', label: t('Şifrele', 'Lock'), icon: IC.lock, ready: true, tool: true, prev: t('Bırak: parola sorulur, şifreli .zip', 'Drop: asks for a password, encrypted .zip') }
    ];
    if (kind === 'doc') {
      const hideOk = ['txt', 'md', 'docx'].includes(file.ext);
      return [zipTools[0],
        { id: 'hide', label: t('Gizle', 'Hide'), icon: IC.hide, radar: true, ready: hideOk, prev: hideOk ? t('TC, IBAN, e-posta, telefon ████ olur', 'ID, IBAN, email, phone become ████') : t('Yalnız .txt, .md, .docx', 'Only .txt, .md, .docx') },
        zipTools[1]];
    }
    return zipTools;
  }
  if (kind === 'pdf') return [
    { id: 'docx', label: 'Word', icon: IC.doc, prev: t('Düzen korunur', 'Layout is kept') },
    { id: 'txt', label: t('Metin', 'Text'), icon: IC.text, ready: true, prev: t('Yalnız metin, düz .txt', 'Text only, plain .txt') },
    { id: 'png', label: 'PNG', icon: IC.image, ready: true, prev: t('Her sayfa bir görsel · 200 dpi', 'One image per page · 200 dpi') },
    { id: 'jpg', label: 'JPG', icon: IC.image, ready: true, prev: t('Her sayfa bir görsel · 200 dpi', 'One image per page · 200 dpi') }
  ];
  if (kind === 'img') {
    const own = file.ext === 'jpeg' ? 'jpg' : file.ext;
    const P = { png: [t('Kayıpsız', 'Lossless'), true], jpg: [t('Kalite %92', 'Quality 92%'), true], bmp: [t('Sıkıştırmasız', 'Uncompressed'), true], webp: [t('Küçük ve kaliteli', 'Small and sharp'), false], pdf: [t('Tek sayfa PDF', 'Single-page PDF'), false] };
    return ['png', 'jpg', 'webp', 'pdf', 'bmp'].filter(e => e !== own).slice(0, 4).map(e => ({
      id: e, label: e === 'pdf' ? 'PDF' : (e === 'webp' ? 'WebP' : e.toUpperCase()), icon: e === 'pdf' ? IC.pdf : IC.image, prev: P[e][0], ready: P[e][1]
    }));
  }
  if (kind === 'doc') {
    // Native: txt/md -> docx, docx -> txt, txt/md/docx -> text-only PDF. LibreOffice (if installed) does everything else and keeps the layout in PDFs.
    const native = ['txt', 'md', 'docx'].includes(file.ext), plain = file.ext === 'txt' || file.ext === 'md';
    const P = {
      pdf: native ? t('LibreOffice varsa biçim korunur, yoksa yalnız metin', 'Layout kept with LibreOffice, else text only') : t('LibreOffice gerekli', 'Needs LibreOffice'),
      txt: file.ext === 'docx' ? t('Yalnız metin', 'Text only') : t('LibreOffice gerekli', 'Needs LibreOffice'),
      docx: plain ? t('Düz metin, biçimsiz', 'Plain text, no formatting') : t('LibreOffice gerekli', 'Needs LibreOffice')
    };
    return ['pdf', 'txt', 'docx'].filter(e => e !== file.ext && !(e === 'txt' && file.ext === 'md')).map(e => ({
      id: e, label: e === 'docx' ? 'Word' : (e === 'txt' ? t('Metin', 'Text') : 'PDF'), icon: e === 'pdf' ? IC.pdf : (e === 'txt' ? IC.text : IC.doc), prev: P[e], ready: true
    }));
  }
  return [];
}

// ---------- state ----------
const S = { open: false, mode: 'convert', file: null, count: 0, sens: [], acts: [], hover: -1, chain: [], seq: 0, busy: false };
let dwellTimer = 0;

const $ = id => document.getElementById(id);
const wheel = $('wheel'), slicesEl = $('slices'), labelsEl = $('labels'), lightEl = $('light');
const titleEl = $('center-title'), subEl = $('center-sub'), hintEl = $('center-hint'), badgeEl = $('badge');

function setHint(text, cls) {
  hintEl.className = 'center-hint';
  hintEl.textContent = text;
  if (cls) { void hintEl.offsetWidth; hintEl.classList.add(cls); }
}

function post(msg) { if (window.chrome && chrome.webview) chrome.webview.postMessage(msg); }

// ---------- sound ----------
let ac = null;
function tone(freq, dur, gain, delay = 0, freqEnd = 0, type = 'sine') {
  try {
    if (!ac) ac = new (window.AudioContext || window.webkitAudioContext)();
    const t = ac.currentTime + delay, o = ac.createOscillator(), g = ac.createGain();
    o.type = type; o.frequency.setValueAtTime(freq, t);
    if (freqEnd) o.frequency.exponentialRampToValueAtTime(freqEnd, t + dur);
    g.gain.setValueAtTime(0.0001, t); g.gain.exponentialRampToValueAtTime(gain, t + 0.008); g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
    o.connect(g); g.connect(ac.destination); o.start(t); o.stop(t + dur + 0.03);
  } catch (e) { /* audio is decoration */ }
}
const sfx = {
  open: () => tone(380, 0.2, 0.05, 0, 760),
  tick: () => tone(2300, 0.022, 0.025, 0, 0, 'triangle'),
  lock: () => { tone(880, 0.08, 0.05); tone(1320, 0.12, 0.04, 0.06); },
  flip: () => tone(560, 0.14, 0.04, 0, 1250),
  done: () => { tone(784, 0.12, 0.05); tone(1175, 0.26, 0.05, 0.09); },
  fail: () => tone(420, 0.2, 0.04, 0, 260)
};

// ---------- geometry ----------
const pt = (r, a) => { const t = a * Math.PI / 180; return (C + Math.cos(t) * r).toFixed(2) + ' ' + (C + Math.sin(t) * r).toFixed(2); };
function arc(a0, a1) {
  const big = (a1 - a0) > 180 ? 1 : 0;
  return `M${pt(RO, a0)} A${RO} ${RO} 0 ${big} 1 ${pt(RO, a1)} L${pt(RI, a1)} A${RI} ${RI} 0 ${big} 0 ${pt(RI, a0)} Z`;
}
const svgNS = 'http://www.w3.org/2000/svg';
function icon(d, size) {
  const s = document.createElementNS(svgNS, 'svg');
  s.setAttribute('width', size); s.setAttribute('height', size); s.setAttribute('viewBox', '0 0 24 24');
  const p = document.createElementNS(svgNS, 'path'); p.setAttribute('class', 'ico'); p.setAttribute('d', d);
  s.appendChild(p); return s;
}

// ---------- render ----------
function build() {
  S.acts = actionsFor(S.file, S.mode);
  S.hover = -1;
  slicesEl.textContent = ''; labelsEl.textContent = '';
  const n = S.acts.length;
  wheel.classList.remove('anim-a', 'anim-b');
  void wheel.offsetWidth; // restart the opening animation
  wheel.classList.add(++S.seq % 2 ? 'anim-a' : 'anim-b');
  if (!n) { renderCenter(); return; }
  const step = 360 / n;
  S.acts.forEach((a, i) => {
    const mid = -90 + i * step;
    const g = document.createElement('div');
    g.className = 'slice' + (a.ready ? '' : ' soon') + (a.tool ? ' tool' : ''); g.style.animationDelay = (i * 30) + 'ms';
    const svg = document.createElementNS(svgNS, 'svg');
    svg.setAttribute('width', '340'); svg.setAttribute('height', '340'); svg.setAttribute('viewBox', '0 0 340 340');
    const d = arc(mid - step / 2 + GAP / 2, mid + step / 2 - GAP / 2);
    const fill = document.createElementNS(svgNS, 'path'); fill.setAttribute('class', 'fill'); fill.setAttribute('d', d);
    const dw = document.createElementNS(svgNS, 'path'); dw.setAttribute('class', 'dwell'); dw.setAttribute('d', d); dw.setAttribute('pathLength', '100');
    svg.append(fill, dw); g.appendChild(svg); slicesEl.appendChild(g);

    const rad = mid * Math.PI / 180, rm = (RI + RO) / 2 + 2;
    const l = document.createElement('div');
    l.className = 'label' + (a.ready ? '' : ' soon') + (a.radar || a.send ? ' radar-target' : '');
    l.style.left = (C + Math.cos(rad) * rm - 48).toFixed(1) + 'px';
    l.style.top = (C + Math.sin(rad) * rm - 25).toFixed(1) + 'px';
    l.style.animationDelay = (90 + i * 30) + 'ms';
    const span = document.createElement('span'); span.textContent = a.label;
    const warn = document.createElement('div'); warn.className = 'warn';
    const no = document.createElement('div'); no.className = 'no';
    l.append(icon(a.icon, 22), span, warn, no);
    labelsEl.appendChild(l);
    a._slice = g; a._label = l; a._fill = fill; a._mid = mid;
  });
  paint();
}

function paint() {
  S.acts.forEach((a, i) => {
    const hov = i === S.hover, cn = S.chain.findIndex(c => c.id === a.id);
    a._slice.classList.toggle('hover', hov);
    a._slice.classList.toggle('chained', cn >= 0);
    a._label.classList.toggle('hover', hov);
    a._label.classList.toggle('chained', cn >= 0);
    a._label.querySelector('.no').textContent = cn >= 0 ? String(cn + 1) : '';
    const push = hov ? 3 : 0, rad = a._mid * Math.PI / 180;
    const tr = `translate(${(Math.cos(rad) * push).toFixed(2)}px, ${(Math.sin(rad) * push).toFixed(2)}px)`;
    a._fill.style.transform = tr; a._label.style.transform = tr;
  });
  renderCenter();
}

function renderCenter() {
  const h = S.hover >= 0 ? S.acts[S.hover] : null;
  subEl.classList.remove('radar-text');
  setHint('');
  if (h) {
    titleEl.textContent = h.label;
    subEl.textContent = h.ready ? h.prev : t('Yakında', 'Coming soon');
    const pos = S.chain.findIndex(c => c.id === h.id);
    if (pos >= 0) setHint(t(`Zincirde ${pos + 1}. adım · bırak`, `Step ${pos + 1} in chain · drop`), 'done');
    else if (S.chain.length) setHint(t(`Bırak: ${S.chain.length + 1} adım birden`, `Drop: ${S.chain.length + 1} steps at once`));
    else if (h.ready && !h.tool) setHint(t('Beklersen zincire eklenir', 'Hold to add to chain'), 'waiting');
  } else if (S.chain.length) {
    titleEl.textContent = t('Zincir', 'Chain');
    subEl.textContent = S.chain.map(c => c.label).join(' → ');
    setHint(t('Bir dilime bırak: hepsi sırayla', 'Drop on a slice: all run in order'));
  } else if (S.sens.length) {
    titleEl.textContent = t('Dikkat', 'Heads up');
    subEl.textContent = S.sens.join(' · ');
    subEl.classList.add('radar-text');
  } else if (S.file && !S.acts.length) {
    titleEl.textContent = S.file.name;
    subEl.textContent = t('Bu dosya türü henüz yok', 'This file type is not supported yet');
  } else {
    titleEl.textContent = modeName(S.mode);
    subEl.textContent = S.file ? `${S.file.name}.${S.file.ext}${S.count > 1 ? ` +${S.count - 1}` : ''}` : t('Dosyayı bekliyor', 'Waiting for a file');
  }
}

function setLight(x, y) {
  const ang = Math.atan2(y - C, x - C) * 180 / Math.PI + 90 - 45;
  lightEl.style.background = `conic-gradient(from ${ang.toFixed(1)}deg, rgba(255,255,255,0) 0deg, rgba(255,255,255,.85) 45deg, rgba(255,255,255,0) 90deg, rgba(255,255,255,0) 360deg)`;
}

function setHover(idx) {
  if (idx === S.hover) return;
  S.hover = idx;
  clearTimeout(dwellTimer);
  if (idx >= 0) {
    // restart the dwell trace on the new slice
    const a = S.acts[idx];
    const dw = a._slice.querySelector('.dwell');
    dw.style.animation = 'none'; void dw.getBoundingClientRect(); dw.style.animation = '';
    sfx.tick();
    if (a.ready && !a.tool) dwellTimer = setTimeout(lockCurrent, DWELL_MS);
  }
  paint();
}

function lockCurrent() {
  if (S.hover < 0) return;
  const a = S.acts[S.hover];
  if (!a.ready) return; // a step that does not exist yet must never sink a whole chain
  const last = S.chain[S.chain.length - 1];
  if (last && last.id === a.id) return;
  S.chain.push({ id: a.id, label: a.label, ready: !!a.ready });
  sfx.lock();
  paint();
}

function hitTest(x, y) {
  const n = S.acts.length;
  if (!n) return -1;
  const dx = x - C, dy = y - C, dist = Math.hypot(dx, dy);
  if (dist < RI - 8) return -1; // center = cancel
  const step = 360 / n;
  const ang = Math.atan2(dy, dx) * 180 / Math.PI + 90;
  return ((Math.round(ang / step) % n) + n) % n;
}

// The page is authored in CSS px for a base width (340 for the wheel, the panel width while a
// panel is up) and scaled to the real window, whatever WebView2's own zoom says.
let baseW = 340;
function fit() { document.documentElement.style.setProperty('--fit', String(innerWidth / baseW)); }
addEventListener('resize', fit);
fit();

// ---------- host messages ----------
const handlers = {
  lang(m) {
    LANG = m.lang === 'en' ? 'en' : 'tr';
    document.documentElement.lang = LANG;
    applyStaticText();
    if (S.open && !S.busy) build();
  },
  backdrop(m) {
    document.documentElement.style.setProperty('--backdrop', m.src ? `url("${m.src}")` : 'none');
  },
  open(m) {
    Object.assign(S, { open: true, mode: m.mode || 'convert', file: null, count: 0, sens: [], chain: [], busy: false });
    wheel.className = 'wheel';
    badgeEl.textContent = '';
    build();
    sfx.open();
  },
  mode(m) {
    if (!S.open || S.busy || S.mode === m.mode) return;
    S.mode = m.mode;
    clearTimeout(dwellTimer);
    build();
    sfx.tick();
  },
  enter(m) {
    if (!S.open || S.busy) return;
    const f = (m.files || [])[0] || null;
    const count = m.count || (f ? 1 : 0);
    const changed = !S.file || !f || S.file.name !== f.name || S.file.ext !== f.ext || S.count !== count;
    S.file = f;
    S.count = count;
    if (changed) build(); else renderCenter();
  },
  radar(m) {
    S.sens = m.sens || [];
    wheel.classList.toggle('sensitive', S.sens.length > 0);
    renderCenter();
  },
  over(m) {
    if (!S.open || S.busy) return;
    setLight(m.x, m.y);
    setHover(hitTest(m.x, m.y));
  },
  leave() { if (!S.busy) setHover(-1); },
  drop() {
    clearTimeout(dwellTimer);
    if (S.hover < 0) { post({ type: 'cancel' }); return; }
    const cur = S.acts[S.hover];
    if (!cur.ready) {
      S.busy = true;
      titleEl.textContent = cur.label;
      subEl.textContent = t('Yakında geliyor', 'Coming soon');
      setHint('');
      sfx.fail();
      setTimeout(() => post({ type: 'cancel' }), 900);
      return;
    }
    const steps = S.chain.map(c => c.id);
    if (cur.tool) {
      S.busy = true;
      titleEl.textContent = cur.label;
      subEl.textContent = t('Açılıyor', 'Opening');
      setHint('');
      Panel.steps = steps;
      post({ type: 'tool', tool: cur.id });
      return;
    }
    if (steps[steps.length - 1] !== cur.id) steps.push(cur.id);
    S.busy = true;
    titleEl.textContent = t('Çalışıyor', 'Working');
    setHint('');
    subEl.textContent = steps.length > 1 ? t(`${steps.length} adım`, `${steps.length} steps`) : cur.label;
    post({ type: 'run', steps });
  },
  result(m) {
    if (Panel.open) { Panel.result(m); return; }
    wheel.classList.add('result');
    wheel.classList.toggle('ok', !!m.ok);
    if (m.ok && m.ext) {
      badgeEl.textContent = m.ext.toUpperCase();
      badgeEl.style.background = BADGE[m.ext] || '#C9D1E0';
      badgeEl.style.display = '';
    } else {
      badgeEl.style.display = 'none';
    }
    titleEl.textContent = m.title;
    setHint('');
    subEl.textContent = m.detail || '';
    subEl.classList.remove('radar-text');
    if (m.ok) { sfx.flip(); setTimeout(sfx.done, 160); } else sfx.fail();
  },
  'crop-src'(m) { Crop.load(m); },
  'lock-form'(m) { Lock.load(m); },
  'panel-open'(m) { Panel.show(m); },
  key(m) { if (Panel.open) Panel.tool.key(m.key); },
  close() {
    if (Panel.open) { Panel.close(); return; }
    S.open = false;
    clearTimeout(dwellTimer);
    wheel.classList.add('closing');
    setTimeout(() => { wheel.className = 'wheel hidden'; }, 170);
  }
};

// ---------- tool panels ----------
// A `tool` slice that gets the drop grows the wheel into a panel. Each tool lays out its own
// body in panel CSS px and reports the size; the host then reshapes the window ("panel-size")
// and answers "panel-open" with where the wheel was, so the panel scales out of it.
const PANEL_PAD = { top: 18, bottom: 20, head: 32, gap: 14, foot: 44 };
const panelEl = $('panel');

const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

const Panel = {
  open: false, tool: null, steps: [], w: 0, h: 0,

  size(tool, w, contentH, focus) {
    this.tool = tool;
    this.w = w;
    this.h = PANEL_PAD.top + PANEL_PAD.head + PANEL_PAD.gap + contentH + PANEL_PAD.gap + PANEL_PAD.foot + PANEL_PAD.bottom;
    panelEl.style.setProperty('--pw', w + 'px');
    panelEl.style.setProperty('--ph', this.h + 'px');
    panelEl.classList.remove('done');
    document.querySelectorAll('.panel-body').forEach(b => { b.hidden = b.id !== tool.bodyId; });
    post({ type: 'panel-size', w, h: this.h, focus: !!focus });
  },

  show(m) {
    this.open = true;
    baseW = this.w; fit();
    wheel.className = 'wheel hidden';
    panelEl.style.setProperty('--ox', m.ox + 'px');
    panelEl.style.setProperty('--oy', m.oy + 'px');
    panelEl.className = 'panel';
    void panelEl.offsetWidth; // restart the opening animation
    panelEl.classList.add('opening');
    setRim(this.w / 2, 0);
    sfx.open();
    if (this.tool.shown) this.tool.shown(m);
  },

  close() {
    this.open = false;
    S.open = false;
    clearTimeout(dwellTimer);
    if (this.tool.closed) this.tool.closed();
    panelEl.classList.remove('opening');
    panelEl.classList.add('closing');
    setTimeout(() => { panelEl.className = 'panel hidden'; wheel.className = 'wheel hidden'; baseW = 340; fit(); }, 170);
  },

  run(extra) {
    post({ type: 'run', steps: [...this.steps, this.tool.id], ...extra });
  },

  result(m) {
    panelEl.classList.toggle('done', !!m.ok);
    this.tool.info.textContent = m.ok ? `${m.title} · ${m.detail}` : `${m.title}: ${m.detail}`;
    if (m.ok) { sfx.flip(); setTimeout(sfx.done, 160); } else sfx.fail();
  }
};

// a light that follows the pointer around the panel rim, like the wheel's
function setRim(x, y) {
  const ang = Math.atan2(y - Panel.h / 2, x - Panel.w / 2) * 180 / Math.PI + 90 - 45;
  $('panel-light').style.background = `conic-gradient(from ${ang.toFixed(1)}deg, rgba(255,255,255,.14) 0deg, rgba(255,255,255,.9) 45deg, rgba(255,255,255,.14) 90deg, rgba(255,255,255,.14) 360deg)`;
}

panelEl.addEventListener('pointermove', e => {
  if (!Panel.open || panelEl.classList.contains('done')) return;
  const b = panelEl.getBoundingClientRect(), f = b.width / Panel.w;
  setRim((e.clientX - b.left) / f, (e.clientY - b.top) / f);
});

// ---------- crop ----------
const CROP = { w: 560, stageMaxW: 520, stageMaxH: 360 };
const PRESETS = [['free', () => t('Serbest', 'Free'), 0], ['sq', () => '1:1', 1], ['vs', () => t('Vesikalık', 'Passport photo'), 35 / 45], ['wide', () => '16:9', 16 / 9]];
const MIN_CROP = 16; // image px
const stageEl = $('stage'), rectEl = $('crop-rect'), cropImg = $('crop-img');

const Crop = {
  id: 'crop', bodyId: 'crop-body', info: $('crop-info'),
  busy: false, W: 0, H: 0, k: 1, R: null, preset: 'free', drag: null,

  load(m) {
    Object.assign(this, { W: m.w, H: m.h, busy: false, preset: 'free', drag: null });
    this.k = Math.min(CROP.stageMaxW / m.w, CROP.stageMaxH / m.h);
    const sw = Math.max(1, Math.round(m.w * this.k)), sh = Math.max(1, Math.round(m.h * this.k));
    stageEl.style.width = sw + 'px'; stageEl.style.height = sh + 'px';
    cropImg.src = m.src;
    $('crop-file').textContent = m.name;
    const inset = 0.08;
    this.R = { x: m.w * inset, y: m.h * inset, w: m.w * (1 - 2 * inset), h: m.h * (1 - 2 * inset) };
    $('crop-apply').disabled = false; $('crop-cancel').disabled = false;
    this.renderPresets(); this.render();
    Panel.size(this, CROP.w, sh, false);
  },

  closed() { this.drag = null; },

  ratio() { return PRESETS.find(p => p[0] === this.preset)[2]; },

  renderPresets() {
    const box = $('crop-presets');
    box.textContent = '';
    PRESETS.forEach(([id, label]) => {
      const b = document.createElement('button');
      b.className = 'chip' + (id === this.preset ? ' on' : '');
      b.textContent = label();
      b.addEventListener('click', () => this.pick(id));
      box.appendChild(b);
    });
    stageEl.classList.toggle('locked', this.ratio() > 0);
  },

  pick(id) {
    if (this.busy || id === this.preset) return;
    this.preset = id;
    const r = this.ratio();
    if (r > 0) {
      // the largest box of that shape that fits, kept around the current selection
      const w = Math.min(this.W, this.H * r) * 0.92, h = w / r;
      const cx = this.R.x + this.R.w / 2, cy = this.R.y + this.R.h / 2;
      this.R = { x: clamp(cx - w / 2, 0, this.W - w), y: clamp(cy - h / 2, 0, this.H - h), w, h };
    }
    rectEl.classList.add('anim');
    setTimeout(() => rectEl.classList.remove('anim'), 380);
    this.renderPresets(); this.render();
    sfx.tick();
  },

  render() {
    const k = this.k, R = this.R;
    rectEl.style.left = (R.x * k) + 'px'; rectEl.style.top = (R.y * k) + 'px';
    rectEl.style.width = (R.w * k) + 'px'; rectEl.style.height = (R.h * k) + 'px';
    if (!this.busy) this.info.textContent = `${Math.round(R.w)} × ${Math.round(R.h)} px`;
  },

  // pointer position in image pixels (getBoundingClientRect already includes the --fit scale)
  at(e) {
    const b = stageEl.getBoundingClientRect();
    return { x: (e.clientX - b.left) / b.width * this.W, y: (e.clientY - b.top) / b.height * this.H };
  },

  down(e) {
    if (!Panel.open || this.busy || !rectEl.contains(e.target)) return;
    e.preventDefault();
    const p = this.at(e);
    this.drag = { h: e.target.dataset.h || 'move', off: { x: p.x - this.R.x, y: p.y - this.R.y }, R0: { ...this.R } };
    stageEl.setPointerCapture(e.pointerId);
  },

  move(e) {
    if (!this.drag) return;
    const p = this.at(e), d = this.drag, R0 = d.R0, W = this.W, H = this.H;
    if (d.h === 'move') {
      this.R = { ...this.R, x: clamp(p.x - d.off.x, 0, W - this.R.w), y: clamp(p.y - d.off.y, 0, H - this.R.h) };
    } else {
      const dx = d.h.includes('e') ? 1 : d.h.includes('w') ? -1 : 0;
      const dy = d.h.includes('s') ? 1 : d.h.includes('n') ? -1 : 0;
      const r = this.ratio();
      if (r > 0) {
        // fixed shape: corners only, the opposite corner stays put
        const ax = dx > 0 ? R0.x : R0.x + R0.w, ay = dy > 0 ? R0.y : R0.y + R0.h;
        const availW = dx > 0 ? W - ax : ax, availH = dy > 0 ? H - ay : ay;
        let w = clamp(Math.max(Math.abs(p.x - ax), Math.abs(p.y - ay) * r), MIN_CROP, availW), h = w / r;
        if (h > availH) { h = availH; w = h * r; }
        this.R = { x: dx > 0 ? ax : ax - w, y: dy > 0 ? ay : ay - h, w, h };
      } else {
        let l = R0.x, t = R0.y, rt = R0.x + R0.w, b = R0.y + R0.h;
        if (dx > 0) rt = clamp(p.x, l + MIN_CROP, W);
        if (dx < 0) l = clamp(p.x, 0, rt - MIN_CROP);
        if (dy > 0) b = clamp(p.y, t + MIN_CROP, H);
        if (dy < 0) t = clamp(p.y, 0, b - MIN_CROP);
        this.R = { x: l, y: t, w: rt - l, h: b - t };
      }
    }
    this.render();
  },

  up(e) {
    if (!this.drag) return;
    this.drag = null;
    if (stageEl.hasPointerCapture(e.pointerId)) stageEl.releasePointerCapture(e.pointerId);
  },

  apply() {
    if (!Panel.open || this.busy) return;
    this.busy = true;
    $('crop-apply').disabled = true; $('crop-cancel').disabled = true;
    this.info.textContent = t('Kırpılıyor', 'Cropping');
    const R = this.R, x = Math.round(R.x), y = Math.round(R.y);
    Panel.run({ crop: { x, y, w: Math.min(this.W - x, Math.round(R.w)), h: Math.min(this.H - y, Math.round(R.h)) } });
  },

  cancel() {
    if (!Panel.open || this.busy) return;
    post({ type: 'cancel' });
  },

  // keys come from the host hook: the crop panel never has focus
  key(k) {
    if (k === 'esc') this.cancel();
    else if (k === 'enter') this.apply();
  }
};

stageEl.addEventListener('pointerdown', e => Crop.down(e));
stageEl.addEventListener('pointermove', e => Crop.move(e));
stageEl.addEventListener('pointerup', e => Crop.up(e));
stageEl.addEventListener('pointercancel', e => Crop.up(e));
$('crop-apply').addEventListener('click', () => Crop.apply());
$('crop-cancel').addEventListener('click', () => Crop.cancel());

// ---------- lock (password for PDFs, encrypted zip for everything else) ----------
const LOCK = { w: 440, content: 74, minLength: 4 };
const EYE_OPEN = 'M2 12s3.5-6 10-6 10 6 10 6-3.5 6-10 6-10-6-10-6z M12 9.5a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5';
const pwEl = $('lock-pw'), lockHint = $('lock-hint');

const Lock = {
  id: 'lock', bodyId: 'lock-body', info: $('lock-info'), busy: false, zip: false,

  load(m) {
    this.busy = false;
    this.zip = !!m.zip;
    $('lock-file').textContent = m.count > 1 ? (this.zip ? t(`${m.count} dosya`, `${m.count} files`) : t(`${m.count} PDF`, `${m.count} PDFs`)) : m.name;
    pwEl.value = '';
    pwEl.disabled = false;
    this.reveal(false);
    this.info.textContent = this.zip ? t('Şifreli .zip · 7-Zip ile açılır', 'Encrypted .zip · opens in 7-Zip')
      : m.count > 1 ? t('Hepsine aynı parola · orijinaller şifresiz kalır', 'Same password for all · originals stay unlocked') : t('Orijinal dosya şifresiz kalır', 'The original file stays unlocked');
    $('lock-cancel').disabled = false;
    this.check();
    Panel.size(this, LOCK.w, LOCK.content, true);
  },

  // Windows can refuse to hand over focus; then typing would land in Explorer, so ask for a click.
  shown(m) {
    if (m.focused) pwEl.focus();
    else { lockHint.textContent = t('Yazmak için kutuya tıkla', 'Click the box to type'); pwEl.parentElement.classList.add('nudge'); }
  },

  closed() { pwEl.value = ''; pwEl.blur(); },

  reveal(on) {
    pwEl.type = on ? 'text' : 'password';
    $('lock-eye-path').setAttribute('d', on ? IC.hide : EYE_OPEN);
    $('lock-eye').setAttribute('aria-label', on ? t('Parolayı gizle', 'Hide password') : t('Parolayı göster', 'Show password'));
  },

  check() {
    const n = pwEl.value.length;
    lockHint.textContent = n === 0 ? t('Bu parola olmadan dosya açılmaz, unutma', "The file can't be opened without this password, so don't forget it")
      : n < LOCK.minLength ? t(`En az ${LOCK.minLength} karakter`, `At least ${LOCK.minLength} characters`) : this.zip ? t('AES-256 şifreli .zip olacak', 'Will be an AES-256 encrypted .zip') : t('AES-256 ile kilitlenecek', 'Will be locked with AES-256');
    $('lock-apply').disabled = this.busy || n < LOCK.minLength;
  },

  apply() {
    if (!Panel.open || this.busy || pwEl.value.length < LOCK.minLength) return;
    this.busy = true;
    pwEl.disabled = true;
    $('lock-apply').disabled = true; $('lock-cancel').disabled = true;
    this.info.textContent = t('Şifreleniyor', 'Locking');
    Panel.run({ password: pwEl.value });
    pwEl.value = '';
  },

  cancel() {
    if (!Panel.open || this.busy) return;
    post({ type: 'cancel' });
  },

  // Enter is taken only from the field itself: when the panel has no focus the same key also
  // reached Explorer, and acting on it here would lock with whatever the field held.
  key(k) { if (k === 'esc') this.cancel(); }
};

pwEl.addEventListener('input', () => Lock.check());
pwEl.addEventListener('focus', () => { pwEl.parentElement.classList.remove('nudge'); Lock.check(); });
pwEl.addEventListener('keydown', e => {
  if (e.key === 'Enter') { e.preventDefault(); Lock.apply(); }
  else if (e.key === 'Escape') { e.preventDefault(); Lock.cancel(); }
});
$('lock-eye').addEventListener('click', () => { Lock.reveal(pwEl.type === 'password'); pwEl.focus(); });
$('lock-apply').addEventListener('click', () => Lock.apply());
$('lock-cancel').addEventListener('click', () => Lock.cancel());

// Fixed text in index.html carries both languages: data-tr / data-en (and data-tr-label / data-en-label for aria-label).
function applyStaticText() {
  document.querySelectorAll('[data-tr]').forEach(el => { el.textContent = el.dataset[LANG]; });
  document.querySelectorAll('[data-tr-label]').forEach(el => { el.setAttribute('aria-label', LANG === 'tr' ? el.dataset.trLabel : el.dataset.enLabel); });
  document.querySelectorAll('[data-tr-placeholder]').forEach(el => { el.placeholder = LANG === 'tr' ? el.dataset.trPlaceholder : el.dataset.enPlaceholder; });
}

if (window.chrome && chrome.webview) {
  chrome.webview.addEventListener('message', e => {
    const m = e.data;
    try { (handlers[m.type] || (() => {}))(m); }
    catch (err) { post({ type: 'log', text: String(err && err.stack || err) }); }
  });
}
