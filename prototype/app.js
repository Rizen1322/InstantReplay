/* Aura, прототип 2. Окно Windows 11 на рабочем столе, внутри рабочее место
   рекордера. Без зависимостей и сборки. Данные условные, но собраны из
   настоящих: игры, даты и размеры клипов из папки записей, параметры из
   settings.json. */
"use strict";

/* =====================================================================
   Помощники
   ===================================================================== */
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
function h(tag, attrs = {}, html) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v == null || v === false) continue;
    if (k === "class") e.className = v;
    else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
    else e.setAttribute(k, v === true ? "" : v);
  }
  if (html != null) e.innerHTML = html;
  return e;
}
const G = code => `<span class="g">&#x${code};</span>`;
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const reduced = matchMedia("(prefers-reduced-motion: reduce)").matches;
const MIB = 1048576;
const num = (v, d = 1) => v.toFixed(d).replace(".", ",");
function fmtSize(bytes) {
  const mb = bytes / MIB;
  if (mb < 10) return num(mb, 1) + " МБ";
  if (mb < 1024) return Math.round(mb) + " МБ";
  return num(mb / 1024, 2) + " ГБ";
}
const fmtTotal = bytes => bytes / MIB < 1024 ? num(bytes / MIB, 1) + " МБ" : num(bytes / MIB / 1024, 2) + " ГБ";
function fmtDur(s) {
  s = Math.max(0, Math.floor(s));
  const hh = Math.floor(s / 3600), mm = Math.floor(s / 60) % 60, ss = s % 60;
  return hh ? `${hh}:${String(mm).padStart(2, "0")}:${String(ss).padStart(2, "0")}` : `${mm}:${String(ss).padStart(2, "0")}`;
}
const MONTHS = ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];
const TODAY = new Date(2026, 8, 21);
const dayKey = d => `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;
function dayLabel(d) {
  const diff = Math.round((new Date(d.getFullYear(), d.getMonth(), d.getDate()) - TODAY) / 864e5);
  if (diff === 0) return "Сегодня";
  if (diff === -1) return "Вчера";
  return `${d.getDate()} ${MONTHS[d.getMonth()]}`;
}
const hhmm = d => `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
function plural(n, one, few, many) {
  const a = n % 10, b = n % 100;
  if (a === 1 && b !== 11) return one;
  if (a >= 2 && a <= 4 && (b < 10 || b >= 20)) return few;
  return many;
}
const kbds = combo => combo ? combo.split(/[ +]/).filter(Boolean).map(k => `<kbd>${k}</kbd>`).join("") : "";
function paintKeyhints(root = document) { $$(".keyhint[data-keys]", root).forEach(e => e.innerHTML = kbds(e.dataset.keys)); }
const hash = n => { const s = Math.sin(n * 127.1 + 311.7) * 43758.5453; return s - Math.floor(s); };
function vnoise(x) { const i = Math.floor(x), f = x - i, u = f * f * (3 - 2 * f); return hash(i) * (1 - u) + hash(i + 1) * u; }

/* =====================================================================
   Данные
   ===================================================================== */
const GAMES = { cs2: "Counter-Strike 2", iwbtc: "I Wanna Be The Co-op v1.69.6", dmc5: "Devil May Cry 5", dl: "Dying Light", gta: "Grand Theft Auto V", dbd: "Dead by Daylight", fc: "EA SPORTS FC 26" };
const FOLDER = { cs2: "Counter-strike 2", iwbtc: "I Wanna Be The Co-op v1.69.6", dmc5: "Devil May Cry 5", dl: "Dying Light", gta: "Grand Theft Auto V", dbd: "Dead by Daylight", fc: "EA SPORTS FC 26" };
let uid = 1;
function mkClip(o) {
  const c = Object.assign({ id: uid++, kind: "video", tracks: 2, codec: "HEVC", w: 2560, h: 1440, fps: 60, name: "" }, o);
  if (c.kind === "video" && !c.mbps) c.mbps = Math.round(c.bytes * 8 / c.dur / 1e6);
  return c;
}
let CLIPS = [
  mkClip({ game: "iwbtc", date: new Date(2026, 8, 20, 18, 8, 3), dur: 180.5, bytes: 411998170, f: 5, p: 2, mbps: 35 }),
  mkClip({ game: "iwbtc", date: new Date(2026, 8, 20, 18, 4, 38), dur: 180.1, bytes: 435339347, f: 6, p: 2, mbps: 35, name: "Третий экран без смертей" }),
  mkClip({ game: "iwbtc", date: new Date(2026, 8, 16, 21, 0, 27), dur: 180.2, bytes: 363120000, f: 6, p: 4, mbps: 35 }),
  mkClip({ game: "iwbtc", date: new Date(2026, 8, 13, 23, 57, 1), dur: 180.4, bytes: 402800000, f: 5, p: 0, mbps: 35 }),
  mkClip({ game: "iwbtc", date: new Date(2026, 8, 13, 20, 26, 20), dur: 180.3, bytes: 398100000, f: 6, p: 1, mbps: 35 }),
  mkClip({ game: "cs2", date: new Date(2026, 7, 1, 1, 43, 3), dur: 180.9, bytes: 944717013, f: 0, p: 2 }),
  mkClip({ game: "cs2", date: new Date(2026, 6, 23, 1, 45, 54), dur: 180.9, bytes: 1018815043, f: 1, p: 2 }),
  mkClip({ game: "cs2", date: new Date(2026, 6, 23, 0, 21, 22), dur: 180.7, bytes: 1023083161, f: 2, p: 2, name: "Эйс на B, ножом" }),
  mkClip({ game: "cs2", date: new Date(2026, 6, 23, 0, 14, 19), dur: 180.6, bytes: 1024709332, f: 3, p: 2 }),
  mkClip({ game: "cs2", date: new Date(2026, 6, 20, 16, 27, 25), dur: 30.2, bytes: 171296295, f: 4, p: 2 }),
  mkClip({ game: "dl", date: new Date(2026, 6, 20, 1, 57, 51), dur: 180.2, bytes: 569080613, f: 9, p: 3, codec: "H.264", tracks: 1 }),
  mkClip({ game: "dl", date: new Date(2026, 6, 18, 22, 10, 2), dur: 180.0, bytes: 817993466, f: 10, p: 2, codec: "H.264", tracks: 1 }),
  mkClip({ game: "gta", date: new Date(2026, 6, 15, 2, 42, 7), dur: 180.2, bytes: 1143643864, f: 11, p: 2, codec: "H.264", tracks: 1 }),
  mkClip({ game: "gta", date: new Date(2026, 6, 15, 1, 35, 13), dur: 111.1, bytes: 1216408474, f: 12, p: 2, codec: "H.264", tracks: 1 }),
  mkClip({ game: "fc", date: new Date(2026, 6, 13, 17, 48, 9), dur: 180.0, bytes: 1012011551, f: 14, p: 2, codec: "H.264", tracks: 1 }),
  mkClip({ game: "dbd", date: new Date(2026, 5, 23, 14, 32, 29), dur: 180.3, bytes: 832118990, f: 13, p: 2, codec: "H.264", tracks: 1 }),
  mkClip({ game: "dmc5", date: new Date(2026, 5, 1, 19, 36, 31), dur: 180.1, bytes: 562129874, f: 7, p: 2, codec: "H.264", tracks: 1 }),
  mkClip({ game: "dmc5", date: new Date(2026, 5, 1, 19, 29, 40), dur: 101.2, bytes: 310232781, f: 8, p: 3, codec: "H.264", tracks: 1 }),
  mkClip({ kind: "shot", game: "cs2", date: new Date(2026, 8, 21, 17, 44, 13), bytes: 3480000, tape: 9, w: 2148, h: 1261, area: true }),
  mkClip({ kind: "shot", game: "cs2", date: new Date(2026, 8, 21, 17, 15, 39), bytes: 5920000, tape: 14 }),
  mkClip({ kind: "shot", game: "iwbtc", date: new Date(2026, 8, 20, 18, 10, 2), bytes: 1310000, img: "media/clips/c5_1.jpg", w: 1060, h: 870, area: true }),
  mkClip({ kind: "shot", game: "iwbtc", date: new Date(2026, 8, 16, 21, 5, 44), bytes: 4870000, img: "media/clips/c6_1.jpg" }),
];
function frameSrc(c, k) {
  if (c.kind === "shot") return c.img || `media/tape/t${c.tape}.jpg`;
  if (c.tapeFrames) return `media/tape/t${c.tapeFrames[clamp(k, 0, 4)]}.jpg`;
  return `media/clips/c${c.f}_${clamp(k, 0, 4)}.jpg`;
}
const poster = c => frameSrc(c, c.p ?? 2);
const clipTitle = c => c.name || GAMES[c.game];
const defaultName = c => c.kind === "shot" ? (c.area ? "Скриншот области" : "Скриншот экрана") : c.recorded ? "Запись" : c.dur <= 31 ? "Повтор 30 секунд" : "Повтор";
function clipPath(c) {
  const d = c.date, p = n => String(n).padStart(2, "0");
  const base = `${GAMES[c.game]} ${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} - ${p(d.getHours())}-${p(d.getMinutes())}-${p(d.getSeconds())}`;
  return c.kind === "shot" ? `C:\\Users\\Rizen\\Documents\\Aura\\${base}.png` : `E:\\видео\\${FOLDER[c.game]}\\${base}.mp4`;
}
const byId = id => CLIPS.find(c => c.id === id);

/* параметры: значения из настоящего settings.json */
const S = {
  preset: "custom", res: 1440, fps: 60, bitrate: 35, codec: "HEVC", bits10: false, replayLen: 180,
  source: "auto", cursor: true,
  gameAudio: true, gameDevice: "realtek", mic: true, micDevice: "usb", tracks: "separate", noise: false, gate: -44,
  hk: { save: "F9", save30: "", toggle: "", mark: "", recStart: "", recStop: "", shot: "", area: "End", panel: "Alt+Q", folder: "" },
  saveRoot: "E:\\видео", shotRoot: "C:\\Users\\Rizen\\Documents\\Aura", byGame: true, template: "{game} {date} - {time}", attachMb: 20,
  notify: true, corner: "TR", notifySec: 2, sound: "soft",
  autostart: true, trayStart: true, autoReplay: true, theme: "dark", updates: true, driver: true,
};
const PRESETS = {
  eco: { res: 720, fps: 30, bitrate: 5, note: "720p, 30 к/с, 5 Мбит/с. Меньше всего места" },
  normal: { res: 1080, fps: 60, bitrate: 12, note: "1080p, 60 к/с, 12 Мбит/с. Для чатов и стримов" },
  high: { res: 1440, fps: 60, bitrate: 25, note: "1440p, 60 к/с, 25 Мбит/с. Чёткая картинка" },
  max: { res: 2160, fps: 60, bitrate: 48, note: "4K, 60 к/с, 48 Мбит/с. Для монтажа" },
};
const RES_W = { 720: 1280, 1080: 1920, 1440: 2560, 2160: 3840 };
const ramMb = () => S.bitrate * S.replayLen / 8 + S.replayLen * 0.78;
const clipMb = sec => sec * (S.bitrate + 0.32) / 8 * 1e6 / MIB;
const FREE = 394.58 * 1024 * MIB;

/* =====================================================================
   Состояние записи
   ===================================================================== */
const E = {
  t: 1000, speed: 1,
  replay: "on", fillStart: 600, errorStart: null,
  rec: false, recStart: 0, saving: false,
  sel: null,                          // { a, b|null }, b = null значит «до сейчас»
  markers: [], micMuted: false, gameMuted: false, muteSpans: [],
  get L() { return S.replayLen; },
};
E.markers.push({ type: "saved", a: E.t - 158, b: E.t - 128 }, { type: "mark", a: E.t - 71 }, { type: "shot", a: E.t - 34 });
const tapeImgs = Array.from({ length: 18 }, (_, i) => { const im = new Image(); im.src = `media/tape/t${i}.jpg`; return im; });
const frameIndex = k => 1 + (((k % 17) + 17) % 17);
const FR = () => (E.L / 18 <= 10 ? 10 : Math.ceil(E.L / 18 / 10) * 10);
function gameAmp(t) {
  if (E.gameMuted) return 0;
  let a = 0.14 + 0.2 * vnoise(t * 0.35) + 0.09 * vnoise(t * 3.1 + 7);
  if (vnoise(t * 0.045 + 3) < 0.3) a *= 0.45;
  const slot = Math.floor(t * 4);
  if (hash(slot * 7.31) > 0.9) { const k = 1 - (t * 4 - slot); a = Math.max(a, k * k * (0.55 + 0.42 * hash(slot + 0.5))); }
  return clamp(a, 0, 1);
}
function micAmp(t) {
  for (const s of E.muteSpans) if (t >= s.a && t <= (s.b ?? E.t)) return 0;
  const talk = clamp((vnoise(t * 0.21 + 50) - 0.5) * 9, 0, 1);
  const syll = 0.3 + 0.7 * Math.abs(Math.sin(t * 9.3 + 3 * vnoise(t * 2)));
  return clamp(talk * (0.22 + 0.6 * vnoise(t * 1.3 + 9)) * syll + 0.025 * hash(Math.floor(t * 20)), 0, 1);
}

/* =====================================================================
   Палитра для рисования
   ===================================================================== */
let PAL = {};
function readPalette() {
  const cs = getComputedStyle(document.documentElement);
  const v = n => cs.getPropertyValue(n).trim();
  PAL = { tx1: v("--tx1"), tx2: v("--tx2"), tx3: v("--tx3"), acc: v("--acc"), rec: v("--rec"), caution: v("--caution"), div: v("--stroke-div"), light: document.documentElement.dataset.theme === "light" };
  PAL.wave = PAL.light ? "rgba(0,0,0," : "rgba(255,255,255,";
}
function fitCanvas(cv) {
  const dpr = window.devicePixelRatio || 1, W = cv.clientWidth, H = cv.clientHeight;
  const w = Math.max(1, Math.round(W * dpr)), hh = Math.max(1, Math.round(H * dpr));
  if (cv.width !== w || cv.height !== hh) { cv.width = w; cv.height = hh; }
  const ctx = cv.getContext("2d"); ctx.setTransform(dpr, 0, 0, dpr, 0, 0); ctx.clearRect(0, 0, W, H);
  return { ctx, W, H };
}
function rrect(ctx, x, y, w, hh, r) {
  r = Math.min(r, w / 2, hh / 2);
  ctx.beginPath(); ctx.moveTo(x + r, y); ctx.arcTo(x + w, y, x + w, y + hh, r); ctx.arcTo(x + w, y + hh, x, y + hh, r); ctx.arcTo(x, y + hh, x, y, r); ctx.arcTo(x, y, x + w, y, r); ctx.closePath();
}
function cover(ctx, img, x, y, w, hh) {
  if (!img.complete || !img.naturalWidth) { ctx.fillStyle = PAL.div; ctx.fillRect(x, y, w, hh); return; }
  const ir = img.naturalWidth / img.naturalHeight, r = w / hh;
  let sw = img.naturalWidth, sh = img.naturalHeight, sx = 0, sy = 0;
  if (ir > r) { sw = sh * r; sx = (img.naturalWidth - sw) / 2; } else { sh = sw / r; sy = (img.naturalHeight - sh) / 2; }
  ctx.drawImage(img, sx, sy, sw, sh, x, y, w, hh);
}
function hatch(ctx, x, y, w, hh, color, alpha) {
  if (w <= 0) return;
  ctx.save(); ctx.beginPath(); ctx.rect(x, y, w, hh); ctx.clip();
  ctx.strokeStyle = color; ctx.globalAlpha = alpha; ctx.lineWidth = 1;
  for (let i = x - hh - 8; i < x + w; i += 7) { ctx.beginPath(); ctx.moveTo(i, y + hh); ctx.lineTo(i + hh, y); ctx.stroke(); }
  ctx.restore();
}

/* =====================================================================
   Таймлайн повтора: одна функция рисует и большой таймлайн, и мини-ленту
   в панели поверх игры
   ===================================================================== */
const TRACKS = { ruler: [0, 22], video: [22, 54], game: [76, 34], mic: [110, 34], marks: [144, 22] };
function drawTimeline(cv, mini) {
  const { ctx, W, H } = fitCanvas(cv);
  if (E.replay === "off") return;
  const now = E.t, L = E.L;
  const X = t => W - (now - t) / L * W;
  const start = E.fillStart ?? now;
  const xs = clamp(X(start), -2, W);
  const T = mini ? { video: [3, 30], game: [36, 17] } : TRACKS;
  const font = s => `${s}px "Segoe UI Variable Small", "Segoe UI", sans-serif`;

  if (!mini) {
    // разделители дорожек и шкала времени
    ctx.fillStyle = PAL.div;
    for (const k of ["video", "game", "mic", "marks"]) ctx.fillRect(0, T[k][0], W, 1);
    ctx.font = font(11);
    for (let s = 10; s < L; s += 10) {
      const x = Math.round(X(now - s)), major = s % 30 === 0;
      ctx.fillStyle = PAL.tx3; ctx.fillRect(x, major ? 12 : 17, 1, major ? 10 : 5);
      if (major && x > 40) { ctx.fillStyle = PAL.tx3; ctx.fillText("−" + fmtDur(s), x + 4, 13); }
    }
  }

  // кадры
  const fr = FR(), [vy, vh] = T.video;
  for (let k = Math.floor((now - L) / fr); k <= Math.floor(now / fr); k++) {
    const ta = k * fr, tb = ta + fr;
    if (tb <= start) continue;
    if (E.replay === "error" && ta >= E.errorStart) continue;
    const x0 = X(ta), fw = X(tb) - x0;
    let a = Math.max(x0, xs), b = Math.min(x0 + fw, W);
    if (E.replay === "error") b = Math.min(b, X(E.errorStart));
    if (b - a < 1) continue;
    ctx.save(); rrect(ctx, a + .5, vy + 3, b - a - 1, vh - 6, 3); ctx.clip();
    cover(ctx, tapeImgs[frameIndex(k)], x0 + .5, vy + 3, fw - 1, vh - 6);
    ctx.restore();
  }
  if (E.replay === "error") {
    const xa = Math.max(X(E.errorStart), xs);
    hatch(ctx, xa, vy + 3, W - xa, vh - 6, PAL.caution, .35);
    if (!mini && W - xa > 110) { ctx.fillStyle = PAL.caution; ctx.font = font(12); ctx.fillText("нет кадров", xa + 10, vy + vh / 2 + 4); }
  }

  // звук
  const lanes = mini ? [{ r: T.game, f: t => Math.max(gameAmp(t) * .85, micAmp(t)), a: .6 }] : [{ r: T.game, f: gameAmp, a: .55 }, { r: T.mic, f: micAmp, a: .55 }];
  const step = mini ? 2.5 : 3, dt = L / (W / step);
  const sel = E.sel ? { a: E.sel.a, b: E.sel.b ?? now } : null;
  for (const ln of lanes) {
    const cy = ln.r[0] + ln.r[1] / 2, amp = ln.r[1] / 2 - 4;
    for (let b = Math.floor(Math.max(now - L, start) / dt); b <= Math.floor(now / dt); b++) {
      const t = b * dt, x = X(t), a = ln.f(t), hh = Math.max(.75, a * amp);
      const inSel = sel && t >= sel.a && t <= sel.b;
      ctx.fillStyle = PAL.wave + (inSel ? .95 : ln.a) + ")";
      ctx.fillRect(x, cy - hh, mini ? 1.5 : 2, hh * 2);
    }
  }
  if (!mini) {
    for (const s of E.muteSpans) {
      const xa = clamp(X(Math.max(s.a, start)), 0, W), xb = clamp(X(s.b ?? now), 0, W);
      if (xb - xa < 2) continue;
      hatch(ctx, xa, T.mic[0] + 1, xb - xa, T.mic[1] - 1, PAL.rec, .3);
      if (xb - xa > 150) { ctx.fillStyle = PAL.tx3; ctx.font = font(11); ctx.fillText("микрофон выключен", xa + 8, T.mic[0] + 21); }
    }
  }

  // ещё не записано
  if (xs > 0) {
    hatch(ctx, 0, mini ? 0 : 22, xs, H, PAL.light ? "#000" : "#fff", PAL.light ? .06 : .05);
    if (!mini && xs > 130) { ctx.fillStyle = PAL.tx3; ctx.font = font(12); const t = "ещё не записано", w = ctx.measureText(t).width; ctx.fillText(t, xs / 2 - w / 2, vy + vh / 2 + 4); }
  }

  // метки: сохранённые куски, флажки, скриншоты, идущая запись
  if (!mini) {
    const [my, mh] = T.marks;
    for (const m of E.markers) {
      if (m.type === "saved") {
        const xa = clamp(X(m.a), 0, W), xb = clamp(X(m.b), 0, W);
        if (xb - xa < 2) continue;
        ctx.fillStyle = PAL.light ? "rgba(15,123,69,.18)" : "rgba(86,211,143,.22)";
        rrect(ctx, xa + .5, my + 4, xb - xa - 1, mh - 8, 3); ctx.fill();
        ctx.fillStyle = PAL.acc; ctx.fillRect(xa, my + 4, 2, mh - 8);
        if (xb - xa > 96) { ctx.fillStyle = PAL.tx1; ctx.font = font(11); ctx.fillText(`сохранено ${fmtDur(m.b - m.a)}`, xa + 7, my + 15); }
      } else {
        const x = X(m.a); if (x < 0 || x > W) continue;
        ctx.fillStyle = PAL.wave + ".28)"; ctx.fillRect(Math.round(x), 22, 1, my - 22);
        ctx.fillStyle = PAL.tx1; ctx.font = `12px "Segoe Fluent Icons"`;
        ctx.fillText(m.type === "mark" ? "\uE7C1" : "\uE722", x - 6, my + 16);
      }
    }
    if (E.rec) {
      const xa = clamp(X(E.recStart), 0, W);
      ctx.fillStyle = PAL.rec; rrect(ctx, xa, my + 4, W - xa, mh - 8, 3); ctx.fill();
      if (W - xa > 90) { ctx.fillStyle = "#fff"; ctx.font = font(11); ctx.fillText(`запись ${fmtDur(now - E.recStart)}`, xa + 7, my + 15); }
    }
  } else {
    for (const m of E.markers) if (m.type === "saved") { const xa = clamp(X(m.a), 0, W), xb = clamp(X(m.b), 0, W); if (xb - xa > 1) { ctx.fillStyle = PAL.acc; ctx.fillRect(xa, H - 3, xb - xa, 3); } }
  }

  // выделение
  if (sel) {
    const xa = clamp(X(sel.a), 0, W), xb = clamp(X(sel.b), 0, W), top = mini ? 0 : 22;
    ctx.fillStyle = PAL.light ? "rgba(0,0,0,.06)" : "rgba(255,255,255,.08)";
    ctx.fillRect(xa, top, xb - xa, H - top);
    ctx.fillStyle = PAL.tx1;
    ctx.fillRect(Math.round(xa) - 1, top, 2, H - top);
    if (E.sel.b != null) ctx.fillRect(Math.round(xb) - 1, top, 2, H - top);
    if (!mini) {
      ctx.fillStyle = PAL.tx1; rrect(ctx, xa - 5, 4, 10, 16, 2); ctx.fill();
      if (E.sel.b != null) { rrect(ctx, xb - 5, 4, 10, 16, 2); ctx.fill(); }
      const label = fmtDur(sel.b - sel.a); ctx.font = font(11);
      const lw = ctx.measureText(label).width + 12, lx = clamp((xa + xb) / 2 - lw / 2, xa + 8, Math.max(xa + 8, xb - lw - 8));
      if (xb - xa > lw + 20) { ctx.fillStyle = PAL.tx1; rrect(ctx, lx, 4, lw, 16, 3); ctx.fill(); ctx.fillStyle = PAL.light ? "#fff" : "#1c1c1c"; ctx.fillText(label, lx + 6, 16); }
    }
  }

  // старое уходит из памяти
  if (xs <= 0) {
    const g = ctx.createLinearGradient(0, 0, mini ? 24 : 56, 0);
    const bg = PAL.light ? "rgba(249,249,249," : "rgba(43,43,43,";
    g.addColorStop(0, bg + "1)"); g.addColorStop(1, bg + "0)");
    ctx.fillStyle = g; ctx.fillRect(0, mini ? 0 : 22, mini ? 24 : 56, H);
  }
}

/* =====================================================================
   Подсказки
   ===================================================================== */
const tipEl = $("#tooltip");
let tipTimer = 0, tipFor = null;
document.addEventListener("pointerover", e => {
  const t = e.target.closest("[data-tip]");
  if (t === tipFor) return;
  clearTimeout(tipTimer); tipEl.classList.remove("is-on"); tipFor = t;
  if (!t || (t.closest(".navpane.is-open") && t.classList.contains("nav-item") && !t.classList.contains("nav-toggle"))) return;
  tipTimer = setTimeout(() => {
    tipEl.innerHTML = `<span>${t.dataset.tip}</span>${t.dataset.kbd ? `<span class="keyhint">${kbds(t.dataset.kbd)}</span>` : ""}`;
    const r = t.getBoundingClientRect(), tr = tipEl.getBoundingClientRect();
    const side = t.closest(".navpane");
    let x = side ? r.right + 8 : clamp(r.left + r.width / 2 - tr.width / 2, 8, innerWidth - tr.width - 8);
    let y = side ? r.top + r.height / 2 - tr.height / 2 : r.bottom + 8;
    if (!side && y + tr.height > innerHeight - 8) y = r.top - tr.height - 8;
    tipEl.style.left = x + "px"; tipEl.style.top = y + "px"; tipEl.classList.add("is-on");
  }, 500);
});
document.addEventListener("pointerdown", () => { clearTimeout(tipTimer); tipEl.classList.remove("is-on"); }, true);

/* =====================================================================
   Меню (MenuFlyout)
   ===================================================================== */
const flyouts = $("#flyouts");
let MENU = null;
function closeMenu() {
  if (!MENU) return;
  const { el, anchor } = MENU; MENU = null;
  anchor?.classList.remove("is-open");
  el.classList.add("is-leaving"); setTimeout(() => el.remove(), 90);
}
/* items: {label, glyph, key, check, danger, disabled, fn, sel} | "sep" | {head} */
function openMenu(items, { x, y, anchor, minWidth, above } = {}) {
  closeMenu();
  const el = h("div", { class: "menu", role: "menu" });
  if (minWidth) el.style.minWidth = minWidth + "px";
  const act = [];
  const hasChecks = items.some(i => i && i.check !== undefined);
  for (const it of items) {
    if (it === "sep") { el.appendChild(h("div", { class: "mi-sep" })); continue; }
    if (it.head) { el.appendChild(h("div", { class: "mi-head" }, it.head)); continue; }
    const b = h("button", { class: "mi" + (it.danger ? " is-danger" : "") + (it.sel !== undefined ? " mi-sel" + (it.sel ? " is-sel" : "") : ""), role: "menuitem", "aria-disabled": it.disabled ? "true" : null },
      `${hasChecks ? `<span class="mi-chk g">${it.check ? "&#xE73E;" : ""}</span>` : ""}${it.glyph ? G(it.glyph) : ""}<span class="mi-l">${it.label}</span>${it.key ? `<span class="mi-k">${it.key}</span>` : ""}`);
    b.addEventListener("click", () => { closeMenu(); it.fn?.(); });
    b.addEventListener("pointerenter", () => hot(act.indexOf(b)));
    el.appendChild(b); if (!it.disabled) act.push(b);
  }
  flyouts.appendChild(el);
  const r = el.getBoundingClientRect();
  if (anchor) {
    const ar = anchor.getBoundingClientRect();
    x = ar.left; y = ar.bottom + 4; anchor.classList.add("is-open");
    if (above || y + r.height > innerHeight - 56) { y = ar.top - r.height - 4; el.classList.add("up"); }
  }
  el.style.left = clamp(x, 4, innerWidth - r.width - 4) + "px";
  el.style.top = clamp(y, 4, innerHeight - r.height - 4) + "px";
  let idx = -1;
  function hot(i) { act.forEach((b, j) => b.classList.toggle("is-hot", j === i)); idx = i; }
  MENU = { el, anchor, key(e) {
    if (e.key === "ArrowDown") { hot((idx + 1) % act.length); return true; }
    if (e.key === "ArrowUp") { hot((idx - 1 + act.length) % act.length); return true; }
    if (e.key === "Enter" && idx >= 0) { act[idx].click(); return true; }
    if (e.key === "Escape") { closeMenu(); return true; }
    return false;
  } };
}
document.addEventListener("pointerdown", e => { if (MENU && !MENU.el.contains(e.target) && !MENU.anchor?.contains(e.target)) closeMenu(); }, true);
addEventListener("resize", closeMenu);

function combo(btn, { options, get, set, width }) {
  const paint = () => { const o = options().find(o => o.value === get()); btn.innerHTML = `<span class="combo-v">${o ? o.short || o.label : ""}</span><span class="g">&#xE70D;</span>`; };
  btn.onclick = () => {
    if (MENU?.anchor === btn) { closeMenu(); return; }
    openMenu(options().map(o => ({ label: o.label + (o.hint ? `<span style="color:var(--tx3);margin-left:8px">${o.hint}</span>` : ""), sel: o.value === get(), disabled: o.disabled, fn: () => { set(o.value); paint(); } })), { anchor: btn, minWidth: Math.max(width || 0, btn.offsetWidth) });
  };
  paint(); return paint;
}
function toggle(btn, { get, set }) {
  const paint = () => btn.setAttribute("aria-checked", get() ? "true" : "false");
  btn.onclick = () => { set(!get()); paint(); };
  paint(); return paint;
}
function segmented(el, { options, get, set }) {
  el.innerHTML = ""; const thumb = h("span", { class: "seg-thumb" }); el.appendChild(thumb);
  const items = options.map(o => { const b = h("button", {}, o.label); b.onclick = () => { set(o.value); paint(true); }; el.appendChild(b); return b; });
  function paint(anim) {
    const i = options.findIndex(o => o.value === get());
    items.forEach((b, j) => b.classList.toggle("is-on", j === i));
    const b = items[i]; if (!b) { thumb.style.opacity = 0; return; }
    if (!anim) thumb.style.transition = "none";
    thumb.style.opacity = 1; thumb.style.width = b.offsetWidth + "px"; thumb.style.transform = `translateX(${b.offsetLeft - 2}px)`;
    if (!anim) { thumb.offsetWidth; thumb.style.transition = ""; }
  }
  requestAnimationFrame(() => paint(false)); el._paint = paint; return paint;
}

/* =====================================================================
   Уведомления: внутри окна, Windows (когда окно спрятано) и в игре
   ===================================================================== */
const winEl = $("#win");
const W = { x: 0, y: 0, w: 1240, h: 780, max: false, min: false, hidden: false, active: true };
const windowVisible = () => !W.min && !W.hidden && !GAME.on;
function notify({ thumb, glyph, neutral, title, sub, actions = [], ms = 4200 }) {
  if (GAME.on) return gameToast({ thumb, title, sub });
  if (!windowVisible()) return winToast({ thumb, title, sub, actions });
  const host = $("#app-toasts");
  const el = h("div", { class: "toast", role: "status" }, `${thumb ? `<img src="${thumb}" alt="">` : `<span class="t-ico${neutral ? " neutral" : ""}">${G(glyph || "E73E")}</span>`}<div class="t-txt"><div class="t-tt">${title}</div>${sub ? `<div class="t-sub">${sub}</div>` : ""}</div>`);
  actions.forEach(a => { const b = h("button", { class: "btn btn-sm" }, a.label); b.onclick = () => { a.fn(); out(); }; el.appendChild(b); });
  host.appendChild(el);
  let tm = setTimeout(out, ms);
  el.onpointerenter = () => clearTimeout(tm); el.onpointerleave = () => { tm = setTimeout(out, 1500); };
  function out() { if (!el.isConnected) return; el.classList.add("is-out"); setTimeout(() => el.remove(), 170); }
  while (host.children.length > 3) host.firstElementChild.remove();
}
const MARK_SVG = `<svg viewBox="0 0 24 24"><circle class="mark-dot" cx="12" cy="12" r="3.6"/><path class="mark-arc" d="M12 3.6a8.4 8.4 0 0 1 0 16.8"/><path class="mark-arc" d="M12 6.9a5.1 5.1 0 0 1 0 10.2"/></svg>`;
function winToast({ thumb, title, sub, actions = [] }) {
  const host = $("#win-toasts");
  const el = h("div", { class: "wtoast" }, `<div class="wt-app">${MARK_SVG}Aura</div><div class="wt-body">${thumb ? `<img src="${thumb}" alt="">` : ""}<div><div class="wt-tt">${title}</div><div class="wt-sub">${sub || ""}</div></div></div>`);
  if (actions.length) { const a = h("div", { class: "wt-acts" }); actions.forEach(x => { const b = h("button", {}, x.label); b.onclick = () => { x.fn(); out(); }; a.appendChild(b); }); el.appendChild(a); }
  host.appendChild(el);
  const tm = setTimeout(out, 5200);
  function out() { clearTimeout(tm); if (!el.isConnected) return; el.classList.add("is-out"); setTimeout(() => el.remove(), 170); }
}
function gameToast({ thumb, title, sub }) {
  if (!S.notify) return;
  const host = $("#game-toasts");
  // при открытой панели уведомление встаёт прямо под ней, а не в угол к интерфейсу игры
  const qp = $("#qp"), open = qp.classList.contains("is-on");
  if (!host.getAttribute("style") || open) host.style.top = open ? (qp.offsetTop + qp.offsetHeight + 8) + "px" : "";
  const ms = S.notifySec * 1000;
  const el = h("div", { class: "gtoast" }, `${thumb ? `<img src="${thumb}" alt="">` : ""}<div><div class="t-tt">${title}</div><div class="t-sub">${sub || ""}</div></div><div class="gt-bar"><i style="--ms:${ms}ms"></i></div>`);
  host.appendChild(el);
  setTimeout(() => { el.classList.add("is-out"); setTimeout(() => el.remove(), 170); }, ms);
}

/* =====================================================================
   Окно: перемещение, размер, развернуть, свернуть, трей
   ===================================================================== */
function defaultRect() {
  const w = Math.min(1240, innerWidth - 80), hh = Math.min(780, innerHeight - 48 - 56);
  return { x: Math.round((innerWidth - w) / 2), y: Math.max(12, Math.round((innerHeight - 48 - hh) / 2)), w, h: hh };
}
Object.assign(W, defaultRect());
function place() {
  const s = winEl.style;
  if (W.max) { s.left = "0px"; s.top = "0px"; s.width = innerWidth + "px"; s.height = (innerHeight - 48) + "px"; }
  else { s.left = W.x + "px"; s.top = W.y + "px"; s.width = W.w + "px"; s.height = W.h + "px"; }
  winEl.classList.toggle("is-max", W.max);
  $("#cap-max-g").innerHTML = W.max ? "&#xE923;" : "&#xE922;";
  $("#cap-max").setAttribute("aria-label", W.max ? "Восстановить" : "Развернуть");
  requestAnimationFrame(layoutChanged);
}
function setActive(on) { W.active = on; winEl.classList.toggle("is-active", on); $("#tb-aura").classList.toggle("is-focused", on && windowVisible()); }
function toggleMax() { W.max = !W.max; place(); }
function minimize() { W.min = true; winEl.classList.add("is-min"); setActive(false); }
function restore() { W.min = false; W.hidden = false; winEl.classList.remove("is-min", "is-hidden"); $("#tb-aura").hidden = false; setActive(true); layoutChanged(); }
let trayHintShown = false;
function hideToTray() {
  W.hidden = true; winEl.classList.add("is-hidden"); setActive(false);
  setTimeout(() => { $("#tb-aura").hidden = true; }, 200);
  if (!trayHintShown) { trayHintShown = true; setTimeout(() => winToast({ title: "Aura работает в фоне", sub: `Повтор пишется дальше. ${S.hk.save} сохранит последние ${fmtDur(E.L)}, значок Aura в трее рядом с часами.` }), 260); }
}
$("#cap-max").onclick = toggleMax;
$("#cap-min").onclick = minimize;
$("#cap-close").onclick = hideToTray;
$("#tb-aura").onclick = () => { if (W.min) restore(); else if (W.active) minimize(); else setActive(true); };
$("#tray-aura").onclick = () => { if (GAME.on) leaveGame(); if (W.min || W.hidden) restore(); else setActive(true); };
$("#tray-aura").oncontextmenu = e => { e.preventDefault(); trayMenu(e.currentTarget); };
$("#wallpaper").addEventListener("pointerdown", () => { setActive(false); });
winEl.addEventListener("pointerdown", () => { if (!W.active) setActive(true); });

$("#titlebar").addEventListener("pointerdown", e => {
  if (e.button !== 0 || e.target.closest("button")) return;
  const sx = e.clientX, sy = e.clientY, ox = W.x, oy = W.y;
  let moved = false;
  const move = ev => {
    const dx = ev.clientX - sx, dy = ev.clientY - sy;
    if (!moved && Math.hypot(dx, dy) < 4) return;
    if (!moved) { moved = true; winEl.classList.add("is-moving"); if (W.max) { const fx = sx / innerWidth; W.max = false; W.x = Math.round(ev.clientX - W.w * fx); W.y = 0; place(); } }
    if (!W.max) { W.x = (W.max ? W.x : ox) + dx; W.y = clamp(oy + dy, 0, innerHeight - 48 - 48); if (ox !== W.x - dx) W.x = W.x; place(); }
  };
  const up = ev => { removeEventListener("pointermove", move); removeEventListener("pointerup", up); winEl.classList.remove("is-moving"); if (moved && ev.clientY <= 2) { W.max = true; place(); } };
  addEventListener("pointermove", move); addEventListener("pointerup", up);
});
$("#titlebar").addEventListener("dblclick", e => { if (!e.target.closest("button")) toggleMax(); });
$$(".rz").forEach(rz => rz.addEventListener("pointerdown", e => {
  e.preventDefault(); e.stopPropagation();
  const d = rz.dataset.rz, sx = e.clientX, sy = e.clientY, o = { ...W };
  winEl.classList.add("is-moving");
  const move = ev => {
    const dx = ev.clientX - sx, dy = ev.clientY - sy;
    if (d.includes("e")) W.w = Math.max(900, o.w + dx);
    if (d.includes("s")) W.h = Math.max(600, o.h + dy);
    if (d.includes("w")) { W.w = Math.max(900, o.w - dx); W.x = o.x + o.w - W.w; }
    if (d.includes("n")) { W.h = Math.max(600, o.h - dy); W.y = o.y + o.h - W.h; }
    place();
  };
  const up = () => { removeEventListener("pointermove", move); removeEventListener("pointerup", up); winEl.classList.remove("is-moving"); };
  addEventListener("pointermove", move); addEventListener("pointerup", up);
}));
addEventListener("resize", () => { if (!W.max) { W.w = Math.min(W.w, innerWidth - 20); W.h = Math.min(W.h, innerHeight - 60); W.x = clamp(W.x, 0, innerWidth - 120); W.y = clamp(W.y, 0, innerHeight - 100); } place(); });

function trayMenu(anchor) {
  const full = E.fillStart != null && E.t - E.fillStart >= E.L;
  openMenu([
    { head: E.replay === "off" ? "Повтор выключен" : E.rec ? `Идёт запись ${fmtDur(E.t - E.recStart)}` : `Повтор в памяти: ${fmtDur(Math.min(E.L, E.t - (E.fillStart ?? E.t)))}${full ? "" : " из " + fmtDur(E.L)}` },
    { label: "Сохранить повтор", glyph: "E74E", key: S.hk.save.replace("+", " "), disabled: E.replay !== "on", fn: saveReplay },
    { label: "Мгновенный повтор", glyph: "E81C", check: E.replay !== "off", fn: () => E.replay === "off" ? startReplay() : stopReplay() },
    { label: E.rec ? "Остановить запись" : "Начать запись", glyph: E.rec ? "E71A" : "E7C8", fn: toggleRec },
    { label: "Скриншот области", glyph: "E7A8", key: S.hk.area, fn: screenshot },
    "sep",
    { label: "Открыть Aura", glyph: "E8A7", fn: () => { if (GAME.on) leaveGame(); restore(); } },
    { label: "Папка записей", glyph: "E838", fn: () => notify({ glyph: "E838", neutral: true, title: "Открыта папка записей", sub: S.saveRoot }) },
    "sep",
    { label: "Выход", glyph: "E7E8", fn: () => notify({ glyph: "E7E8", neutral: true, title: "В прототипе Aura не закрывается", sub: "В приложении этот пункт остановит повтор и выйдет" }) },
  ], { anchor, minWidth: 260, above: true });
}

/* =====================================================================
   Навигация
   ===================================================================== */
let view = "capture", navWasOpen = false;
function setView(v) {
  if (v !== view) { const n = $(`.view[data-view="${v}"]`); n.classList.remove("is-enter"); void n.offsetWidth; n.classList.add("is-enter"); }
  // у настроек своя колонка разделов, поэтому главное меню на это время сжимается до значков
  const navEl = $("#navpane");
  if (v === "settings" && view !== "settings") { navWasOpen = navEl.classList.contains("is-open"); navEl.classList.remove("is-open"); }
  else if (v !== "settings" && view === "settings" && navWasOpen) navEl.classList.add("is-open");
  view = v; winEl.dataset.view = v;
  $$(".view").forEach(x => x.classList.toggle("is-on", x.dataset.view === v));
  $$(".nav-item[data-view]").forEach(b => b.classList.toggle("is-on", b.dataset.view === v));
  movePill();
  if (v === "clips") { $("#nav-badge").classList.remove("is-on"); newCount = 0; renderClips(); }
  if (v === "settings") renderSettings();
  if (v === "capture") { renderInspector(); layoutChanged(); }
  closeMenu();
}
function movePill() {
  const pill = $("#nav-pill"), b = $(`#nav-list .nav-item[data-view="${view}"]`);
  const foot = $(`.nav-foot .nav-item[data-view="${view}"]`);
  const target = b || foot, list = $("#nav-list");
  const y = target.getBoundingClientRect().top - list.getBoundingClientRect().top + (36 - 16) / 2;
  pill.style.setProperty("--y", y + "px");
}
$$(".nav-item[data-view]").forEach(b => b.onclick = () => setView(b.dataset.view));
$("#nav-toggle").onclick = () => { $("#navpane").classList.toggle("is-open"); $("#nav-toggle").dataset.tip = $("#navpane").classList.contains("is-open") ? "Свернуть меню" : "Развернуть меню"; setTimeout(layoutChanged, 260); };
$("#ts-state").onclick = () => setView("capture");
let newCount = 0;

/* =====================================================================
   ЗАХВАТ
   ===================================================================== */
const tgl = $("#replay-tgl");
function paintState() {
  document.body.dataset.replay = E.replay;
  document.body.dataset.rec = E.rec ? "on" : "off";
  tgl.setAttribute("aria-checked", E.replay !== "off" ? "true" : "false");
  const filled = E.fillStart == null ? 0 : Math.min(E.L, E.t - E.fillStart), full = filled >= E.L - .05;
  let title, sub;
  if (E.replay === "off") { title = "Повтор выключен"; sub = "Aura ничего не держит в памяти"; }
  else if (E.replay === "error") { title = "Захват прервался"; sub = "Игра сменила режим экрана. Переподключаюсь, звук пишется дальше"; }
  else if (!full) { title = "Повтор набирается"; sub = `${fmtDur(filled)} из ${fmtDur(E.L)}. Сохранить можно уже сейчас`; }
  else { title = "Повтор идёт"; sub = S.source === "auto" ? `Counter-Strike 2, окно игры. В памяти последние ${fmtDur(E.L)}` : "Монитор 1, весь экран"; }
  $("#st-title").textContent = title; $("#st-sub").textContent = sub;
  const recT = fmtDur(E.t - E.recStart);
  $("#rec-time").textContent = recT;
  $("#btn-rec-label").textContent = E.rec ? `Остановить` : "Запись";
  $("#mon-buf").textContent = E.replay === "error" ? "нет кадров" : full ? `${fmtDur(filled)} в памяти` : `${fmtDur(filled)} из ${fmtDur(E.L)}`;
  $("#tl-caption").textContent = E.replay === "off" ? "Выключен" : `Последние ${fmtDur(E.L)}. Протяните по дорожкам, чтобы сохранить только кусок`;
  // заголовок окна
  $("#ts-label").textContent = E.rec ? "Запись" : E.replay === "off" ? "Повтор выключен" : E.replay === "error" ? "Нет кадров" : full ? "Повтор" : "Набирается";
  $("#ts-label").style.color = E.rec ? "var(--rec)" : "";
  $("#ts-time").textContent = E.rec ? recT : E.replay === "off" ? "" : fmtDur(filled);
  $("#ts-save-label").textContent = E.replay === "off" ? "Включить" : "Сохранить";
  $("#tray-aura").dataset.tip = E.replay === "off" ? "Aura: повтор выключен" : E.rec ? `Aura: запись ${recT}` : `Aura: повтор, ${fmtDur(filled)} в памяти`;
  // панель в игре
  $("#qp-title").textContent = E.rec ? "Идёт запись" : title;
  $("#qp-time").textContent = E.rec ? recT : E.replay === "off" ? "" : fmtDur(filled);
  paintQpTiles();
  paintSaveButtons();
}
function paintSaveButtons() {
  const off = E.replay === "off";
  const part = E.sel ? fmtDur((E.sel.b ?? E.t) - E.sel.a) : null;
  $$(".btn-save").forEach(b => {
    if (b.classList.contains("is-saving") || b.classList.contains("is-done")) return;
    const main = b.id === "btn-save";
    b.classList.toggle("is-part", !!part && main);
    $(".save-g", b).innerHTML = off ? "&#xE768;" : "&#xE74E;";
    $(".keyhint", b).style.display = off ? "none" : "";
    $(".save-label", b).textContent = off ? "Включить повтор" : part && main ? `Сохранить ${part}` : "Сохранить повтор";
  });
  $("#tl-sel").hidden = !E.sel;
  if (E.sel) $("#tl-sel-text").textContent = E.sel.b == null ? `Выделено ${part}, до «сейчас»` : `Выделено ${part}`;
}
tgl.onclick = () => {
  if (E.replay === "off") { startReplay(); return; }
  const filled = Math.min(E.L, E.t - E.fillStart);
  if (filled < 20) { stopReplay(); return; }
  openMenu([
    { head: `В памяти ${fmtDur(filled)}. После выключения они пропадут.` },
    { label: "Сохранить и выключить", glyph: "E74E", fn: () => { saveReplay(); setTimeout(stopReplay, 1400); } },
    { label: "Выключить без сохранения", glyph: "E7E8", fn: stopReplay },
  ], { anchor: tgl, minWidth: 300 });
};
function startReplay() { E.replay = "on"; E.fillStart = E.t; E.errorStart = null; E.sel = null; paintState(); renderInspector(); }
function stopReplay() { E.replay = "off"; E.fillStart = null; E.sel = null; E.markers = []; E.muteSpans = []; paintState(); renderInspector(); }

/* монитор: последний кадр, сменяется плавно */
let monFlip = false, monLast = -1;
function paintMonitor(force) {
  if (E.replay === "off") return;
  const k = frameIndex(Math.floor(E.t / 4));
  if (k === monLast && !force) return;
  monLast = k;
  const a = $("#mon-a"), b = $("#mon-b"), next = monFlip ? a : b, prev = monFlip ? b : a;
  next.src = `media/tape-hd/t${k}.jpg`;
  next.onload = () => { next.classList.add("is-on"); prev.classList.remove("is-on"); };
  monFlip = !monFlip;
}

/* инспектор справа от монитора */
function renderInspector() {
  const off = E.replay === "off", err = E.replay === "error";
  const last = CLIPS.filter(c => c.kind === "video").sort((a, b) => b.date - a.date)[0];
  const hours = FREE / (clipMb(3600) * MIB);
  $("#inspector").innerHTML = `
    <div class="ins-sec">
      <div class="ins-h">Источник<button class="btn btn-subtle" id="ins-src">Изменить</button></div>
      <div class="ins-row">${G("E7FC")}<div class="ins-l"><div class="ins-name">${S.source === "auto" ? "Counter-Strike 2" : "Монитор 1"}</div><div class="ins-sub">${S.source === "auto" ? "Окно игры, найдено само" : "Весь экран"}, 2560×1440</div></div></div>
      <div class="ins-row">${G("E7F4")}<div class="ins-l"><div class="ins-name">Видео</div><div class="ins-sub">${off ? "Захват не идёт" : err ? "Кадры не приходят 5 с" : "Без пропусков кадров"}</div></div><span class="ins-v${err ? " warn" : ""}">${off ? "0 к/с" : err ? "0 к/с" : `${S.fps} к/с`}</span></div>
    </div>
    <div class="ins-sec">
      <div class="ins-h">Звук</div>
      <div class="ins-row">${G("E767")}<div class="ins-l"><div class="ins-name">Игра</div><div class="ins-sub">Динамики (Realtek(R) Audio)</div></div><span class="meter${E.gameMuted ? " is-muted" : ""}" id="m-game"><i></i></span><button class="mute-btn${E.gameMuted ? " is-off" : ""}" id="ins-mute-game" data-tip="${E.gameMuted ? "Включить звук игры" : "Не писать звук игры"}">${G(E.gameMuted ? "E74F" : "E767")}</button></div>
      <div class="ins-row">${G("E720")}<div class="ins-l"><div class="ins-name">Микрофон</div><div class="ins-sub">${E.micMuted ? "Выключен, в запись не попадает" : "Микрофон (USB Audio Device)"}</div></div><span class="meter${E.micMuted ? " is-muted" : ""}" id="m-mic"><i></i></span><button class="mute-btn${E.micMuted ? " is-off" : ""}" id="ins-mute-mic" data-tip="${E.micMuted ? "Включить микрофон" : "Выключить микрофон"}">${G(E.micMuted ? "EC54" : "E720")}</button></div>
    </div>
    <div class="ins-sec">
      <div class="ins-h">Качество<button class="btn btn-subtle" id="ins-quality">Настроить</button></div>
      <dl class="ins-quality"><dt>Картинка</dt><dd>${RES_W[S.res]}×${S.res}, ${S.fps} к/с</dd><dt>Кодек</dt><dd>${S.codec}, ${S.bitrate} Мбит/с</dd><dt>Повтор</dt><dd><button class="combo" id="ins-len" style="height:28px;min-width:0"></button></dd></dl>
    </div>
    <div class="ins-sec">
      <div class="ins-h">Место</div>
      <div class="ins-row">${G("EDA2")}<div class="ins-l"><div class="ins-name">Диск E:</div><div class="ins-sub">Хватит примерно на ${Math.round(hours)} ч записи</div></div><span class="ins-v">394,6 ГБ</span></div>
      <div class="ins-row">${G("E9F5")}<div class="ins-l"><div class="ins-name">Оперативная память</div><div class="ins-sub">${off ? "Свободна, повтор выключен" : `Держит повтор ${fmtDur(E.L)}`}</div></div><span class="ins-v">${off ? "0 МБ" : Math.round(ramMb()) + " МБ"}</span></div>
    </div>
    ${last ? `<div class="ins-sec"><div class="ins-h">Последний клип<button class="btn btn-subtle" id="ins-all">Все клипы</button></div>
      <button class="ins-last" id="ins-last"><img src="${poster(last)}" alt=""><div class="ins-l" style="min-width:0"><div class="ins-name">${clipTitle(last)}</div><div class="ins-sub">${dayLabel(last.date)}, ${hhmm(last.date)}, ${fmtDur(last.dur)}</div></div></button></div>` : ""}`;
  $("#ins-src").onclick = e => openMenu([{ label: "Игра, иначе монитор 1", sel: S.source === "auto", fn: () => { S.source = "auto"; renderInspector(); paintState(); } }, { label: "Всегда монитор 1", sel: S.source === "mon", fn: () => { S.source = "mon"; renderInspector(); paintState(); } }], { anchor: e.currentTarget, minWidth: 240 });
  $("#ins-quality").onclick = () => { setView("settings"); selectPane("capture"); };
  $("#ins-mute-game").onclick = () => { E.gameMuted = !E.gameMuted; renderInspector(); syncTrackMutes(); };
  $("#ins-mute-mic").onclick = toggleMic;
  combo($("#ins-len"), { options: () => [[30, "30 секунд"], [60, "1 минута"], [180, "3 минуты"], [300, "5 минут"], [600, "10 минут"], [900, "15 минут"]].map(([value, label]) => ({ value, label, short: fmtDur(value) })), get: () => S.replayLen, set: v => { S.replayLen = v; E.sel = null; paintState(); renderInspector(); notify({ glyph: "E81C", neutral: true, title: `Повтор: последние ${fmtDur(v)}`, sub: `В памяти займёт ≈ ${Math.round(ramMb())} МБ` }); } });
  if (last) { $("#ins-last").onclick = () => { setView("clips"); selectOnly(last.id); }; $("#ins-all").onclick = () => setView("clips"); }
}
function toggleMic() {
  E.micMuted = !E.micMuted;
  if (E.micMuted) E.muteSpans.push({ a: E.t, b: null }); else { const s = E.muteSpans.at(-1); if (s && s.b == null) s.b = E.t; }
  if (view === "capture") renderInspector();
  syncTrackMutes(); paintQpTiles();
}
function syncTrackMutes() {
  const g = $("#mute-game"), m = $("#mute-mic");
  g.classList.toggle("is-off", E.gameMuted); g.innerHTML = G(E.gameMuted ? "E74F" : "E767"); g.dataset.tip = E.gameMuted ? "Включить звук игры" : "Не писать звук игры";
  m.classList.toggle("is-off", E.micMuted); m.innerHTML = G(E.micMuted ? "EC54" : "E720"); m.dataset.tip = E.micMuted ? "Включить микрофон" : "Выключить микрофон";
}
$("#mute-game").onclick = () => { E.gameMuted = !E.gameMuted; syncTrackMutes(); renderInspector(); };
$("#mute-mic").onclick = toggleMic;
function paintMeters() {
  const g = $("#m-game i"), m = $("#m-mic i");
  if (!g) return;
  const ga = E.replay === "off" ? 0 : gameAmp(E.t), ma = E.micMuted ? 0 : micAmp(E.t);
  g.style.width = ga * 100 + "%"; g.parentElement.classList.toggle("hot", ga > .85);
  m.style.width = ma * 100 + "%"; m.parentElement.classList.toggle("hot", ma > .85);
}

/* сохранение повтора */
function currentRange() {
  const start = Math.max(E.fillStart, E.t - E.L);
  return E.sel ? { a: Math.max(E.sel.a, start), b: Math.min(E.sel.b ?? E.t, E.t) } : { a: start, b: E.t };
}
function saveReplay() {
  if (E.saving) return;
  if (E.replay === "off") { startReplay(); return; }
  if (E.replay !== "on") { notify({ glyph: "E7BA", neutral: true, title: "Сейчас нечего сохранять", sub: "Захват переподключается, кадров пока нет" }); return; }
  const { a, b } = currentRange(), dur = b - a;
  if (dur < 1) return;
  E.saving = true;
  const ms = reduced ? 300 : Math.round(500 + dur * 3.4);
  const btns = $$(".btn-save");
  btns.forEach(bt => { bt.style.setProperty("--save-ms", ms + "ms"); bt.classList.remove("is-done", "is-part"); bt.classList.add("is-saving"); $(".save-label", bt).textContent = "Сохраняю…"; });
  $("#ts-save-label").textContent = "Сохраняю…";
  const wasPart = !!E.sel; E.sel = null; paintSaveButtons();
  E.markers.push({ type: "saved", a, b });
  setTimeout(() => {
    E.saving = false;
    const k0 = Math.floor(a / 10), k1 = Math.floor(b / 10);
    const frames = [0, 1, 2, 3, 4].map(i => frameIndex(Math.round(k0 + (k1 - k0) * (i + .5) / 5)));
    const clip = mkClip({ game: "cs2", date: new Date(2026, 8, 21, 18, 40 + (uid % 15), uid % 60), dur, bytes: clipMb(dur) * MIB, tapeFrames: frames, p: 4, mbps: S.bitrate, codec: S.codec, tracks: S.mic && S.tracks === "separate" ? 2 : 1, isNew: true });
    CLIPS.unshift(clip);
    btns.forEach(bt => { bt.classList.remove("is-saving"); bt.classList.add("is-done"); $(".save-label", bt).textContent = `Сохранено ${fmtDur(dur)}`; $(".save-g", bt).innerHTML = "&#xE73E;"; });
    $("#ts-save-label").textContent = "Сохранено";
    setTimeout(() => { btns.forEach(bt => bt.classList.remove("is-done")); paintState(); }, 1500);
    if (view !== "clips") { newCount++; const nb = $("#nav-badge"); nb.textContent = newCount; nb.classList.add("is-on"); }
    notify({ thumb: poster(clip), title: wasPart ? "Фрагмент сохранён" : "Повтор сохранён", sub: `${GAMES.cs2}, ${fmtDur(dur)}, ${fmtSize(clip.bytes)}`, actions: [{ label: "Открыть", fn: () => { if (!windowVisible()) restore(); setView("clips"); selectOnly(clip.id); } }] });
    if (view === "clips") renderClips(); else if (view === "capture") renderInspector();
  }, ms);
}
$("#btn-save").onclick = saveReplay;
$("#qp-save").onclick = saveReplay;
$("#ts-save").onclick = saveReplay;
$("#tl-sel-save").onclick = saveReplay;
$("#tl-sel-clear").onclick = () => { E.sel = null; paintSaveButtons(); };

function toggleRec() {
  if (!E.rec) { E.rec = true; E.recStart = E.t; paintState(); return; }
  const dur = E.t - E.recStart;
  E.rec = false; paintState();
  const clip = mkClip({ game: "cs2", date: new Date(2026, 8, 21, 18, 52, uid % 60), dur, bytes: clipMb(dur) * MIB, tapeFrames: [3, 5, 7, 9, 11].map(i => frameIndex(Math.floor(E.t / 10) - i)), p: 0, mbps: S.bitrate, codec: S.codec, recorded: true, isNew: true });
  CLIPS.unshift(clip);
  if (view === "clips") renderClips(); else if (view === "capture") renderInspector();
  notify({ thumb: poster(clip), title: "Запись сохранена", sub: `${fmtDur(dur)}, ${fmtSize(clip.bytes)}`, actions: [{ label: "Открыть", fn: () => { if (!windowVisible()) restore(); setView("clips"); selectOnly(clip.id); } }] });
}
$("#btn-rec").onclick = toggleRec;

function screenshot() {
  const clip = mkClip({ kind: "shot", game: "cs2", date: new Date(2026, 8, 21, 18, 55, uid % 60), bytes: 3.1e6 + hash(uid) * 2e6, tape: frameIndex(Math.floor(E.t / 10)), w: 1648, h: 928, area: true, isNew: true });
  CLIPS.unshift(clip);
  if (E.replay !== "off") E.markers.push({ type: "shot", a: E.t });
  if (GAME.on) { const f = $("#game-flash"); f.classList.remove("is-on"); void f.offsetWidth; f.classList.add("is-on"); }
  notify({ thumb: poster(clip), title: "Скриншот скопирован", sub: "В буфере обмена и в папке скриншотов" });
  if (view === "clips") renderClips();
}
$("#btn-shot").onclick = () => {
  notify({ glyph: "E7A8", neutral: true, title: "Выделите область", sub: "В приложении окно Aura спрячется, а экран замрёт. Здесь снимок сделан сразу", ms: 2600 });
  setTimeout(screenshot, 600);
};

/* таймлайн: наведение, превью кадра, выделение отрезка */
const trk = $("#tl-tracks"), tlCv = $("#tl-canvas"), hov = $("#tl-hover"), peek = $("#tl-peek");
const tAt = clientX => { const r = trk.getBoundingClientRect(); return { t: E.t - (r.right - clientX) / r.width * E.L, x: clientX - r.left, r }; };
let drag = null;
trk.addEventListener("pointermove", e => {
  if (E.replay === "off") return;
  const { t, x, r } = tAt(e.clientX), start = E.fillStart ?? E.t, y = e.clientY - r.top;
  hov.style.transform = `translateX(${x}px)`; hov.classList.add("is-on"); hov.classList.toggle("flip", x > r.width - 120);
  const saved = E.markers.find(m => m.type === "saved" && t >= m.a && t <= m.b);
  $("#tl-hover-t").textContent = t < start ? "ещё не записано" : "−" + fmtDur(E.t - t) + (saved && y > 144 ? ", уже сохранено" : "");
  if (y >= 22 && y < 76 && t >= start && !(E.replay === "error" && t >= E.errorStart)) {
    $("img", peek).src = `media/tape-hd/t${frameIndex(Math.floor(t / FR()))}.jpg`;
    $("span", peek).textContent = "−" + fmtDur(E.t - t);
    peek.style.left = clamp(e.clientX - 104, 8, innerWidth - 216) + "px";
    peek.style.top = (r.top - 126) + "px";
    peek.classList.add("is-on");
  } else peek.classList.remove("is-on");
  if (drag && Math.abs(x - drag.x0) > 4) {
    const a = Math.max(start, Math.min(drag.t0, t)), b = Math.max(drag.t0, t);
    E.sel = { a, b: b >= E.t - .4 ? null : b }; drag.moved = true; paintSaveButtons();
  }
});
trk.addEventListener("pointerleave", () => { hov.classList.remove("is-on"); peek.classList.remove("is-on"); });
trk.addEventListener("pointerdown", e => {
  if (E.replay === "off" || e.button !== 0) return;
  trk.setPointerCapture(e.pointerId);
  const { t, x } = tAt(e.clientX);
  drag = { t0: Math.max(t, E.fillStart ?? t), x0: x, moved: false };
});
trk.addEventListener("pointerup", e => {
  if (!drag) return;
  if (!drag.moved) { const { t } = tAt(e.clientX), a = Math.max(t, E.fillStart ?? t); E.sel = E.t - a < 1 ? null : { a, b: null }; paintSaveButtons(); }
  drag = null;
});

/* =====================================================================
   КЛИПЫ
   ===================================================================== */
const CL = { kind: "all", game: "all", sort: "new", view: "grid", q: "", sel: new Set(), anchor: null, busy: {}, details: true, tw: 210 };
const SORTS = { new: "Сначала новые", old: "Сначала старые", size: "Сначала крупные", name: "По названию" };
function visibleClips() {
  let list = CLIPS.filter(c => (CL.kind === "all" || (CL.kind === "video" ? c.kind === "video" : c.kind === "shot")) && (CL.game === "all" || c.game === CL.game));
  if (CL.q) { const q = CL.q.toLowerCase(); list = list.filter(c => (clipTitle(c) + " " + GAMES[c.game] + " " + dayLabel(c.date)).toLowerCase().includes(q)); }
  const by = { new: (a, b) => b.date - a.date, old: (a, b) => a.date - b.date, size: (a, b) => b.bytes - a.bytes, name: (a, b) => clipTitle(a).localeCompare(clipTitle(b), "ru") }[CL.sort];
  return list.sort(by);
}
let gamePaint = null;
function renderKindBar() {
  const bar = $("#kind-bar");
  const cnt = { all: CLIPS.length, video: CLIPS.filter(c => c.kind === "video").length, shot: CLIPS.filter(c => c.kind === "shot").length };
  bar.innerHTML = [["all", "Все"], ["video", "Видео"], ["shot", "Скриншоты"]].map(([k, l]) => `<button class="sel-item${CL.kind === k ? " is-on" : ""}" data-k="${k}">${l}<span class="cnt">${cnt[k]}</span></button>`).join("") + `<span class="sel-line" id="sel-line"></span>`;
  $$(".sel-item", bar).forEach(b => b.onclick = () => { CL.kind = b.dataset.k; CL.sel.clear(); renderClips(); });
  requestAnimationFrame(() => { const on = $(".sel-item.is-on", bar), line = $("#sel-line"); if (!on || !line) return; const w = 16; line.style.width = w + "px"; line.style.transform = `translateX(${on.offsetLeft + on.offsetWidth / 2 - w / 2}px)`; });
}
function renderClips() {
  renderKindBar();
  gamePaint = combo($("#game-combo"), {
    options: () => [{ value: "all", label: "Все игры", hint: CLIPS.length }, ...Object.entries(GAMES).map(([k, v]) => ({ value: k, label: v, hint: CLIPS.filter(c => c.game === k).length })).filter(o => o.hint)],
    get: () => CL.game, set: v => { CL.game = v; CL.sel.clear(); renderClips(); }, width: 280,
  });
  $("#sort-label").textContent = SORTS[CL.sort];
  $("#details-btn").classList.toggle("is-on", CL.details);
  $$("#sb-views button").forEach(b => b.classList.toggle("is-on", b.dataset.v === CL.view));
  $("#thumb-size").disabled = CL.view !== "grid";
  renderGallery(); renderDetails(); paintStatus();
}
function renderGallery() {
  const inner = $("#gallery-inner"), list = visibleClips();
  $("#gallery").style.setProperty("--tw", CL.tw + "px");
  if (!CLIPS.length) {
    inner.innerHTML = `<div class="empty"><div class="empty-box"><div class="empty-key">${S.hk.save}</div><div><h3>Сохраните первый повтор</h3><p>Нажмите ${S.hk.save} во время игры, и последние ${fmtDur(E.L)} лягут сюда одним файлом. Aura держит их в памяти, пока включён повтор.</p><div class="row"><button class="btn btn-accent" id="e-save">${G("E74E")}Сохранить повтор сейчас</button><button class="btn" id="e-keys">Настроить клавиши</button></div></div></div></div>`;
    $("#e-save").onclick = saveReplay; $("#e-keys").onclick = () => { setView("settings"); selectPane("keys"); };
    return;
  }
  if (!list.length) {
    inner.innerHTML = `<div class="empty"><div class="empty-box"><div><h3>Ничего не нашлось</h3><p>${CL.q ? `По запросу «${CL.q}» нет клипов.` : "Здесь пока пусто."} Попробуйте другое слово или сбросьте фильтры.</p><div class="row"><button class="btn" id="e-reset">Сбросить фильтры</button></div></div></div></div>`;
    $("#e-reset").onclick = () => { CL.kind = "all"; CL.game = "all"; CL.q = ""; $("#clip-search").value = ""; renderClips(); };
    return;
  }
  const grouped = CL.sort === "new" || CL.sort === "old", groups = [];
  if (grouped) for (const c of list) { const k = dayKey(c.date); if (!groups.length || groups.at(-1).k !== k) groups.push({ k, label: dayLabel(c.date), items: [] }); groups.at(-1).items.push(c); }
  else groups.push({ k: "all", label: SORTS[CL.sort], items: list });
  let html = "";
  if (CL.view === "grid") {
    for (const g of groups) {
      const bytes = g.items.reduce((s, c) => s + c.bytes, 0);
      html += `<section class="g-group"><div class="g-head"><b>${g.label}</b><span>${g.items.length} ${plural(g.items.length, "элемент", "элемента", "элементов")}, ${fmtTotal(bytes)}</span><button class="g-pick" data-group="${g.k}">Выбрать все</button></div><div class="g-grid">`;
      for (const c of g.items) html += itemHtml(c);
      html += `</div></section>`;
    }
  } else {
    const col = (k, l, cls = "") => `<button data-sort="${k}" class="${cls}">${l}${CL.sort === k || (k === "new" && CL.sort === "old") ? `<span class="g">${CL.sort === "old" ? "&#xE70E;" : "&#xE70D;"}</span>` : ""}</button>`;
    html += `<div class="tbl"><div class="tbl-h"><span></span><span></span>${col("name", "Имя")}<span>Игра</span>${col("new", "Дата")}<span class="num">Длина</span>${col("size", "Размер", "num")}<span>Тип</span></div>`;
    for (const g of groups) {
      if (grouped) html += `<div class="tbl-g">${g.label}</div>`;
      for (const c of g.items) html += `<div class="tbl-r${CL.sel.has(c.id) ? " is-sel" : ""}" data-id="${c.id}" tabindex="-1"><span class="item-chk">&#xE73E;</span><img src="${poster(c)}" alt="" draggable="false"><span class="c c1${c.name ? "" : " dim"}">${c.name || defaultName(c)}</span><span class="c">${GAMES[c.game]}</span><span class="c">${dayLabel(c.date)}, ${hhmm(c.date)}</span><span class="c num">${c.kind === "video" ? fmtDur(c.dur) : ""}</span><span class="c num">${fmtSize(c.bytes)}</span><span class="c">${c.kind === "video" ? `MP4, ${c.codec}` : `PNG, ${c.w}×${c.h}`}</span></div>`;
    }
    html += `</div>`;
  }
  inner.innerHTML = html;
  $("#gallery-inner").classList.toggle("multi", CL.sel.size > 1);
  $$(".g-pick", inner).forEach(b => b.onclick = () => { const g = groups.find(x => x.k === b.dataset.group); g.items.forEach(c => CL.sel.add(c.id)); paintSel(); });
  $$(".tbl-h button", inner).forEach(b => b.onclick = () => { const k = b.dataset.sort; CL.sort = k === "new" ? (CL.sort === "new" ? "old" : "new") : k; renderClips(); });
  $$(".item.is-new", inner).forEach(el => setTimeout(() => { el.classList.remove("is-new"); const c = byId(+el.dataset.id); if (c) delete c.isNew; }, 1600));
  for (const [id, p] of Object.entries(CL.busy)) addBusy(+id, p);
}
function itemHtml(c) {
  return `<div class="item${CL.sel.has(c.id) ? " is-sel" : ""}${c.isNew ? " is-new" : ""}" data-id="${c.id}" tabindex="-1">
    <div class="item-thumb"><img src="${poster(c)}" alt="" draggable="false">${c.kind === "video" ? `<span class="item-dur">${fmtDur(c.dur)}</span><span class="item-scrub"></span>` : `<span class="item-kind">${G(c.area ? "E7A8" : "E722")}${c.area ? "область" : "экран"}</span>`}</div>
    <span class="item-chk">&#xE73E;</span>
    <div class="item-name">${clipTitle(c)}</div>
    <div class="item-meta"><span>${hhmm(c.date)}${c.kind === "video" && c.tracks === 2 ? ", 2 дорожки" : ""}</span><span>${fmtSize(c.bytes)}</span></div></div>`;
}
/* наведение: пролистывание кадров клипа */
$("#gallery").addEventListener("pointermove", e => {
  const th = e.target.closest(".item-thumb"); if (!th) return;
  const c = byId(+th.parentElement.dataset.id); if (!c || c.kind !== "video") return;
  const r = th.getBoundingClientRect(), p = clamp((e.clientX - r.left) / r.width, 0, .999);
  const img = $("img", th), src = frameSrc(c, Math.floor(p * 5));
  if (!img.src.endsWith(src)) img.src = src;
  $(".item-scrub", th).style.width = p * 100 + "%";
});
$("#gallery").addEventListener("pointerout", e => {
  const th = e.target.closest(".item-thumb"); if (!th || th.contains(e.relatedTarget)) return;
  const c = byId(+th.parentElement.dataset.id); if (c) $("img", th).src = poster(c);
});
/* выбор: щелчок, Ctrl, Shift, рамка */
const gal = $("#gallery"), mq = $("#marquee");
let marq = null;
gal.addEventListener("pointerdown", e => {
  if (e.button !== 0) return;
  const it = e.target.closest("[data-id]");
  if (it) { clickItem(+it.dataset.id, e); gal.focus({ preventScroll: true }); return; }
  if (e.target.closest("button, .empty")) return;
  const gr = gal.getBoundingClientRect();
  marq = { x0: e.clientX - gr.left + gal.scrollLeft, y0: e.clientY - gr.top + gal.scrollTop, base: e.ctrlKey ? new Set(CL.sel) : new Set(), moved: false };
  if (!e.ctrlKey) { CL.sel.clear(); paintSel(); }
  gal.setPointerCapture(e.pointerId); gal.focus({ preventScroll: true });
});
gal.addEventListener("pointermove", e => {
  if (!marq) return;
  const gr = gal.getBoundingClientRect(), x = e.clientX - gr.left + gal.scrollLeft, y = e.clientY - gr.top + gal.scrollTop;
  if (!marq.moved && Math.hypot(x - marq.x0, y - marq.y0) < 5) return;
  marq.moved = true;
  const L = Math.min(x, marq.x0), T = Math.min(y, marq.y0), Wd = Math.abs(x - marq.x0), Hd = Math.abs(y - marq.y0);
  Object.assign(mq.style, { left: L + "px", top: T + "px", width: Wd + "px", height: Hd + "px" }); mq.classList.add("is-on");
  const sel = new Set(marq.base);
  $$("[data-id]", gal).forEach(el => {
    const r = el.getBoundingClientRect(), ex = r.left - gr.left + gal.scrollLeft, ey = r.top - gr.top + gal.scrollTop;
    if (ex < L + Wd && ex + r.width > L && ey < T + Hd && ey + r.height > T) sel.add(+el.dataset.id);
  });
  CL.sel = sel; paintSel(true);
  if (e.clientY > gr.bottom - 20) gal.scrollTop += 12; else if (e.clientY < gr.top + 20) gal.scrollTop -= 12;
});
gal.addEventListener("pointerup", () => { if (!marq) return; mq.classList.remove("is-on"); marq = null; paintSel(); });
function clickItem(id, e) {
  const order = visibleClips().map(c => c.id);
  if (e.shiftKey && CL.anchor != null) {
    const a = order.indexOf(CL.anchor), b = order.indexOf(id);
    if (!e.ctrlKey) CL.sel.clear();
    order.slice(Math.min(a, b), Math.max(a, b) + 1).forEach(i => CL.sel.add(i));
  } else if (e.ctrlKey || e.target.closest(".item-chk")) { CL.sel.has(id) ? CL.sel.delete(id) : CL.sel.add(id); CL.anchor = id; }
  else { CL.sel.clear(); CL.sel.add(id); CL.anchor = id; }
  paintSel();
}
gal.addEventListener("dblclick", e => { const it = e.target.closest("[data-id]"); if (it) openClip(byId(+it.dataset.id)); });
gal.addEventListener("contextmenu", e => {
  e.preventDefault();
  const it = e.target.closest("[data-id]");
  if (!it) { openMenu([{ label: "Вид", glyph: "F0E2", disabled: true }, { label: "Эскизы", check: CL.view === "grid", fn: () => { CL.view = "grid"; renderClips(); } }, { label: "Таблица", check: CL.view === "list", fn: () => { CL.view = "list"; renderClips(); } }, "sep", { label: "Выделить всё", glyph: "E8B3", key: "Ctrl+A", fn: selectAll }, { label: "Открыть папку записей", glyph: "E838", fn: () => notify({ glyph: "E838", neutral: true, title: "Открыта папка записей", sub: S.saveRoot }) }], { x: e.clientX, y: e.clientY }); return; }
  const id = +it.dataset.id;
  if (!CL.sel.has(id)) { CL.sel.clear(); CL.sel.add(id); CL.anchor = id; paintSel(); }
  itemMenu(byId(id), e.clientX, e.clientY);
});
function selectOnly(id) { CL.sel.clear(); CL.sel.add(id); CL.anchor = id; if (view === "clips") { renderClips(); requestAnimationFrame(() => $(`#gallery [data-id="${id}"]`)?.scrollIntoView({ block: "nearest" })); } }
function selectAll() { visibleClips().forEach(c => CL.sel.add(c.id)); paintSel(); }
function paintSel(fast) {
  $$("#gallery [data-id]").forEach(el => el.classList.toggle("is-sel", CL.sel.has(+el.dataset.id)));
  $("#gallery-inner").classList.toggle("multi", CL.sel.size > 1);
  paintStatus(); if (!fast) renderDetails();
}
function paintStatus() {
  const list = visibleClips(), sel = [...CL.sel].map(byId).filter(Boolean);
  $("#sb-count").textContent = `${list.length} ${plural(list.length, "элемент", "элемента", "элементов")}`;
  $("#sb-sel").textContent = sel.length ? `Выбрано: ${sel.length}, ${fmtTotal(sel.reduce((s, c) => s + c.bytes, 0))}` : `Всего ${fmtTotal(CLIPS.reduce((s, c) => s + c.bytes, 0))}`;
}
function itemMenu(c, x, y) {
  const many = CL.sel.size > 1;
  const items = many ? [
    { head: `Выбрано ${CL.sel.size}` },
    { label: "Сжать для Discord", glyph: "E7B8", fn: () => [...CL.sel].map(byId).forEach(compress) },
    { label: "Показать в папке", glyph: "E838", fn: () => reveal(c) },
    { label: "Копировать", glyph: "E8C8", key: "Ctrl+C", fn: () => copyClip(c, CL.sel.size) },
    "sep",
    { label: "Удалить в корзину", glyph: "E74D", key: "Del", danger: true, fn: () => removeClips([...CL.sel]) },
  ] : [
    { label: c.kind === "video" ? "Воспроизвести" : "Открыть", glyph: c.kind === "video" ? "E768" : "EB9F", key: "Enter", fn: () => openClip(c) },
    ...(c.kind === "video" ? [{ label: "Изменить в редакторе", glyph: "E8C6", key: "Ctrl+E", fn: () => openEditor(c) }] : []),
    { label: "Показать в папке", glyph: "E838", fn: () => reveal(c) },
    "sep",
    { label: c.kind === "shot" ? "Копировать картинку" : "Копировать файл", glyph: "E8C8", key: "Ctrl+C", fn: () => copyClip(c) },
    ...(c.kind === "video" ? [{ label: "Сжать для Discord", glyph: "E7B8", fn: () => compress(c) }] : []),
    { label: "Переименовать", glyph: "E8AC", key: "F2", fn: () => { CL.details = true; renderClips(); setTimeout(startRename, 30); } },
    "sep",
    { label: "Удалить в корзину", glyph: "E74D", key: "Del", danger: true, fn: () => removeClips([c.id]) },
  ];
  openMenu(items, { x, y, minWidth: 250 });
}
function openClip(c) { if (!c) return; notify({ thumb: poster(c), title: c.kind === "video" ? "Открываю в плеере" : "Открываю в просмотрщике", sub: clipPath(c).split("\\").pop() }); }
function openEditor(c) { notify({ thumb: poster(c), title: "Редактор откроется отдельным окном", sub: "В прототипе его нет, он уже переделан в приложении" }); }
function reveal(c) { notify({ glyph: "E838", neutral: true, title: "Показано в проводнике", sub: clipPath(c) }); }
function copyClip(c, n) { notify({ glyph: "E8C8", neutral: true, title: n > 1 ? `Скопировано файлов: ${n}` : c.kind === "shot" ? "Картинка в буфере обмена" : "Файл в буфере обмена", sub: "Вставьте в Discord, Telegram или в папку" }); }
function addBusy(id, p) {
  const th = $(`#gallery .item[data-id="${id}"] .item-thumb`); if (!th) return;
  let b = $(".item-busy", th); if (!b) { b = h("div", { class: "item-busy" }, `Сжимаю<div class="pbar"><i></i></div>`); th.appendChild(b); }
  $(".pbar i", b).style.width = p * 100 + "%";
}
function compress(c) {
  if (!c || c.kind !== "video" || CL.busy[c.id] != null) return;
  CL.busy[c.id] = 0; const t0 = performance.now(), dur = 2400, target = S.attachMb * (.92 + hash(c.id) * .05);
  renderDetails();
  (function step() {
    const p = Math.min(1, (performance.now() - t0) / dur); CL.busy[c.id] = p;
    addBusy(c.id, p); const bar = $(`#det-busy i`); if (bar && CL.sel.has(c.id)) bar.style.width = p * 100 + "%";
    if (p < 1) { requestAnimationFrame(step); return; }
    delete CL.busy[c.id]; c.compressed = target;
    $$(`#gallery .item[data-id="${c.id}"] .item-busy`).forEach(e => e.remove());
    renderDetails();
    notify({ glyph: "E7B8", title: `Готово для Discord: ${num(target, 1)} МБ`, sub: "Копия рядом с оригиналом и уже в буфере обмена", actions: [{ label: "Показать", fn: () => reveal(c) }] });
  })();
}
let undoList = null;
function removeClips(ids) {
  const removed = CLIPS.filter(c => ids.includes(c.id)); if (!removed.length) return;
  ids.forEach(id => $$(`#gallery [data-id="${id}"]`).forEach(el => el.classList.add("is-out")));
  setTimeout(() => { CLIPS = CLIPS.filter(c => !ids.includes(c.id)); ids.forEach(id => CL.sel.delete(id)); undoList = removed; if (view === "clips") renderClips(); if (view === "capture") renderInspector(); }, reduced ? 1 : 160);
  const n = removed.length;
  notify({ glyph: "E74D", neutral: true, title: n === 1 ? "Перемещено в корзину" : `В корзину: ${n} ${plural(n, "файл", "файла", "файлов")}`, sub: `Освобождено ${fmtTotal(removed.reduce((s, c) => s + c.bytes, 0))}`, ms: 6000, actions: [{ label: "Отменить", fn: () => { if (!undoList) return; CLIPS.push(...undoList); undoList = null; renderClips(); } }] });
}
/* область сведений */
function renderDetails() {
  const box = $("#details");
  box.classList.toggle("is-open", CL.details);
  if (!CL.details) return;
  const cs = [...CL.sel].map(byId).filter(Boolean);
  if (!cs.length) {
    const vids = CLIPS.filter(c => c.kind === "video");
    box.innerHTML = `<div class="det"><div class="det-empty">${G("E8B2")}<div>Выберите клип, чтобы увидеть сведения</div><div style="margin-top:12px;color:var(--tx2)">${vids.length} видео, ${CLIPS.length - vids.length} ${plural(CLIPS.length - vids.length, "скриншот", "скриншота", "скриншотов")}<br>${fmtTotal(CLIPS.reduce((s, c) => s + c.bytes, 0))} на диске E:</div></div></div>`;
    return;
  }
  if (cs.length > 1) {
    const bytes = cs.reduce((s, c) => s + c.bytes, 0), dur = cs.reduce((s, c) => s + (c.dur || 0), 0), vids = cs.filter(c => c.kind === "video");
    box.innerHTML = `<div class="det"><div class="det-mosaic">${cs.slice(0, 3).map(c => `<img src="${poster(c)}" alt="">`).join("")}${cs.length > 3 ? `<span>ещё ${cs.length - 3}</span>` : ""}</div>
      <div class="det-name"><h3>Выбрано: ${cs.length}</h3></div><div class="det-sub">${fmtTotal(bytes)}${dur ? `, ${fmtDur(dur)} видео` : ""}</div>
      <div class="det-acts">${vids.length ? `<button class="btn" id="d-comp">${G("E7B8")}Сжать для Discord<span class="keyhint" style="color:var(--tx3);font-size:12px">${vids.length} видео</span></button>` : ""}
      <button class="btn" id="d-rev">${G("E838")}Показать в папке</button><button class="btn btn-danger" id="d-del">${G("E74D")}Удалить в корзину<span class="keyhint">${kbds("Del")}</span></button></div></div>`;
    $("#d-comp")?.addEventListener("click", () => vids.forEach(compress));
    $("#d-rev").onclick = () => reveal(cs[0]); $("#d-del").onclick = () => removeClips(cs.map(c => c.id));
    return;
  }
  const c = cs[0], vid = c.kind === "video", busy = CL.busy[c.id];
  box.innerHTML = `<div class="det">
    <div class="det-prev" id="det-prev"><img src="${poster(c)}" alt="">${vid ? `<button class="det-play" data-tip="Воспроизвести" data-kbd="Enter">${G("F5B0")}</button><span class="item-dur" style="right:6px;bottom:6px">${fmtDur(c.dur)}</span><span class="item-scrub" style="opacity:.9"></span>` : ""}</div>
    <div class="det-name"><h3 id="det-title">${clipTitle(c)}</h3><button class="btn btn-subtle btn-icon" id="d-ren" data-tip="Переименовать" data-kbd="F2">${G("E8AC")}</button></div>
    <div class="det-sub">${c.name ? GAMES[c.game] + ", " : ""}${dayLabel(c.date)}, ${hhmm(c.date)}</div>
    <dl class="det-props">
      ${vid ? `<dt>Длина</dt><dd>${fmtDur(c.dur)}</dd><dt>Видео</dt><dd>${c.w}×${c.h}, ${c.fps} к/с</dd><dt>Кодек</dt><dd>${c.codec}, ${c.mbps} Мбит/с</dd><dt>Звук</dt><dd>${c.tracks === 2 ? "Игра и микрофон, 2 дорожки" : "1 дорожка"}</dd>` : `<dt>Снимок</dt><dd>${c.area ? "Область" : "Весь экран"}, ${c.w}×${c.h}</dd><dt>Формат</dt><dd>PNG</dd>`}
      <dt>Размер</dt><dd>${fmtSize(c.bytes)}${c.compressed ? `, для Discord ${num(c.compressed, 1)} МБ` : ""}</dd>
      <dt>Папка</dt><dd class="path" title="${clipPath(c)}">${clipPath(c)}</dd>
    </dl>
    <div class="det-acts">
      ${vid ? `<button class="btn btn-accent" id="d-edit">${G("E8C6")}Изменить в редакторе<span class="keyhint">${kbds("Ctrl E")}</span></button>` : `<button class="btn btn-accent" id="d-copy1">${G("E8C8")}Копировать картинку<span class="keyhint">${kbds("Ctrl C")}</span></button>`}
      ${vid ? (busy != null ? `<div class="btn is-disabled" style="opacity:1">${G("E7B8")}Сжимаю до ${S.attachMb} МБ<span class="pbar" id="det-busy" style="margin-left:auto;width:90px"><i style="width:${busy * 100}%"></i></span></div>` : `<button class="btn" id="d-comp1">${G("E7B8")}Сжать для Discord<span class="keyhint" style="color:var(--tx3);font-size:12px">до ${S.attachMb} МБ</span></button>`) : ""}
      <div class="row2"><button class="btn" id="d-rev1">${G("E838")}В папке</button><button class="btn" id="d-copy">${G("E8C8")}Копировать</button></div>
      <button class="btn btn-danger" id="d-del1">${G("E74D")}Удалить в корзину<span class="keyhint">${kbds("Del")}</span></button>
    </div></div>`;
  $("#d-ren").onclick = startRename;
  $("#d-edit")?.addEventListener("click", () => openEditor(c));
  $("#d-copy1")?.addEventListener("click", () => copyClip(c));
  $("#d-comp1")?.addEventListener("click", () => compress(c));
  $("#d-rev1").onclick = () => reveal(c); $("#d-copy").onclick = () => copyClip(c); $("#d-del1").onclick = () => removeClips([c.id]);
  $(".det-play")?.addEventListener("click", () => openClip(c));
  const pv = $("#det-prev"), pimg = $("img", pv), ps = $(".item-scrub", pv);
  if (vid) { pv.onpointermove = e => { const r = pv.getBoundingClientRect(), p = clamp((e.clientX - r.left) / r.width, 0, .999); pimg.src = frameSrc(c, Math.floor(p * 5)); ps.style.width = p * 100 + "%"; }; pv.onpointerleave = () => { pimg.src = poster(c); ps.style.width = 0; }; }
}
function startRename() {
  const c = byId([...CL.sel][0]); if (!c || CL.sel.size !== 1) return;
  const hdr = $("#det-title"); if (!hdr) return;
  const inp = h("input", { value: clipTitle(c), spellcheck: "false" }); hdr.replaceWith(inp); inp.focus(); inp.select();
  let done = false;
  const finish = ok => {
    if (done) return; done = true;
    if (ok) { const v = inp.value.trim(); c.name = v && v !== GAMES[c.game] ? v : ""; }
    renderGallery(); renderDetails();
    if (ok) notify({ glyph: "E8AC", neutral: true, title: "Переименовано", sub: clipPath(c).split("\\").pop() });
  };
  inp.onkeydown = e => { if (e.key === "Enter") finish(true); if (e.key === "Escape") { e.stopPropagation(); finish(false); } };
  inp.onblur = () => finish(true);
}
$("#clip-search").oninput = e => { CL.q = e.target.value.trim(); CL.sel.clear(); renderGallery(); renderDetails(); paintStatus(); };
$("#sort-btn").onclick = e => openMenu(Object.entries(SORTS).map(([k, l]) => ({ label: l, sel: CL.sort === k, fn: () => { CL.sort = k; renderClips(); } })), { anchor: e.currentTarget, minWidth: 220 });
$("#details-btn").onclick = () => { CL.details = !CL.details; renderClips(); };
$$("#sb-views button").forEach(b => b.onclick = () => { CL.view = b.dataset.v; renderClips(); });
$("#thumb-size").oninput = e => { CL.tw = +e.target.value; $("#gallery").style.setProperty("--tw", CL.tw + "px"); paintRange(e.target); };
function paintRange(inp) { inp.style.setProperty("--p", ((inp.value - inp.min) / (inp.max - inp.min) * 100) + "%"); }

/* =====================================================================
   НАСТРОЙКИ
   ===================================================================== */
const QUALITY = ["preset", "res", "fps", "bitrate", "codec", "bits10"];
const HK_LABELS = { save: "Сохранить повтор", save30: "Сохранить последние 30 секунд", toggle: "Включить или выключить повтор", mark: "Поставить метку", recStart: "Начать запись", recStop: "Остановить запись", shot: "Скриншот экрана", area: "Скриншот области", panel: "Панель поверх игры", folder: "Открыть папку записей" };
const HK_GLYPH = { save: "E74E", save30: "E81C", toggle: "E7E8", mark: "E7C1", recStart: "E7C8", recStop: "E71A", shot: "E722", area: "E7A8", panel: "E7FC", folder: "E838" };
const HK_DEFAULT = { save: "Alt+F10", save30: "Alt+Shift+F10", toggle: "Alt+F11", mark: "Alt+M", recStart: "Alt+F9", recStop: "Alt+Shift+F9", shot: "Alt+PrintScreen", area: "PrintScreen", panel: "Alt+Q", folder: "Alt+F8" };
const HK_TAKEN = { "Alt+Z": "Занято оверлеем NVIDIA", "Win+Alt+R": "Занято Xbox Game Bar", "Win+Alt+G": "Занято Xbox Game Bar", "PrintScreen": "Windows откроет «Ножницы», если это включено в параметрах", "Alt+Tab": "Системное сочетание Windows", "Alt+F4": "Закрывает окно игры" };
const CORNERS = { TL: "Слева сверху", TC: "Сверху по центру", TR: "Справа сверху", BL: "Слева снизу", BR: "Справа снизу" };
const hkRows = keys => keys.map(k => ({ key: k, hk: true, glyph: HK_GLYPH[k], label: HK_LABELS[k], type: "hotkey", isNew: k === "mark" || k === "panel", desc: () => ({ mark: "Флажок на таймлайне и в редакторе, чтобы быстро найти момент", panel: "Сохранить, запись, микрофон и скриншот, не выходя из игры", area: "Экран замирает, остаётся выделить нужное", save30: "Короткий фрагмент без лишнего" })[k] || "" }));
const PANES = [
  { id: "capture", title: "Запись", glyph: "E714", groups: [
    { title: "Качество", rows: [
      { key: "preset", glyph: "E9E9", label: "Набор", type: "seg", options: [["eco", "Эконом"], ["normal", "Обычный"], ["high", "Высокий"], ["max", "Максимум"], ["custom", "Своё"]], desc: () => S.preset === "custom" ? "Свои значения ниже" : PRESETS[S.preset].note, onChange: v => { if (v !== "custom") { Object.assign(S, { res: PRESETS[v].res, fps: PRESETS[v].fps, bitrate: PRESETS[v].bitrate }); flash(["res", "fps", "bitrate"]); } } },
      { key: "res", glyph: "E7F4", label: "Разрешение", type: "combo", options: [[720, "720p", "1280×720"], [1080, "1080p", "1920×1080"], [1440, "1440p", "2560×1440"], [2160, "4K", "3840×2160"]], desc: () => S.res > 1440 ? `<span class="warn">Выше монитора 2560×1440: файл больше, а чётче не станет</span>` : "Монитор 2560×1440, писать выше родного нет смысла", custom: true },
      { key: "fps", glyph: "E916", label: "Частота кадров", type: "combo", options: [[30, "30 к/с"], [60, "60 к/с"], [120, "120 к/с"], [144, "144 к/с"]], desc: () => S.fps >= 120 && S.bitrate < 50 ? `<span class="warn">Для ${S.fps} к/с нужен битрейт от 50 Мбит/с, иначе картинка поплывёт в движении</span>` : "Монитор 240 Гц. Для обычных клипов хватает 60", custom: true },
      { key: "bitrate", glyph: "EC4A", label: "Битрейт", type: "slider", min: 5, max: 80, step: 1, fmt: v => `${v} Мбит/с`, desc: () => `Файл повтора ≈ <b>${Math.round(clipMb(S.replayLen))} МБ</b>, час записи ≈ <b>${num(clipMb(3600) / 1024, 1)} ГБ</b>`, custom: true },
      { key: "codec", glyph: "E7B8", label: "Кодек", type: "combo", options: [["H.264", "H.264", "открывается везде"], ["HEVC", "HEVC", "меньше файл"], ["AV1", "AV1", "нужна RTX 40", true]], desc: () => S.codec === "HEVC" ? "Тот же вид при меньшем файле. Telegram и Discord показывают, старые плееры нет" : "Открывается везде, но файл больше" },
      { key: "bits10", glyph: "E790", label: "Десять бит цвета", type: "toggle", desc: () => S.codec === "HEVC" ? "Ровнее градиенты неба и теней, файл больше примерно на 10%" : "Только для HEVC", disabled: () => S.codec !== "HEVC" },
    ] },
    { title: "Мгновенный повтор", rows: [
      { key: "replayLen", glyph: "E81C", label: "Длина повтора", type: "combo", options: [[30, "30 секунд"], [60, "1 минута"], [180, "3 минуты"], [300, "5 минут"], [600, "10 минут"], [900, "15 минут"]], desc: () => `Столько последних минут Aura держит в памяти: сейчас ≈ <b>${Math.round(ramMb())} МБ</b> из 32 ГБ`, onChange: () => { E.sel = null; paintState(); } },
      { key: "autoReplay", glyph: "E768", label: "Включать при запуске Aura", type: "toggle", desc: () => "Повтор начинает писаться сразу, ничего нажимать не нужно" },
    ] },
    { title: "Что записывать", rows: [
      { key: "source", glyph: "E7FC", label: "Источник", type: "combo", options: [["auto", "Игра, иначе монитор 1"], ["mon", "Всегда монитор 1"]], desc: () => S.source === "auto" ? "Aura сама находит игру в полноэкранном режиме и пишет только её окно" : "Пишется весь экран" },
      { key: "cursor", glyph: "E962", label: "Курсор мыши", type: "toggle", desc: () => "В шутерах обычно не нужен" },
    ] },
  ] },
  { id: "audio", title: "Звук", glyph: "E767", groups: [
    { title: "Источники", rows: [
      { key: "gameAudio", glyph: "E767", label: "Звук игры", type: "toggle", desc: () => "Всё, что играет на выбранном устройстве", meter: "game" },
      { key: "gameDevice", label: "Устройство вывода", type: "combo", options: [["realtek", "Динамики (Realtek(R) Audio)"], ["usb", "Наушники (USB Audio Device)"], ["default", "Как в Windows"]], when: () => S.gameAudio, sub: true },
      { key: "mic", glyph: "E720", label: "Микрофон", type: "toggle", desc: () => "Ваш голос отдельной дорожкой", meter: "mic" },
      { key: "micDevice", label: "Устройство ввода", type: "combo", options: [["usb", "Микрофон (USB Audio Device)"], ["realtek", "Микрофон (Realtek(R) Audio)"], ["default", "Как в Windows"]], when: () => S.mic, sub: true },
    ] },
    { title: "Файл", rows: [
      { key: "tracks", glyph: "E8FD", label: "Дорожки", type: "radios", options: [["mixed", "Одна общая"], ["separate", "Две отдельные"]], desc: () => S.tracks === "separate" ? "Голос можно убрать или свести заново в редакторе" : "Файл одинаково звучит в любом плеере", when: () => S.mic && S.gameAudio },
    ] },
    { title: "Микрофон", rows: [
      { key: "noise", glyph: "E9E9", label: "Шумоподавление", type: "toggle", desc: () => "Убирает фоновый гул в паузах между фразами", when: () => S.mic },
      { key: "gate", label: "Порог тишины", type: "slider", min: -70, max: -20, step: 1, fmt: v => `${v} дБ`, desc: () => "Тише этого уровня микрофон молчит", when: () => S.mic && S.noise, sub: true },
    ] },
  ] },
  { id: "keys", title: "Клавиши", glyph: "E765", groups: [
    { title: "Повтор", rows: hkRows(["save", "save30", "toggle", "mark"]) },
    { title: "Запись", rows: hkRows(["recStart", "recStop"]) },
    { title: "Скриншоты", rows: hkRows(["shot", "area"]) },
    { title: "Aura", rows: hkRows(["panel", "folder"]), footer: true },
  ] },
  { id: "files", title: "Файлы", glyph: "E8B7", groups: [
    { title: "Папки", rows: [{ key: "saveRoot", glyph: "E8B7", label: "Видео", type: "folder" }, { key: "shotRoot", glyph: "EB9F", label: "Скриншоты", type: "folder" }] },
    { title: "Имена", rows: [
      { key: "byGame", glyph: "E8F1", label: "Раскладывать по папкам игр", type: "toggle", desc: () => S.byGame ? `Например, ${S.saveRoot}\\Counter-Strike 2\\` : `Все записи в ${S.saveRoot}\\` },
      { key: "template", glyph: "E8AC", label: "Шаблон имени", type: "text", desc: () => `Получится «${S.template.replace("{game}", "Counter-Strike 2").replace("{date}", "2026-09-21").replace("{time}", "18-26-56")}.mp4»` },
    ] },
    { title: "Место на диске E:", rows: [{ type: "disk", glyph: "EDA2" }] },
    { title: "Отправка", rows: [{ key: "attachMb", glyph: "E7B8", label: "Сжимать для Discord до", type: "combo", options: [[10, "10 МБ", "без Nitro"], [20, "20 МБ", "с запасом"], [50, "50 МБ", "Nitro Basic"], [500, "500 МБ", "Nitro"]], desc: () => "Кнопка «Сжать для Discord» в клипах подгонит файл под этот размер" }] },
  ] },
  { id: "notify", title: "Уведомления", glyph: "EA8F", groups: [
    { title: "Поверх игры", rows: [
      { key: "notify", glyph: "EA8F", label: "Показывать уведомления", type: "toggle", desc: () => "После сохранения повтора, записи и скриншота. Фокус у игры не забирается" },
      { key: "corner", glyph: "E7F4", label: "Угол экрана", type: "corner", when: () => S.notify, desc: () => CORNERS[S.corner] },
      { key: "notifySec", glyph: "E916", label: "Сколько держать", type: "slider", min: 1, max: 6, step: 1, fmt: v => `${v} с`, when: () => S.notify, desc: () => "" },
      { key: "sound", glyph: "E767", label: "Звук при сохранении", type: "combo", options: [["soft", "Мягкий"], ["classic", "Классический"], ["file", "Свой файл…"], ["none", "Без звука"]], desc: () => "Сигнал, что запись легла на диск", play: true },
      { type: "preview", glyph: "E7B3", label: "Как это выглядит", desc: () => "Откроется игра и покажет уведомление в выбранном углу" },
    ] },
  ] },
  { id: "app", title: "Приложение", glyph: "E771", groups: [
    { title: "Запуск", rows: [
      { key: "autostart", glyph: "E7E8", label: "Запускать вместе с Windows", type: "toggle", desc: () => "" },
      { key: "trayStart", glyph: "E8A0", label: "Стартовать в трее", type: "toggle", desc: () => "Окно не открывается, Aura сразу работает в фоне" },
    ] },
    { title: "Оформление", rows: [
      { key: "theme", glyph: "E790", label: "Тема", type: "combo", options: [["dark", "Тёмная"], ["light", "Светлая"], ["system", "Как в Windows"]], desc: () => "", onChange: applyTheme },
    ] },
    { title: "Обновления", rows: [
      { type: "version", glyph: "E895" },
      { key: "updates", glyph: "E895", label: "Проверять при запуске", type: "toggle", desc: () => "" },
      { key: "driver", glyph: "EA18", label: "Следить за драйвером видеокарты", type: "toggle", desc: () => "Проверка раз в неделю, в фоне" },
    ] },
    { title: "Обслуживание", rows: [
      { type: "button", glyph: "E9D9", label: "Журнал работы", desc: () => "Пригодится, если что-то пошло не так", btn: "Открыть папку", fn: () => notify({ glyph: "E838", neutral: true, title: "Открыта папка с логами", sub: "%LOCALAPPDATA%\\Aura\\logs" }) },
      { type: "button", glyph: "EA99", label: "Кэш миниатюр", desc: () => "48 МБ. Миниатюры создадутся заново", btn: "Очистить", fn: () => notify({ glyph: "E73E", title: "Кэш очищен", sub: "Освобождено 48 МБ" }) },
      { type: "button", glyph: "E72C", label: "Сбросить все настройки", desc: () => "Клавиши, папки и качество вернутся к стандартным", btn: "Сбросить", danger: true, fn: e => openMenu([{ head: "Сбросить все настройки Aura? Клипы останутся на месте." }, { label: "Сбросить", glyph: "E72C", danger: true, fn: () => notify({ glyph: "E72C", neutral: true, title: "Настройки сброшены", sub: "В прототипе ничего не изменилось" }) }, { label: "Отмена", fn: () => {} }], { anchor: e.currentTarget, minWidth: 260 }) },
    ] },
  ] },
  { id: "system", title: "Система", glyph: "E770", groups: [
    { title: "Видеокарта", rows: [{ type: "gpu", glyph: "E9F5" }] },
    { title: "Как идёт запись", rows: [{ type: "live", glyph: "EC4A" }] },
    { title: "Компьютер", rows: [["Процессор", "AMD Ryzen 5 9600X, 6 ядер, 12 потоков"], ["Оперативная память", "32 ГБ, 5600 МГц"], ["Материнская плата", "Gigabyte B650 GAMING X AX V2"], ["Windows", "11 IoT Корпоративная LTSC, сборка 26100"], ["Экран", "2560×1440, 240 Гц"]].map(([l, v]) => ({ type: "info", label: l, value: v, sub: true })) },
  ] },
];
let pane = "capture", pending = null;
function renderSettings() {
  const list = $("#set-list");
  if (!list.childElementCount) {
    list.appendChild(h("span", { class: "set-pill", id: "set-pill" }));
    PANES.forEach(p => { const b = h("button", { class: "set-item", "data-pane": p.id }, `${G(p.glyph)}<span>${p.title}</span>`); b.onclick = () => { $("#set-search").value = ""; selectPane(p.id); }; list.appendChild(b); });
  }
  selectPane(pane, true);
}
function setPill() {
  const b = $(`.set-item[data-pane="${pane}"]`), pill = $("#set-pill"); if (!pill) return;
  const q = $("#set-search").value;
  $$(".set-item").forEach(x => x.classList.toggle("is-on", !q && x.dataset.pane === pane));
  pill.style.opacity = q ? 0 : 1; if (b) pill.style.setProperty("--y", (b.offsetTop + 10) + "px");
}
function selectPane(id, silent) {
  pane = id; setPill();
  const p = PANES.find(p => p.id === id), page = $("#set-page");
  const st = page.scrollTop;
  page.innerHTML = `<div class="set-inner"><div class="set-title">${p.title}</div>${infobarHtml()}</div>`;
  const inner = $(".set-inner", page);
  p.groups.forEach(g => inner.appendChild(renderGroup(g)));
  bindInfobar();
  if (silent) page.scrollTop = st; else { page.scrollTop = 0; inner.animate?.([{ opacity: 0, transform: "translateY(12px)" }, { opacity: 1, transform: "none" }], { duration: reduced ? 1 : 333, easing: "cubic-bezier(0,0,0,1)" }); }
}
function infobarHtml() {
  if (!pending) return "";
  return `<div class="infobar" id="infobar"><span class="ib-ico">&#xE946;</span><span class="ib-t">Качество изменено</span><span class="ib-m">Новое качество начнёт действовать сразу, повтор в памяти начнётся заново.</span><button class="btn btn-sm" id="ib-revert">Отменить</button><button class="btn btn-sm btn-accent" id="ib-apply">Применить</button></div>`;
}
function bindInfobar() {
  $("#ib-apply")?.addEventListener("click", () => {
    pending = null;
    if (E.replay !== "off") { E.fillStart = E.t; E.sel = null; E.markers = []; }
    paintState(); refreshPane();
    notify({ glyph: "E73E", title: "Качество применено", sub: `${RES_W[S.res]}×${S.res}, ${S.fps} к/с, ${S.bitrate} Мбит/с. Повтор набирается заново` });
  });
  $("#ib-revert")?.addEventListener("click", () => { Object.assign(S, pending); pending = null; refreshPane(); });
}
function refreshPane() { if ($("#set-search").value) runSearch(); else selectPane(pane, true); }
function flash(keys) { requestAnimationFrame(() => keys.forEach(k => { const r = $(`.s-row[data-key="${k}"]`); if (r) { r.classList.remove("is-flash"); void r.offsetWidth; r.classList.add("is-flash"); } })); }
function renderGroup(g, rows) {
  const box = h("section", { class: "s-group" }, `<div class="s-group-t">${g.title}</div>`);
  const card = h("div", { class: "s-card" });
  (rows || g.rows).forEach(r => card.appendChild(renderRow(r)));
  box.appendChild(card);
  if (g.footer) {
    const b = h("button", { class: "btn", style: "margin-top:12px" }, `${G("E72C")}Вернуть стандартные`);
    b.onclick = () => { Object.assign(S.hk, HK_DEFAULT); refreshPane(); syncHotkeys(); notify({ glyph: "E72C", neutral: true, title: "Клавиши как в Aura по умолчанию", sub: "Сохранить повтор: Alt F10" }); };
    box.appendChild(b);
  }
  return box;
}
function setVal(r, v) {
  const prev = r.hk ? S.hk[r.key] : S[r.key];
  if (r.hk) S.hk[r.key] = v; else S[r.key] = v;
  if (QUALITY.includes(r.key)) {
    if (!pending) pending = { preset: S.preset, res: S.res, fps: S.fps, bitrate: S.bitrate, codec: S.codec, bits10: S.bits10, [r.key]: prev };
    if (r.custom) { const hit = Object.entries(PRESETS).find(([, p]) => p.res === S.res && p.fps === S.fps && p.bitrate === S.bitrate); S.preset = hit ? hit[0] : "custom"; }
    if (!Object.keys(pending).some(k => pending[k] !== S[k])) pending = null;
  }
  r.onChange?.(v);
  if (r.key === "mic") { if (!S.mic !== E.micMuted) toggleMic(); }
  if (r.key === "gameAudio") { E.gameMuted = !S.gameAudio; syncTrackMutes(); }
}
function renderRow(r) {
  const val = r.hk ? S.hk[r.key] : S[r.key];
  const row = h("div", { class: "s-row" + (r.sub ? " is-sub" : ""), "data-key": r.key || r.type });
  if (r.when && !r.when()) row.classList.add("is-hidden");
  const desc = r.desc ? r.desc() : "";
  const head = `${r.glyph ? G(r.glyph) : ""}<div class="s-txt"><div class="s-lbl">${r.label ?? ""}${r.isNew ? '<span class="s-new">Новое</span>' : ""}</div>${desc ? `<div class="s-desc">${desc}</div>` : ""}</div>`;
  const ctl = h("div", { class: "s-ctl" });
  const later = () => setTimeout(refreshPane, 180);
  switch (r.type) {
    case "toggle": {
      row.innerHTML = head;
      if (r.meter) ctl.appendChild(h("span", { class: "meter", id: "sm-" + r.meter, style: "width:80px" }, "<i></i>"));
      const lab = h("span", { class: "tgl-state" }, val ? "Вкл." : "Откл.");
      const b = h("button", { class: "tgl" + (r.disabled?.() ? " is-disabled" : ""), role: "switch" }, "<span></span>");
      toggle(b, { get: () => S[r.key], set: v => { setVal(r, v); lab.textContent = v ? "Вкл." : "Откл."; later(); } });
      ctl.append(lab, b); break;
    }
    case "combo": {
      row.innerHTML = head; const b = h("button", { class: "combo", style: "min-width:200px" });
      combo(b, { options: () => r.options.map(([value, label, hint, disabled]) => ({ value, label, hint, disabled })), get: () => S[r.key], set: v => { setVal(r, v); refreshPane(); } });
      ctl.appendChild(b);
      if (r.play) { const p = h("button", { class: "btn btn-icon", "data-tip": "Послушать" }, G("E768")); p.onclick = () => notify({ glyph: "E767", neutral: true, title: "Звук сохранения", sub: "Здесь прозвучал бы выбранный сигнал", ms: 1800 }); ctl.appendChild(p); }
      break;
    }
    case "seg": {
      row.innerHTML = head; const s = h("div", { class: "seg" });
      segmented(s, { options: r.options.map(([value, label]) => ({ value, label })), get: () => S[r.key], set: v => { setVal(r, v); later(); } });
      ctl.appendChild(s); break;
    }
    case "radios": {
      row.innerHTML = head; const w = h("div", { class: "radios", role: "radiogroup" });
      r.options.forEach(([v, l]) => { const b = h("button", { class: "radio" + (S[r.key] === v ? " is-on" : ""), role: "radio" }, `<i></i>${l}`); b.onclick = () => { setVal(r, v); refreshPane(); }; w.appendChild(b); });
      ctl.appendChild(w); break;
    }
    case "slider": {
      row.innerHTML = head;
      const out = h("span", { class: "s-val" }, r.fmt(val));
      const inp = h("input", { type: "range", min: r.min, max: r.max, step: r.step, value: val });
      paintRange(inp);
      inp.oninput = () => { setVal(r, +inp.value); out.textContent = r.fmt(+inp.value); paintRange(inp); const d = $(".s-desc", row); if (d && r.desc) d.innerHTML = r.desc(); };
      inp.onchange = () => { if (QUALITY.includes(r.key)) refreshPane(); };
      ctl.append(out, inp); break;
    }
    case "hotkey": row.innerHTML = head; ctl.appendChild(hotkey(r, row)); break;
    case "folder": {
      row.innerHTML = `${G(r.glyph)}<div class="s-txt"><div class="s-lbl">${r.label}</div><div class="s-desc">${val}</div></div>`;
      const o = h("button", { class: "btn btn-subtle btn-icon", "data-tip": "Открыть в проводнике" }, G("E8A7")); o.onclick = () => notify({ glyph: "E838", neutral: true, title: "Открыто в проводнике", sub: val });
      const c = h("button", { class: "btn" }, "Обзор…"); c.onclick = () => notify({ glyph: "E8B7", neutral: true, title: "Здесь откроется выбор папки", sub: "Системный диалог Windows" });
      ctl.append(o, c); break;
    }
    case "text": {
      row.innerHTML = head;
      const inp = h("input", { class: "s-field", value: val, spellcheck: "false" });
      inp.oninput = () => { S[r.key] = inp.value; $(".s-desc", row).innerHTML = r.desc(); };
      const vars = h("button", { class: "btn btn-icon", "data-tip": "Подстановки" }, G("E710"));
      vars.onclick = () => openMenu([{ head: "Что можно вставить в имя" }, ...[["{game}", "Название игры"], ["{date}", "Дата, 2026-09-21"], ["{time}", "Время, 18-26-56"], ["{length}", "Длина, 3m00s"]].map(([k, l]) => ({ label: l, key: k, fn: () => { inp.value += " " + k; inp.oninput(); } }))], { anchor: vars, minWidth: 260 });
      ctl.append(inp, vars); break;
    }
    case "corner": {
      row.innerHTML = head; const c = h("div", { class: "corner", role: "radiogroup" });
      ["TL", "TC", "TR", "BL", "gap", "BR"].forEach(k => {
        const b = h("button", { class: k === "gap" ? "gap" : S.corner === k ? "is-on" : "", "data-tip": CORNERS[k] || null });
        if (k !== "gap") b.onclick = () => { S.corner = k; refreshPane(); previewInGame(); };
        c.appendChild(b);
      });
      ctl.appendChild(c); break;
    }
    case "preview": {
      row.innerHTML = head;
      const a = h("button", { class: "btn" }, `${G("E8B2")}Повтор`), b = h("button", { class: "btn" }, `${G("E7A8")}Скриншот`);
      a.onclick = () => previewInGame("replay"); b.onclick = () => previewInGame("shot"); ctl.append(a, b); break;
    }
    case "disk": {
      const games = Object.keys(GAMES).map(k => ({ k, b: CLIPS.filter(c => c.game === k).reduce((s, c) => s + c.bytes, 0) })).filter(x => x.b).sort((a, b) => b.b - a.b);
      const total = games.reduce((s, x) => s + x.b, 0), shades = PAL.light ? ["#0F7B45", "#4F8F6D", "#8AAE9A", "#B9CCC1", "#DCE5E0"] : ["#56D38F", "#3E9A69", "#2E6F4E", "#255239", "#1D3B2C"];
      const top = games.slice(0, 4), rest = games.slice(4).reduce((s, x) => s + x.b, 0);
      const parts = [...top.map((x, i) => ({ l: GAMES[x.k], b: x.b, c: shades[i] })), ...(rest ? [{ l: "Остальные", b: rest, c: shades[4] }] : [])];
      row.innerHTML = `${G("EDA2")}<div class="s-txt"><div style="display:flex;align-items:baseline;gap:12px"><div class="s-lbl">Записи занимают ${fmtTotal(total)}</div><div class="cmd-fill"></div><div class="s-desc">свободно 394,6 ГБ, это ≈ ${Math.round(FREE / (clipMb(3600) * MIB))} ч записи</div></div>
        <div class="disk-bar">${parts.map(p => `<i style="flex:${p.b} 0 0;background:${p.c}"></i>`).join("")}<i style="flex:${FREE * .02} 0 0;background:var(--fill-ctl-2)"></i></div>
        <div class="disk-legend">${parts.map(p => `<span><i style="background:${p.c}"></i>${p.l} ${fmtTotal(p.b)}</span>`).join("")}</div></div>`;
      break;
    }
    case "version": {
      row.innerHTML = `${G("E895")}<div class="s-txt"><div class="s-lbl">Aura 1.1.105</div><div class="s-desc" id="upd">Проверено сегодня в 16:04</div></div>`;
      const b = h("button", { class: "btn" }, "Проверить обновления");
      b.onclick = () => { b.textContent = "Проверяю…"; b.classList.add("is-disabled"); setTimeout(() => { b.textContent = "Проверить обновления"; b.classList.remove("is-disabled"); $("#upd").textContent = "Установлена последняя версия"; }, 1100); };
      ctl.appendChild(b); break;
    }
    case "button": { row.innerHTML = head; const b = h("button", { class: "btn" + (r.danger ? " btn-danger" : "") }, r.btn); b.onclick = e => r.fn(e); ctl.appendChild(b); break; }
    case "gpu": {
      row.innerHTML = `${G("E9F5")}<div class="s-txt"><div class="s-lbl">NVIDIA GeForce RTX 3070</div><div class="s-desc">8 ГБ, драйвер 616.64. Кодирует NVENC, процессор почти не занят</div></div>`;
      const b = h("button", { class: "btn" }, `${G("EA18")}Проверить драйвер`);
      b.onclick = () => { b.classList.add("is-disabled"); setTimeout(() => { b.classList.remove("is-disabled"); notify({ glyph: "E73E", title: "Драйвер свежий", sub: "616.64, обновлений нет" }); }, 900); };
      ctl.appendChild(b); break;
    }
    case "live": {
      row.innerHTML = `${G("EC4A")}<div class="live"><div><div class="lv-l">Захват</div><div class="lv-v"><span id="lv-cap">${S.fps},0</span><small>к/с</small></div><canvas id="sp-cap"></canvas></div><div><div class="lv-l">Кодирование</div><div class="lv-v"><span id="lv-enc">${S.fps},0</span><small>к/с</small></div><canvas id="sp-enc"></canvas></div><div><div class="lv-l">Память Aura</div><div class="lv-v"><span id="lv-mem">1015</span><small>МБ</small></div><canvas id="sp-mem"></canvas></div></div>`;
      break;
    }
    case "info": row.innerHTML = `<div class="s-txt"><div class="s-lbl" style="color:var(--tx2)">${r.label}</div></div>`; ctl.innerHTML = `<span>${r.value}</span>`; break;
  }
  if (!["disk", "live"].includes(r.type)) row.appendChild(ctl);
  return row;
}
/* горячие клавиши */
let listening = null;
function hotkey(r, row) {
  const b = h("button", { class: "hk" });
  const descEl = () => $(".s-desc", row) || (() => { const d = h("div", { class: "s-desc" }); $(".s-txt", row).appendChild(d); return d; })();
  const base = r.desc();
  const paint = (text, cls) => {
    const v = S.hk[r.key];
    b.className = "hk" + (cls ? " " + cls : "");
    b.innerHTML = text ? `<span class="none">${text}</span>` : v ? `<span class="keyhint">${kbds(v.replace(/\+/g, " "))}</span><span class="hk-x" data-tip="Убрать">&#xE711;</span>` : `<span class="none">Не задано</span>`;
    const x = $(".hk-x", b); if (x) x.onclick = e => { e.stopPropagation(); S.hk[r.key] = ""; paint(); syncHotkeys(); };
  };
  b.onclick = () => {
    if (listening) listening.cancel();
    paint("Нажмите сочетание…", "is-listen");
    descEl().innerHTML = "Esc, чтобы отменить, Backspace, чтобы убрать";
    listening = {
      key(e) {
        e.preventDefault();
        if (e.key === "Escape") return this.cancel();
        if (e.key === "Backspace") { S.hk[r.key] = ""; return this.done(); }
        if (["Control", "Shift", "Alt", "Meta"].includes(e.key)) return;
        const combo = comboOf(e);
        const dupe = Object.entries(S.hk).find(([k, v]) => v === combo && k !== r.key), taken = HK_TAKEN[combo];
        S.hk[r.key] = combo; if (dupe) S.hk[dupe[0]] = "";
        this.done(dupe ? `<span class="warn">Было у «${HK_LABELS[dupe[0]]}», там снято</span>` : taken ? `<span class="warn">${taken}. Лучше выбрать другое</span>` : null, !!dupe);
      },
      cancel() { listening = null; paint(); descEl().innerHTML = base; },
      done(msg, refresh) { listening = null; paint(null, msg ? "is-warn" : ""); descEl().innerHTML = msg || base; syncHotkeys(); if (refresh) setTimeout(refreshPane, 1800); },
    };
  };
  paint(); return b;
}
function syncHotkeys() {
  $$("#btn-save .keyhint, #qp-save .keyhint").forEach(e => e.innerHTML = kbds((S.hk.save || "").replace(/\+/g, " ")));
  $("#ts-save").dataset.kbd = (S.hk.save || "").replace(/\+/g, " ");
  $("#btn-shot").dataset.kbd = (S.hk.area || "").replace(/\+/g, " ");
  $("#nav-overlay").dataset.kbd = (S.hk.panel || "").replace(/\+/g, " ");
  $("#btn-rec").dataset.tip = S.hk.recStart ? "Начать запись" : "Клавиша не назначена";
  $("#btn-rec").dataset.kbd = (S.hk.recStart || "").replace(/\+/g, " ");
  $(".qp-head .keyhint").innerHTML = kbds((S.hk.panel || "").replace(/\+/g, " "));
  paintQpTiles();
}
/* поиск по параметрам */
$("#set-search").oninput = runSearch;
function runSearch() {
  const q = $("#set-search").value.trim().toLowerCase(); setPill();
  $$(".set-cnt").forEach(c => c.remove());
  if (!q) { selectPane(pane, true); return; }
  const page = $("#set-page");
  page.innerHTML = `<div class="set-inner"><div class="set-title"><span class="crumb">Поиск</span>${G("E76C")}<span>«${$("#set-search").value.trim()}»</span></div>${infobarHtml()}</div>`;
  const inner = $(".set-inner", page); let found = 0;
  const text = (r, g, p) => (r.label + " " + (r.desc ? r.desc().replace(/<[^>]+>/g, "") : "") + " " + g.title + " " + p.title).toLowerCase();
  PANES.forEach(p => {
    let n = 0;
    p.groups.forEach(g => {
      const rows = g.rows.filter(r => r.label && text(r, g, p).includes(q)); if (!rows.length) return;
      found += rows.length; n += rows.length;
      const grp = renderGroup({ title: `${p.title}: ${g.title.toLowerCase()}` }, rows);
      const re = new RegExp(`(${q.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")})`, "i");
      $$(".s-lbl", grp).forEach(l => l.innerHTML = l.innerHTML.replace(re, "<mark>$1</mark>"));
      inner.appendChild(grp);
    });
    if (n) $(`.set-item[data-pane="${p.id}"]`).appendChild(h("span", { class: "set-cnt" }, n));
  });
  bindInfobar();
  if (!found) inner.insertAdjacentHTML("beforeend", `<div class="det-empty" style="height:auto;padding:48px">${G("E721")}<div>Такого параметра нет. Попробуйте «битрейт», «микрофон», «папка» или «клавиша»</div></div>`);
}
function applyTheme() {
  const t = S.theme === "system" ? (matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark") : S.theme;
  document.documentElement.dataset.theme = t; readPalette();
}
function previewInGame(kind = "replay") {
  enterGame(true);
  const c = CLIPS.find(c => c.kind === (kind === "shot" ? "shot" : "video")) || CLIPS[0];
  const host = $("#game-toasts");
  const pos = { TL: "top:28px;left:28px;right:auto;bottom:auto", TC: "top:28px;left:50%;right:auto;bottom:auto;transform:translateX(-50%)", TR: "top:28px;right:28px;left:auto;bottom:auto", BL: "bottom:28px;left:28px;right:auto;top:auto", BR: "bottom:28px;right:28px;left:auto;top:auto" }[S.corner];
  host.setAttribute("style", pos);
  setTimeout(() => {
    gameToast({ thumb: c ? poster(c) : null, title: kind === "shot" ? "Скриншот скопирован" : "Повтор сохранён", sub: kind === "shot" ? "В буфере обмена" : `Counter-Strike 2, ${fmtDur(E.L)}, ${Math.round(clipMb(E.L))} МБ` });
    setTimeout(() => { host.removeAttribute("style"); if (GAME.preview) leaveGame(); }, S.notifySec * 1000 + 500);
  }, 350);
}

/* =====================================================================
   Игра и панель Aura поверх неё
   ===================================================================== */
const GAME = { on: false, preview: false, focus: 0 };
const QP = [
  { label: "Запись", fn: toggleRec },
  { label: "Микрофон", fn: toggleMic },
  { label: "Повтор", fn: () => E.replay === "off" ? startReplay() : stopReplay() },
  { label: "Скриншот", fn: screenshot },
];
function enterGame(preview) {
  GAME.on = true; GAME.preview = !!preview; closeMenu();
  $("#game").classList.add("is-on"); $("#game").setAttribute("aria-hidden", "false");
  $("#game-hint").hidden = !!preview;
  setActive(false);
  $("#tb-game").classList.add("is-focused");
}
function leaveGame() {
  GAME.on = false; GAME.preview = false; closePanel();
  $("#game").classList.remove("is-on"); $("#game").setAttribute("aria-hidden", "true"); $("#game-toasts").innerHTML = "";
  $("#tb-game").classList.remove("is-focused");
  if (windowVisible()) setActive(true);
}
function openPanel() { const q = $("#qp"); q.classList.add("is-on"); GAME.focus = 0; paintQpTiles(); paintQpFocus(); }
function closePanel() { $("#qp").classList.remove("is-on"); $("#game-toasts").style.top = ""; }
function togglePanel() {
  if (!GAME.on) { enterGame(); openPanel(); return; }
  $("#qp").classList.contains("is-on") ? closePanel() : openPanel();
}
function paintQpTiles() {
  const grid = $("#qp-grid");
  if (!grid.childElementCount) QP.forEach((t, i) => { const b = h("button", { class: "qp-tile", "data-i": i + 1 }); b.onclick = () => { t.fn(); GAME.focus = i + 1; paintQpFocus(); }; b.onpointerenter = () => { GAME.focus = i + 1; paintQpFocus(); }; grid.appendChild(b); });
  const [rec, mic, rep, shot] = grid.children;
  const set = (b, g, l, s, cls) => { b.className = "qp-tile" + (cls ? " " + cls : "") + (GAME.focus === +b.dataset.i ? " is-focus" : ""); b.innerHTML = `<span class="qt-n">${b.dataset.i}</span>${G(g)}<span class="qt-l">${l}</span><span class="qt-s">${s}</span>`; };
  set(rec, E.rec ? "E71A" : "E7C8", E.rec ? "Стоп" : "Запись", E.rec ? fmtDur(E.t - E.recStart) : (S.hk.recStart || "выкл."), E.rec ? "is-rec" : "");
  set(mic, E.micMuted ? "EC54" : "E720", "Микрофон", E.micMuted ? "выкл." : "вкл.", E.micMuted ? "is-off" : "");
  set(rep, "E81C", "Повтор", E.replay === "off" ? "выкл." : fmtDur(Math.min(E.L, E.t - (E.fillStart ?? E.t))), E.replay === "on" ? "is-on" : E.replay === "off" ? "is-off" : "");
  set(shot, "E7A8", "Скриншот", S.hk.area || "область");
}
function paintQpFocus() { $$(".qp-tile").forEach(b => b.classList.toggle("is-focus", GAME.focus === +b.dataset.i)); $("#qp-save").classList.toggle("is-focus", GAME.focus === 0); }
$("#tb-game").onclick = () => GAME.on ? leaveGame() : enterGame();
$("#nav-overlay").onclick = togglePanel;
$("#qp-open").onclick = () => { leaveGame(); restore(); setView("capture"); };
$("#game").addEventListener("pointerdown", e => { if (!e.target.closest(".qp, .gtoast") && $("#qp").classList.contains("is-on")) closePanel(); });
function gameKey(e) {
  const panel = $("#qp").classList.contains("is-on");
  if (e.key === "Escape") { panel ? closePanel() : leaveGame(); return true; }
  if (!panel) return false;
  if (["1", "2", "3", "4"].includes(e.key)) { GAME.focus = +e.key; QP[+e.key - 1].fn(); paintQpFocus(); return true; }
  if (e.key === "ArrowRight") { GAME.focus = GAME.focus === 0 ? 1 : Math.min(4, GAME.focus + 1); paintQpFocus(); return true; }
  if (e.key === "ArrowLeft") { GAME.focus = Math.max(GAME.focus === 1 ? 1 : 0, GAME.focus - 1); paintQpFocus(); return true; }
  if (e.key === "ArrowDown") { if (!GAME.focus) GAME.focus = 1; paintQpFocus(); return true; }
  if (e.key === "ArrowUp") { GAME.focus = 0; paintQpFocus(); return true; }
  if (e.key === "Enter" || e.key === " ") { GAME.focus ? QP[GAME.focus - 1].fn() : saveReplay(); paintQpFocus(); return true; }
  return false;
}

/* =====================================================================
   Клавиатура
   ===================================================================== */
function comboOf(e) {
  const mods = [e.ctrlKey && "Ctrl", e.altKey && "Alt", e.shiftKey && "Shift", e.metaKey && "Win"].filter(Boolean);
  let k = e.key.length === 1 ? e.key.toUpperCase() : e.key;
  if (k === " ") k = "Space"; if (e.code?.startsWith("Key")) k = e.code.slice(3); if (e.code?.startsWith("Digit")) k = e.code.slice(5);
  return [...mods, k].join("+");
}
document.addEventListener("keydown", e => {
  if (listening) { listening.key(e); return; }
  if (MENU && MENU.key(e)) { e.preventDefault(); return; }
  const c = comboOf(e), typing = e.target instanceof Element && e.target.matches("input, textarea");
  // глобальные клавиши Aura работают везде, как в приложении
  if (c === S.hk.save) { e.preventDefault(); saveReplay(); return; }
  if (c === S.hk.panel) { e.preventDefault(); togglePanel(); return; }
  if (c === S.hk.area && !typing) { e.preventDefault(); screenshot(); return; }
  if (S.hk.mark && c === S.hk.mark && E.replay !== "off") { E.markers.push({ type: "mark", a: E.t }); notify({ glyph: "E7C1", neutral: true, title: "Метка поставлена", sub: "Найдёте её на таймлайне и в редакторе", ms: 1800 }); return; }
  if (GAME.on) { if (gameKey(e)) e.preventDefault(); return; }
  if (!windowVisible() || !W.active) return;
  if (e.ctrlKey && e.key === "1") { e.preventDefault(); setView("capture"); return; }
  if (e.ctrlKey && e.key === "2") { e.preventDefault(); setView("clips"); return; }
  if (e.ctrlKey && (e.key === "," || e.code === "Comma")) { e.preventDefault(); setView("settings"); return; }
  if (typing) { if (e.key === "Escape") e.target.blur(); return; }
  if (view === "capture" && e.key === "Escape" && E.sel) { E.sel = null; paintSaveButtons(); return; }
  if (view === "clips") {
    const one = CL.sel.size === 1 ? byId([...CL.sel][0]) : null;
    if (e.ctrlKey && e.code === "KeyF") { e.preventDefault(); $("#clip-search").focus(); return; }
    if (e.ctrlKey && e.code === "KeyA") { e.preventDefault(); selectAll(); return; }
    if (e.altKey && e.shiftKey && e.code === "KeyP") { e.preventDefault(); CL.details = !CL.details; renderClips(); return; }
    if (e.key === "Escape" && CL.sel.size) { CL.sel.clear(); paintSel(); return; }
    if (e.key === "Delete" && CL.sel.size) { removeClips([...CL.sel]); return; }
    if (e.key === "F2" && one) { e.preventDefault(); if (!CL.details) { CL.details = true; renderClips(); } startRename(); return; }
    if (e.key === "Enter" && one) { openClip(one); return; }
    if (e.ctrlKey && e.code === "KeyE" && one && one.kind === "video") { e.preventDefault(); openEditor(one); return; }
    if (e.ctrlKey && e.code === "KeyC" && CL.sel.size) { copyClip(one || byId([...CL.sel][0]), CL.sel.size); return; }
    if (e.key.startsWith("Arrow")) { e.preventDefault(); moveSel(e.key); return; }
  }
  if (view === "settings" && e.ctrlKey && e.code === "KeyF") { e.preventDefault(); $("#set-search").focus(); }
});
function moveSel(key) {
  const els = $$("#gallery [data-id]"); if (!els.length) return;
  const cur = els.find(x => +x.dataset.id === CL.anchor) || els.find(x => CL.sel.has(+x.dataset.id));
  let next;
  if (!cur) next = els[0];
  else {
    const r = cur.getBoundingClientRect(), cx = r.left + r.width / 2, i = els.indexOf(cur);
    if (key === "ArrowRight") next = els[i + 1];
    else if (key === "ArrowLeft") next = els[i - 1];
    else {
      const down = key === "ArrowDown";
      next = els.filter(x => { const q = x.getBoundingClientRect(); return down ? q.top > r.bottom - 4 : q.bottom < r.top + 4; })
        .sort((a, b) => { const qa = a.getBoundingClientRect(), qb = b.getBoundingClientRect(); return (Math.abs(qa.top - r.top) - Math.abs(qb.top - r.top)) * 4 + Math.abs(qa.left + qa.width / 2 - cx) - Math.abs(qb.left + qb.width / 2 - cx); })[0];
    }
  }
  if (!next) return;
  CL.sel.clear(); CL.sel.add(+next.dataset.id); CL.anchor = +next.dataset.id; paintSel();
  next.scrollIntoView({ block: "nearest" });
}

/* =====================================================================
   Состояния прототипа
   ===================================================================== */
const DEMO = { empty: false, backup: null };
function renderDemo() {
  const p = $("#demo-panel");
  p.innerHTML = `<div class="demo-row"><span>Повтор</span><div class="seg" id="d-rep"></div></div>
    <div class="demo-row"><span>Набор буфера с нуля</span><button class="btn btn-sm" id="d-fill">Начать</button></div>
    <div class="demo-row"><span>Пустая библиотека</span><button class="tgl" id="d-empty" role="switch"><span></span></button></div>
    <div class="demo-row"><span>Время быстрее ×8</span><button class="tgl" id="d-speed" role="switch"><span></span></button></div>
    <div class="demo-row"><span>Тема</span><div class="seg" id="d-theme"></div></div>
    <div class="demo-note">${S.hk.save} сохранить, ${S.hk.area} скриншот, ${S.hk.panel.replace("+", " ")} панель в игре. Окно двигается за заголовок, тянется за края, закрывается в трей. Значок в трее: щелчок и правая кнопка.</div>`;
  segmented($("#d-rep"), { options: [{ value: "on", label: "Идёт" }, { value: "off", label: "Выкл." }, { value: "error", label: "Сбой" }], get: () => E.replay, set: v => {
    if (v === "on") { if (E.replay === "off") startReplay(); else { E.replay = "on"; E.errorStart = null; } }
    else if (v === "off") stopReplay();
    else { if (E.replay === "off") startReplay(); E.replay = "error"; E.errorStart = E.t; }
    paintState(); renderInspector();
  } });
  $("#d-fill").onclick = () => { if (E.replay === "off") startReplay(); E.replay = "on"; E.errorStart = null; E.fillStart = E.t - 20; E.markers = []; E.sel = null; paintState(); renderInspector(); $("#d-rep")._paint(true); };
  toggle($("#d-empty"), { get: () => DEMO.empty, set: v => { DEMO.empty = v; if (v) { DEMO.backup = CLIPS; CLIPS = []; } else CLIPS = DEMO.backup || CLIPS; CL.sel.clear(); if (view === "clips") renderClips(); renderInspector(); } });
  toggle($("#d-speed"), { get: () => E.speed > 1, set: v => { E.speed = v ? 8 : 1; } });
  segmented($("#d-theme"), { options: [{ value: "dark", label: "Тёмная" }, { value: "light", label: "Светлая" }], get: () => document.documentElement.dataset.theme, set: v => { S.theme = v; applyTheme(); if (view === "settings") refreshPane(); if (view === "clips") renderClips(); } });
}
$("#demo-btn").onclick = () => { const d = $("#demo"); d.classList.toggle("is-open"); if (d.classList.contains("is-open")) renderDemo(); };
document.addEventListener("pointerdown", e => { if (!e.target.closest("#demo") && !e.target.closest(".menu")) $("#demo").classList.remove("is-open"); });

/* =====================================================================
   Цикл
   ===================================================================== */
const sparks = { cap: [], enc: [], mem: [] };
let last = performance.now(), secAcc = 0, lightAcc = 0;
function layoutChanged() { /* холсты подстраиваются сами в каждом кадре */ movePill(); }
function frame(now) {
  const dt = Math.min(1, (now - last) / 1000); last = now;
  E.t += dt * E.speed;
  if (E.sel && E.sel.b != null && E.sel.b < E.t - E.L) { E.sel = null; paintSaveButtons(); }
  if (E.sel && E.sel.a < E.t - E.L) E.sel.a = E.t - E.L;
  E.markers = E.markers.filter(m => (m.b ?? m.a) > E.t - 900);
  secAcc += dt; lightAcc += dt;
  if (!document.hidden) {
    if (GAME.on && $("#qp").classList.contains("is-on")) drawTimeline($("#qp-tape"), true);
    if (view === "capture" && windowVisible()) { drawTimeline(tlCv, false); paintMeters(); paintMonitor(); }
    if (view === "settings" && pane === "audio") { const g = $("#sm-game i"), m = $("#sm-mic i"); if (g) g.style.width = (S.gameAudio ? gameAmp(E.t) : 0) * 100 + "%"; if (m) m.style.width = (S.mic && !E.micMuted ? micAmp(E.t) : 0) * 100 + "%"; }
  }
  if (lightAcc >= .25) { lightAcc = 0; if (E.rec || (E.fillStart != null && E.t - E.fillStart < E.L + 1)) paintState(); }
  if (secAcc >= 1) {
    secAcc = 0; paintState();
    sparks.cap.push(E.replay === "on" ? S.fps - hash(E.t) * .4 : 0); sparks.enc.push(E.replay === "off" ? 0 : S.fps - hash(E.t + 1) * .2); sparks.mem.push(990 + hash(E.t + 2) * 60);
    for (const k in sparks) if (sparks[k].length > 60) sparks[k].shift();
    if (view === "settings" && pane === "system") paintSparks();
  }
  requestAnimationFrame(frame);
}
function paintSparks() {
  const one = (id, arr, min, max, fmt) => {
    const cv = $("#sp-" + id); if (!cv) return;
    const { ctx, W: w, H } = fitCanvas(cv);
    ctx.strokeStyle = PAL.acc; ctx.lineWidth = 1.5; ctx.beginPath();
    arr.forEach((v, i) => { const x = w - (arr.length - 1 - i) * (w / 59), y = H - 2 - (clamp(v, min, max) - min) / (max - min) * (H - 4); i ? ctx.lineTo(x, y) : ctx.moveTo(x, y); });
    ctx.stroke(); const el = $("#lv-" + id); if (el && arr.length) el.textContent = fmt(arr.at(-1));
  };
  one("cap", sparks.cap, 0, S.fps + 2, v => num(v, 1)); one("enc", sparks.enc, 0, S.fps + 2, v => num(v, 1)); one("mem", sparks.mem, 900, 1100, v => Math.round(v));
}

/* =====================================================================
   Запуск
   ===================================================================== */
readPalette();
paintKeyhints();
place();
// на широком окне меню раскрыто с подписями, на узком остаются значки
if (W.w >= 1200) { $("#navpane").classList.add("is-open"); $("#nav-toggle").dataset.tip = "Свернуть меню"; }
setView("capture");
paintState();
paintMonitor(true);
syncTrackMutes();
paintRange($("#thumb-size"));
document.fonts?.ready.then(() => { movePill(); readPalette(); });
requestAnimationFrame(frame);
