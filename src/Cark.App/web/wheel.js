'use strict';

// Geometry (CSS px). Must match OverlayForm.CssSize / CssGlassInset.
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
const BADGE = { pdf: '#FF8A65', docx: '#7EA6FF', txt: '#C9D1E0', png: '#5FD0FF', jpg: '#FFC861', jpeg: '#FFC861', bmp: '#9FB4D8', webp: '#9BE8B0' };
const MODE_NAME = { convert: 'Dönüştür', tools: 'Araçlar', send: 'Gönder' };

// `ready` marks what the host can really do in this build; the rest says "yakında".
function actionsFor(file, mode) {
  const kind = file ? file.kind : 'other';
  if (mode === 'send') return [
    { id: 'wa', label: 'WhatsApp', icon: IC.chat, send: true, prev: 'Sohbete göre boyutlanır' },
    { id: 'mail', label: 'Mail', icon: IC.mail, send: true, prev: 'Ek sınırına göre sıkışır' },
    { id: 'tel', label: 'Telefona', icon: IC.phone, send: true, prev: 'Aynı ağdaki telefona' },
    { id: 'path', label: 'Yolu kopyala', icon: IC.link, send: true, ready: true, prev: 'Panoya kopyalanır' }
  ];
  if (mode === 'tools') {
    if (kind === 'img') return [
      { id: 'crop', label: 'Kırp', icon: IC.crop, prev: 'Oran seç, kaydet' },
      { id: 'cmp', label: 'Sıkıştır', icon: IC.compress, ready: true, prev: 'JPG, kalite %70' },
      { id: 'bg', label: 'Arka planı sil', icon: IC.erase, prev: 'Şeffaf PNG' },
      { id: 'loc', label: 'Konumu sil', icon: IC.pinoff, radar: true, ready: true, prev: 'Konum ve cihaz bilgisi silinir' },
      { id: 'rs', label: 'Boyutlandır', icon: IC.resize, ready: true, prev: '1080 px genişlik' },
      { id: 'rot', label: 'Döndür', icon: IC.rotate, ready: true, prev: '90° sağa' }
    ];
    if (kind === 'pdf') return [
      { id: 'cmp', label: 'Sıkıştır', icon: IC.compress, prev: 'Boyutu küçült' },
      { id: 'hide', label: 'Gizle', icon: IC.hide, radar: true, prev: 'Hassas alanlar kapatılır' },
      { id: 'lock', label: 'Şifrele', icon: IC.lock, prev: 'Parola ile kilitle' },
      { id: 'split', label: 'Böl', icon: IC.split, prev: 'Her sayfa ayrı dosya' },
      { id: 'merge', label: 'Birleştir', icon: IC.merge, prev: 'Başka PDF ile tek dosya' },
      { id: 'rot', label: 'Döndür', icon: IC.rotate, prev: 'Tüm sayfalar 90°' }
    ];
    return [
      { id: 'cmp', label: 'Sıkıştır', icon: IC.compress, prev: 'Boyutu küçült' },
      { id: 'hide', label: 'Gizle', icon: IC.hide, radar: true, prev: 'Hassas alanlar kapatılır' },
      { id: 'lock', label: 'Şifrele', icon: IC.lock, prev: 'Parola ile kilitle' }
    ];
  }
  if (kind === 'pdf') return [
    { id: 'docx', label: 'Word', icon: IC.doc, prev: 'Düzen korunur' },
    { id: 'txt', label: 'Metin', icon: IC.text, prev: 'Yalnız metin, düz .txt' },
    { id: 'png', label: 'PNG', icon: IC.image, prev: 'Her sayfa bir görsel' },
    { id: 'jpg', label: 'JPG', icon: IC.image, prev: 'Her sayfa bir görsel' }
  ];
  if (kind === 'img') {
    const own = file.ext === 'jpeg' ? 'jpg' : file.ext;
    const P = { png: ['Kayıpsız', true], jpg: ['Kalite %92', true], bmp: ['Sıkıştırmasız', true], webp: ['Küçük ve kaliteli', false], pdf: ['Tek sayfa PDF', false] };
    return ['png', 'jpg', 'webp', 'pdf', 'bmp'].filter(e => e !== own).slice(0, 4).map(e => ({
      id: e, label: e === 'pdf' ? 'PDF' : (e === 'webp' ? 'WebP' : e.toUpperCase()), icon: e === 'pdf' ? IC.pdf : IC.image, prev: P[e][0], ready: P[e][1]
    }));
  }
  if (kind === 'doc') return ['pdf', 'txt', 'docx'].filter(e => e !== file.ext).map(e => ({
    id: e, label: e === 'docx' ? 'Word' : (e === 'txt' ? 'Metin' : 'PDF'), icon: e === 'pdf' ? IC.pdf : (e === 'txt' ? IC.text : IC.doc), prev: 'Biçim korunur'
  }));
  return [];
}

// ---------- state ----------
const S = { open: false, mode: 'convert', file: null, sens: [], acts: [], hover: -1, chain: [], seq: 0, busy: false };
let dwellTimer = 0;

const $ = id => document.getElementById(id);
const wheel = $('wheel'), slicesEl = $('slices'), labelsEl = $('labels'), lightEl = $('light');
const titleEl = $('center-title'), subEl = $('center-sub'), badgeEl = $('badge');

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
    g.className = 'slice'; g.style.animationDelay = (i * 30) + 'ms';
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
  if (h) {
    titleEl.textContent = h.label;
    subEl.textContent = h.ready ? h.prev : 'Yakında';
  } else if (S.chain.length) {
    titleEl.textContent = 'Zincir';
    subEl.textContent = S.chain.map(c => c.label).join(' → ');
  } else if (S.sens.length) {
    titleEl.textContent = 'Dikkat';
    subEl.textContent = S.sens.join(' · ');
    subEl.classList.add('radar-text');
  } else if (S.file && !S.acts.length) {
    titleEl.textContent = S.file.name;
    subEl.textContent = 'Bu dosya türü henüz yok';
  } else {
    titleEl.textContent = MODE_NAME[S.mode];
    subEl.textContent = S.file ? `${S.file.name}.${S.file.ext}` : 'Dosyayı bekliyor';
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
    dwellTimer = setTimeout(lockCurrent, DWELL_MS);
  }
  paint();
}

function lockCurrent() {
  if (S.hover < 0) return;
  const a = S.acts[S.hover];
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

function fit() { document.documentElement.style.setProperty('--fit', String(innerWidth / 340)); }
addEventListener('resize', fit);
fit();

// ---------- host messages ----------
const handlers = {
  backdrop(m) {
    $('glass').style.backgroundImage = m.src ? `url("${m.src}")` : 'none';
  },
  open(m) {
    Object.assign(S, { open: true, mode: m.mode || 'convert', file: null, sens: [], chain: [], busy: false });
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
    const changed = !S.file || !f || S.file.name !== f.name || S.file.ext !== f.ext;
    S.file = f;
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
    const steps = S.chain.map(c => c.id);
    if (steps[steps.length - 1] !== cur.id) steps.push(cur.id);
    S.busy = true;
    titleEl.textContent = 'Çalışıyor';
    subEl.textContent = steps.length > 1 ? `${steps.length} adım` : cur.label;
    post({ type: 'run', steps });
  },
  result(m) {
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
    subEl.textContent = m.detail || '';
    subEl.classList.remove('radar-text');
    if (m.ok) { sfx.flip(); setTimeout(sfx.done, 160); } else sfx.fail();
  },
  close() {
    S.open = false;
    clearTimeout(dwellTimer);
    wheel.classList.add('closing');
    setTimeout(() => { wheel.className = 'wheel hidden'; }, 170);
  }
};

if (window.chrome && chrome.webview) {
  chrome.webview.addEventListener('message', e => {
    const m = e.data;
    try { (handlers[m.type] || (() => {}))(m); }
    catch (err) { post({ type: 'log', text: String(err && err.stack || err) }); }
  });
}
