/* Aura, интерактивный прототип. Без зависимостей и сборки.
   Данные условные, но собраны из настоящих: имена игр, даты и размеры файлов
   взяты из папки записей, параметры захвата из settings.json. */
"use strict";

/* =====================================================================
   Иконки. Контуры из Theme/Icons.xaml (квадрат 24×24, обводка), плюс
   несколько новых в той же манере.
   ===================================================================== */
const ICONS = {
  deck: '<rect x="3" y="5.5" width="18" height="13" rx="3"/><path d="M15.5 5.5v13"/><path d="M6.4 12h.9l1.2-2.6 1.6 5.2 1.4-3.6.9 1h.4"/>',
  film: '<path d="M6 5h12a3 3 0 0 1 3 3v8a3 3 0 0 1-3 3H6a3 3 0 0 1-3-3V8a3 3 0 0 1 3-3 M8 5v14 M16 5v14"/>',
  cog: '<circle cx="12" cy="12" r="6.9"/><circle cx="12" cy="12" r="2.7"/><path d="M12 2.6v2.5 M12 18.9v2.5 M21.4 12h-2.5 M5.1 12H2.6 M18.65 5.35l-1.77 1.77 M7.12 16.88l-1.77 1.77 M18.65 18.65l-1.77-1.77 M7.12 7.12 5.35 5.35"/>',
  panel: '<rect x="3" y="4.5" width="18" height="15" rx="3"/><rect x="12.4" y="7.6" width="5.6" height="6" rx="1.4"/>',
  save: '<path d="M12 3.5v10.8 M12 14.3l4.2-4.2 M12 14.3 7.8 10.1 M4.5 16v2.2a2.3 2.3 0 0 0 2.3 2.3h10.4a2.3 2.3 0 0 0 2.3-2.3V16"/>',
  crop: '<path d="M7 3.5V17h13.5 M3.5 7H17v13.5"/>',
  camera: '<path d="M3 9.2a2.7 2.7 0 0 1 2.7-2.7h1.6l1.2-2h7l1.2 2h1.6a2.7 2.7 0 0 1 2.7 2.7v8.1a2.7 2.7 0 0 1-2.7 2.7H5.7A2.7 2.7 0 0 1 3 17.3z"/><circle cx="12" cy="13" r="3.3"/>',
  rec: '<circle cx="12" cy="12" r="7.6"/><circle cx="12" cy="12" r="3.4"/>',
  play: '<path class="solid" d="M7.5 5.2c0-.9 1-1.5 1.8-1L18.6 11c.7.5.7 1.5 0 2l-9.3 6.8c-.8.5-1.8 0-1.8-1z"/>',
  folder: '<path d="M3 7.6A2.6 2.6 0 0 1 5.6 5h3.1l2 2.7h7.7A2.6 2.6 0 0 1 21 10.3v6.9a2.6 2.6 0 0 1-2.6 2.6H5.6A2.6 2.6 0 0 1 3 17.2z"/>',
  folderOpen: '<path d="M3 9.4V7.6A2.6 2.6 0 0 1 5.6 5h3.1l2 2.7h7.7A2.6 2.6 0 0 1 21 10.3v1.1 M3 9.4h17.1a1.2 1.2 0 0 1 1.2 1.5l-1.9 6.7a2.2 2.2 0 0 1-2.1 1.6H5.2A2.2 2.2 0 0 1 3 17.1z"/>',
  search: '<circle cx="10.6" cy="10.6" r="6.4"/><path d="M15.4 15.4l4.4 4.4"/>',
  chev: '<path d="M9.5 5.5l7 6.5-7 6.5"/>',
  chevDown: '<path d="M6.5 9.5l5.5 5.5 5.5-5.5"/>',
  check: '<path d="M4.8 12.6l4.8 4.8L19.4 6.8"/>',
  x: '<path d="M6 6l12 12 M18 6 6 18"/>',
  info: '<circle cx="12" cy="12" r="8.6"/><path d="M12 11.2v5.4 M12 7.9h.01"/>',
  alert: '<path d="M10.3 4.2 2.9 17.4c-.7 1.2.2 2.6 1.6 2.6h15c1.4 0 2.3-1.4 1.6-2.6L13.7 4.2a2 2 0 0 0-3.4 0z"/><path d="M12 9.2v4.4 M12 17.2h.01"/>',
  disk: '<rect x="3" y="13" width="18" height="6.5" rx="2.2"/><path d="M5.6 13 8 5.6A2.4 2.4 0 0 1 10.3 4h3.4a2.4 2.4 0 0 1 2.3 1.6L18.4 13 M7 16.2h.01 M10.4 16.2h.01"/>',
  ram: '<rect x="2.6" y="7" width="18.8" height="8.4" rx="2"/><path d="M6.2 15.4V19 M10 15.4V19 M14 15.4V19 M17.8 15.4V19"/>',
  monitor: '<rect x="2.6" y="4.2" width="18.8" height="12.4" rx="2.6"/><path d="M8.6 20h6.8 M12 16.6V20"/>',
  mic: '<rect x="9.2" y="2.8" width="5.6" height="10.6" rx="2.8"/><path d="M5.8 11.4a6.2 6.2 0 0 0 12.4 0 M12 17.8V21"/>',
  micOff: '<rect x="9.2" y="2.8" width="5.6" height="10.6" rx="2.8"/><path d="M5.8 11.4a6.2 6.2 0 0 0 12.4 0 M12 17.8V21 M4 4l16 16"/>',
  speaker: '<path d="M4 9.4h3.4L12 5.6v12.8L7.4 14.6H4z M15.4 9.8a3.4 3.4 0 0 1 0 4.4 M18 7.4a7 7 0 0 1 0 9.2"/>',
  trash: '<path d="M4.2 6.6h15.6 M9.6 6.6V4.4h4.8v2.2 M6.6 6.6 7.7 19a1.8 1.8 0 0 0 1.8 1.6h5a1.8 1.8 0 0 0 1.8-1.6l1.1-12.4"/>',
  bell: '<path d="M6.4 10a5.6 5.6 0 0 1 11.2 0c0 4.4 1.6 5.8 1.6 5.8H4.8s1.6-1.4 1.6-5.8z M10.2 19a2 2 0 0 0 3.6 0"/>',
  pencil: '<path d="M4.4 19.6l.8-4 11-11a2.2 2.2 0 0 1 3.2 3.2l-11 11z"/><path d="M14.6 6.2l3.2 3.2"/>',
  copy: '<rect x="9" y="9" width="11" height="11" rx="2.4"/><path d="M5.6 15h-.4A1.7 1.7 0 0 1 3.5 13.3V5.2A1.7 1.7 0 0 1 5.2 3.5h8.1A1.7 1.7 0 0 1 15 5.2v.4"/>',
  box: '<path d="M3.4 7.7 12 3.4l8.6 4.3v8.6L12 20.6 3.4 16.3z"/><path d="M3.4 7.7 12 12l8.6-4.3 M12 12v8.6"/>',
  scissors: '<circle cx="6.2" cy="6.2" r="2.4"/><circle cx="6.2" cy="17.8" r="2.4"/><path d="M8.3 7.6 19.5 18 M19.5 6 8.3 16.4"/>',
  keyboard: '<rect x="2.5" y="6.5" width="19" height="11" rx="2.6"/><path d="M6.5 10h.01 M10 10h.01 M13.5 10h.01 M17 10h.01 M8.5 14h7"/>',
  cpu: '<rect x="6.5" y="6.5" width="11" height="11" rx="2.4"/><path d="M10 3.2v3.3 M14 3.2v3.3 M10 17.5v3.3 M14 17.5v3.3 M3.2 10h3.3 M3.2 14h3.3 M17.5 10h3.3 M17.5 14h3.3"/>',
  app: '<rect x="3.2" y="4.4" width="17.6" height="15.2" rx="3"/><path d="M3.2 9.2h17.6 M6.6 6.8h.01 M9.5 6.8h.01"/>',
  replay: '<path d="M3.8 12a8.2 8.2 0 1 0 2.7-6.1"/><path d="M3.6 4.2v4.6h4.6"/><path d="M10.4 9.6c0-.5.55-.82 1-.55l3.9 2.4c.42.26.42.84 0 1.1l-3.9 2.4c-.45.27-1-.05-1-.55z"/>',
  sliders: '<path d="M4 8h6 M14.5 8H20 M4 16h10 M18 16h2"/><circle cx="12.2" cy="8" r="2.2"/><circle cx="16" cy="16" r="2.2"/>',
  grid: '<rect x="4" y="4" width="7" height="7" rx="1.8"/><rect x="13" y="4" width="7" height="7" rx="1.8"/><rect x="4" y="13" width="7" height="7" rx="1.8"/><rect x="13" y="13" width="7" height="7" rx="1.8"/>',
  list: '<path d="M9 6.5h11 M9 12h11 M9 17.5h11 M4.5 6.5h.01 M4.5 12h.01 M4.5 17.5h.01"/>',
  mark: '<path d="M7 4h10v16l-5-3.6L7 20z"/>',
  external: '<path d="M13.8 4.2h6v6 M19.8 4.2 11.4 12.6 M17.2 14.2v4a1.8 1.8 0 0 1-1.8 1.8H5.8A1.8 1.8 0 0 1 4 18.2V8.6a1.8 1.8 0 0 1 1.8-1.8h4"/>',
  undo: '<path d="M3.8 12a8.2 8.2 0 1 0 2.7-6.1 M3.6 4.2v4.6h4.6"/>',
  refresh: '<path d="M20.2 12a8.2 8.2 0 1 1-2.7-6.1 M20.4 4.2v4.6h-4.6"/>',
  shield: '<path d="M12 3.2 19.6 6v6c0 4.5-3.1 7.6-7.6 8.8C7.5 19.6 4.4 16.5 4.4 12V6z M9 12l2.2 2.2 4-4.2"/>',
  power: '<path d="M17.2 6.8a7.4 7.4 0 1 1-10.4 0"/><path d="M12 2.8v8.4"/>',
  file: '<path d="M13.4 3.4H7a2.2 2.2 0 0 0-2.2 2.2v12.8A2.2 2.2 0 0 0 7 20.6h10a2.2 2.2 0 0 0 2.2-2.2V9.2z"/><path d="M13.4 3.4v5.8h5.8"/>',
  wave: '<path d="M3 12h2 M7 8v8 M11 5v14 M15 9v6 M19 11v2"/>',
};
(function mountIcons() {
  const defs = document.getElementById("icon-defs");
  defs.innerHTML = Object.entries(ICONS).map(([k, v]) =>
    `<symbol id="i-${k}" viewBox="0 0 24 24">${v.replace('class="solid"', 'fill="currentColor" stroke="none"')}</symbol>`).join("");
})();
const ico = (name, cls = "i") => `<svg class="${cls}" aria-hidden="true"><use href="#i-${name}"/></svg>`;

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
function fmtSizeTotal(bytes) {
  const mb = bytes / MIB;
  return mb < 1024 ? num(mb, 1) + " МБ" : num(mb / 1024, 2) + " ГБ";
}
function fmtDur(s) {
  s = Math.max(0, Math.floor(s));
  const hh = Math.floor(s / 3600), mm = Math.floor(s / 60) % 60, ss = s % 60;
  return hh ? `${hh}:${String(mm).padStart(2, "0")}:${String(ss).padStart(2, "0")}` : `${mm}:${String(ss).padStart(2, "0")}`;
}
const MONTHS = ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];
const TODAY = new Date(2026, 8, 21);
function dayKey(d) { return `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`; }
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
const keysHtml = combo => combo ? combo.split(/[ +]/).filter(Boolean).map(k => `<kbd>${k}</kbd>`).join("") : "";
function paintKbds(root = document) { $$(".kbd-set[data-keys]", root).forEach(e => e.innerHTML = keysHtml(e.dataset.keys)); }

/* =====================================================================
   Данные
   ===================================================================== */
const GAMES = {
  cs2: "Counter-Strike 2",
  iwbtc: "I Wanna Be The Co-op v1.69.6",
  dmc5: "Devil May Cry 5",
  dl: "Dying Light",
  gta: "Grand Theft Auto V",
  dbd: "Dead by Daylight",
  fc: "EA SPORTS FC 26",
};
const GAME_FOLDER = { cs2: "Counter-strike 2", iwbtc: "I Wanna Be The Co-op v1.69.6", dmc5: "Devil May Cry 5", dl: "Dying Light", gta: "Grand Theft Auto V", dbd: "Dead by Daylight", fc: "EA SPORTS FC 26" };

let uid = 1;
function mkClip(o) {
  const c = Object.assign({ id: uid++, kind: "video", tracks: 2, codec: "HEVC", w: 2560, h: 1440, fps: 60, name: "" }, o);
  if (c.kind === "video" && !c.mbps) c.mbps = Math.round(c.bytes * 8 / c.dur / 1e6);
  return c;
}
// f: индекс набора кадров media/clips/c{f}_{0..4}.jpg; p: какой из пяти кадров обложка
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
  // скриншоты; разрешение областей взято из лога Aura
  mkClip({ kind: "shot", game: "cs2", date: new Date(2026, 8, 21, 17, 44, 13), bytes: 3480000, tape: 9, w: 2148, h: 1261, area: true }),
  mkClip({ kind: "shot", game: "cs2", date: new Date(2026, 8, 21, 17, 15, 39), bytes: 5920000, tape: 14 }),
  mkClip({ kind: "shot", game: "iwbtc", date: new Date(2026, 8, 20, 18, 10, 2), bytes: 1310000, img: "../media/clips/c5_1.jpg", w: 1060, h: 870, area: true }),
  mkClip({ kind: "shot", game: "iwbtc", date: new Date(2026, 8, 16, 21, 5, 44), bytes: 4870000, img: "../media/clips/c6_3.jpg" }),
];
const EMPTY_BACKUP = { list: null };

function frameSrc(c, k) {
  if (c.kind === "shot") return c.img || `../media/tape/t${c.tape}.jpg`;
  if (c.tapeFrames) return `../media/tape/t${c.tapeFrames[clamp(k, 0, 4)]}.jpg`;
  return `../media/clips/c${c.f}_${clamp(k, 0, 4)}.jpg`;
}
const poster = c => frameSrc(c, c.p ?? 2);
const clipTitle = c => c.name || GAMES[c.game];
// в списке игра стоит отдельной колонкой, поэтому без своего имени пишем, что это за файл
const defaultName = c => c.kind === "shot" ? (c.area ? "Скриншот области" : "Скриншот экрана") : c.dur <= 31 ? "Повтор 30 секунд" : "Повтор";
const clipPath = c => {
  const d = c.date, p = n => String(n).padStart(2, "0");
  const base = `${GAMES[c.game]} ${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} - ${p(d.getHours())}-${p(d.getMinutes())}-${p(d.getSeconds())}`;
  return c.kind === "shot" ? `C:\\Users\\Rizen\\Documents\\Aura\\${base}.png` : `E:\\видео\\${GAME_FOLDER[c.game]}\\${base}.mp4`;
};

/* настройки: значения из настоящего settings.json */
const S = {
  preset: "custom", res: 1440, fps: 60, bitrate: 35, codec: "HEVC", bits10: false, replayLen: 180,
  source: "auto", cursor: true,
  gameAudio: true, gameDevice: "realtek", mic: true, micDevice: "usb", tracks: "separate", noise: false, gate: -44,
  hk: { save: "F9", save30: "", toggle: "", mark: "", recStart: "", recStop: "", shot: "", area: "End", panel: "Alt+Q", folder: "" },
  saveRoot: "E:\\видео", shotRoot: "C:\\Users\\Rizen\\Documents\\Aura", byGame: true, template: "{game} {date} - {time}", attachMb: 20,
  notify: true, corner: "TR", notifySec: 2, sound: "soft",
  autostart: true, trayStart: true, autoReplay: true, theme: "dark", scale: "auto", updates: true, driver: true,
};
const PRESETS = {
  eco: { res: 720, fps: 30, bitrate: 5, label: "Эконом", note: "720p, 30 к/с, 5 Мбит/с. Меньше всего места" },
  normal: { res: 1080, fps: 60, bitrate: 12, label: "Обычный", note: "1080p, 60 к/с, 12 Мбит/с. Для стримов и клипов в чат" },
  high: { res: 1440, fps: 60, bitrate: 25, label: "Высокий", note: "1440p, 60 к/с, 25 Мбит/с. Чёткая картинка" },
  max: { res: 2160, fps: 60, bitrate: 48, label: "Максимум", note: "4K, 60 к/с, 48 Мбит/с. Для монтажа" },
};
const RES_W = { 720: 1280, 1080: 1920, 1440: 2560, 2160: 3840 };
const ramMb = () => S.bitrate * S.replayLen / 8 + S.replayLen * 0.78;
const clipMb = sec => sec * (S.bitrate + 0.32) / 8 * 1e6 / MIB;
const FREE_BYTES = 394.58 * 1024 * MIB;

/* =====================================================================
   Состояние записи
   ===================================================================== */
const E = {
  t: 1000,                 // «абсолютное» время, с
  speed: 1,
  replay: "on",            // on | off | error
  fillStart: 1000 - 400,   // буфер полон
  errorStart: null,
  rec: false, recStart: 0,
  saving: false,
  sel: null,               // { a, b|null }  b=null: до «сейчас»
  markers: [],
  micMuted: false, muteSpans: [],
  get L() { return S.replayLen; },
};
E.markers.push({ type: "saved", a: E.t - 158, b: E.t - 128 });
E.markers.push({ type: "mark", a: E.t - 71 });
E.markers.push({ type: "shot", a: E.t - 34 });

const tapeImgs = Array.from({ length: 18 }, (_, i) => { const im = new Image(); im.src = `../media/tape/t${i}.jpg`; return im; });
const frameIndex = k => 1 + (((k % 17) + 17) % 17);   // кадр t0 это консоль, его пропускаем

/* звук: детерминированный шум, чтобы лента не «дрожала» при перерисовке */
const hash = n => { const s = Math.sin(n * 127.1 + 311.7) * 43758.5453; return s - Math.floor(s); };
function vnoise(x) { const i = Math.floor(x), f = x - i, u = f * f * (3 - 2 * f); return hash(i) * (1 - u) + hash(i + 1) * u; }
function gameAmp(t) {
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
   Лента: один рисовальщик для большой ленты, шапки и панели поверх игры
   ===================================================================== */
function css(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }
let PAL = {};
function readPalette() {
  PAL = { t1: css("--t1"), t2: css("--t2"), t3: css("--t3"), acc: css("--acc"), rec: css("--rec"), amber: css("--amber"), well: css("--well"), line: css("--line-2") };
  PAL.light = document.documentElement.dataset.theme === "light";
}
function fitCanvas(cv) {
  // clientWidth, а не getBoundingClientRect: размер не должен зависеть от масштаба интерфейса
  const dpr = window.devicePixelRatio || 1, W = cv.clientWidth, H = cv.clientHeight;
  const w = Math.max(1, Math.round(W * dpr)), hh = Math.max(1, Math.round(H * dpr));
  if (cv.width !== w || cv.height !== hh) { cv.width = w; cv.height = hh; }
  return { dpr, W, H };
}
function rrect(ctx, x, y, w, hh, r) {
  ctx.beginPath();
  ctx.moveTo(x + r, y); ctx.arcTo(x + w, y, x + w, y + hh, r); ctx.arcTo(x + w, y + hh, x, y + hh, r);
  ctx.arcTo(x, y + hh, x, y, r); ctx.arcTo(x, y, x + w, y, r); ctx.closePath();
}
function drawImageCover(ctx, img, x, y, w, hh) {
  if (!img.complete || !img.naturalWidth) { ctx.fillStyle = PAL.line; ctx.fillRect(x, y, w, hh); return; }
  const ir = img.naturalWidth / img.naturalHeight, r = w / hh;
  let sw = img.naturalWidth, sh = img.naturalHeight, sx = 0, sy = 0;
  if (ir > r) { sw = sh * r; sx = (img.naturalWidth - sw) / 2; } else { sh = sw / r; sy = (img.naturalHeight - sh) / 2; }
  ctx.drawImage(img, sx, sy, sw, sh, x, y, w, hh);
}

/* layout: main | mini | capsule */
function drawTape(cv, layout) {
  const { dpr, W, H } = fitCanvas(cv);
  const ctx = cv.getContext("2d");
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, W, H);
  if (E.replay === "off" && layout !== "capsule") return;

  const now = E.t;
  const L = layout === "capsule" ? 24 : E.L;
  const X = t => W - (now - t) / L * W;
  const Tx = x => now - (W - x) / W * L;
  const start = E.fillStart ?? now;
  const xStart = clamp(X(start), -2, W);
  const wave = PAL.light ? "rgba(20,24,27," : "rgba(231,235,238,";

  if (layout === "capsule") {
    if (E.replay === "off") { ctx.fillStyle = PAL.t3; ctx.fillRect(0, H / 2 - .5, W, 1); return; }
    const step = 3, dt = L / (W / step);
    for (let b = Math.floor((now - L) / dt); b <= Math.floor(now / dt); b++) {
      const t = b * dt, x = X(t);
      if (t < start) continue;
      const a = Math.max(gameAmp(t) * .8, micAmp(t));
      const hh = Math.max(1.5, a * (H - 4));
      ctx.fillStyle = wave + (0.35 + 0.4 * ((x / W) ** 2)) + ")";
      ctx.fillRect(x, (H - hh) / 2, 2, hh);
    }
    return;
  }

  const main = layout === "main";
  const fr = main ? { y: 4, h: 66 } : { y: 3, h: 26 };
  const lanes = main
    ? [{ c: 95, hh: 13, f: gameAmp, alpha: .5 }, { c: 129, hh: 13, f: micAmp, alpha: .38 }]
    : [{ c: 40, hh: 9, f: t => Math.max(gameAmp(t) * .85, micAmp(t)), alpha: .5 }];

  // сетка времени
  ctx.fillStyle = PAL.light ? "rgba(12,18,24,.06)" : "rgba(255,255,255,.035)";
  const tick = L >= 120 ? 30 : 10;
  for (let s = tick; s < L; s += tick) ctx.fillRect(Math.round(X(now - s)), 0, 1, H);

  // кадры
  const FR = L / 18 <= 10 ? 10 : Math.ceil(L / 18 / 10) * 10;   // один кадр на 10 с (на длинном повторе реже)
  const k0 = Math.floor((now - L) / FR), k1 = Math.floor(now / FR);
  const xNow = W;
  for (let k = k0; k <= k1; k++) {
    const ta = k * FR, tb = ta + FR;
    if (tb <= start) continue;
    if (E.replay === "error" && E.errorStart != null && ta >= E.errorStart) continue;
    const x0 = X(ta), fw = X(tb) - x0;
    let visA = Math.max(x0, xStart), visB = Math.min(x0 + fw, xNow);
    if (E.replay === "error" && E.errorStart != null) visB = Math.min(visB, X(E.errorStart));
    if (visB - visA < 1) continue;
    ctx.save();
    rrect(ctx, visA + .5, fr.y, visB - visA - 1, fr.h, main ? 4 : 3); ctx.clip();
    drawImageCover(ctx, tapeImgs[frameIndex(k)], x0 + .5, fr.y, fw - 1, fr.h);
    ctx.restore();
  }

  // нет кадров при сбое захвата
  if (E.replay === "error" && E.errorStart != null) {
    const xa = Math.max(X(E.errorStart), xStart);
    hatch(ctx, xa, fr.y, W - xa, fr.h, PAL.amber, .18);
    if (main && W - xa > 90) { ctx.fillStyle = PAL.amber; ctx.font = "500 11px " + css("--f-ui"); ctx.fillText("нет кадров", xa + 10, fr.y + fr.h / 2 + 4); }
  }

  // звук
  const step = main ? 3 : 2.5, dt = L / (W / step);
  const sel = E.sel ? { a: E.sel.a, b: E.sel.b ?? now } : null;
  for (const ln of lanes) {
    for (let b = Math.floor(Math.max(now - L, start) / dt); b <= Math.floor(now / dt); b++) {
      const t = b * dt, x = X(t);
      const a = ln.f(t);
      const hh = Math.max(1, a * ln.hh);
      const inSel = sel && t >= sel.a && t <= sel.b;
      ctx.fillStyle = wave + (inSel ? Math.min(1, ln.alpha + .38) : ln.alpha) + ")";
      ctx.fillRect(x, ln.c - hh, main ? 2 : 1.5, hh * 2);
    }
  }

  // пустая часть до начала буфера
  if (xStart > 0) {
    hatch(ctx, 0, 0, xStart, H, PAL.light ? "#000" : "#fff", PAL.light ? .05 : .035);
    if (main && xStart > 110) {
      ctx.fillStyle = PAL.t3; ctx.font = "400 11.5px " + css("--f-ui");
      const txt = "ещё не записано"; const tw = ctx.measureText(txt).width;
      ctx.fillText(txt, xStart / 2 - tw / 2, H / 2 + 4);
    }
  }

  // метки
  for (const m of E.markers) {
    if (m.type === "saved") {
      const xa = clamp(X(m.a), 0, W), xb = clamp(X(m.b), 0, W);
      if (xb - xa < 1) continue;
      ctx.fillStyle = PAL.acc; ctx.globalAlpha = .9;
      ctx.fillRect(xa, fr.y + fr.h + (main ? 3 : 2), xb - xa, main ? 3 : 2);
      ctx.globalAlpha = 1;
    } else {
      const x = X(m.a); if (x < 0 || x > W) continue;
      ctx.fillStyle = PAL.light ? "rgba(20,24,27,.4)" : "rgba(255,255,255,.42)";
      ctx.fillRect(Math.round(x), 0, 1, H);
      if (m.type === "mark") { ctx.beginPath(); ctx.moveTo(x - 4, 0); ctx.lineTo(x + 5, 0); ctx.lineTo(x + .5, 6); ctx.closePath(); ctx.fill(); }
      else ctx.fillRect(Math.round(x) - 2, 0, 5, 3);
    }
  }

  // идущая запись
  if (E.rec) {
    const xa = clamp(X(E.recStart), 0, W);
    ctx.fillStyle = PAL.rec; ctx.fillRect(xa, H - 3, W - xa, 3);
  }

  // выделенный отрезок
  if (sel) {
    const xa = clamp(X(sel.a), 0, W), xb = clamp(X(sel.b), 0, W);
    ctx.fillStyle = PAL.light ? "rgba(20,24,27,.07)" : "rgba(255,255,255,.07)";
    ctx.fillRect(xa, 0, xb - xa, H);
    ctx.fillStyle = PAL.t1;
    ctx.fillRect(Math.round(xa), 0, 2, H);
    if (E.sel.b != null) ctx.fillRect(Math.round(xb) - 2, 0, 2, H);
    if (main) {
      const label = fmtDur(sel.b - sel.a), bw = ctx.measureText(label).width + 12;
      ctx.font = "500 11px " + css("--f-mono");
      const lx = clamp((xa + xb) / 2 - bw / 2, 4, W - bw - 60);
      ctx.fillStyle = PAL.t1; rrect(ctx, lx, fr.y + fr.h - 22, bw, 18, 4); ctx.fill();
      ctx.fillStyle = PAL.well; ctx.fillText(label, lx + 6, fr.y + fr.h - 9);
    }
  }

  // старое уходит из памяти: мягкий край слева
  if (xStart <= 0) {
    const g = ctx.createLinearGradient(0, 0, main ? 64 : 28, 0);
    g.addColorStop(0, PAL.well); g.addColorStop(1, PAL.well + "00");
    ctx.fillStyle = g; ctx.fillRect(0, 0, main ? 64 : 28, H);
  }
}
function hatch(ctx, x, y, w, hh, color, alpha) {
  if (w <= 0) return;
  ctx.save(); ctx.beginPath(); ctx.rect(x, y, w, hh); ctx.clip();
  ctx.strokeStyle = color; ctx.globalAlpha = alpha; ctx.lineWidth = 1;
  const off = (E.t * 8) % 8;
  for (let i = x - hh - 8 + off; i < x + w; i += 8) { ctx.beginPath(); ctx.moveTo(i, y + hh); ctx.lineTo(i + hh, y); ctx.stroke(); }
  ctx.restore();
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
  if (!t) return;
  tipTimer = setTimeout(() => {
    tipEl.innerHTML = `<span>${t.dataset.tip}</span>${t.dataset.kbd ? `<span class="kbd-set">${keysHtml(t.dataset.kbd)}</span>` : ""}`;
    const r = t.getBoundingClientRect(), tr = tipEl.getBoundingClientRect();
    const inRail = t.closest(".rail");
    let x = inRail ? r.right + 8 : clamp(r.left + r.width / 2 - tr.width / 2, 8, innerWidth - tr.width - 8);
    let y = inRail ? r.top + r.height / 2 - tr.height / 2 : r.bottom + 6;
    if (!inRail && y + tr.height > innerHeight - 8) y = r.top - tr.height - 6;
    tipEl.style.left = x + "px"; tipEl.style.top = y + "px";
    tipEl.classList.add("is-on");
  }, 450);
});
document.addEventListener("pointerdown", () => { clearTimeout(tipTimer); tipEl.classList.remove("is-on"); });

/* =====================================================================
   Меню и выпадающие списки
   ===================================================================== */
const layer = $("#layer");
let openMenuState = null;
function closeMenu() {
  if (!openMenuState) return;
  const { el, anchor } = openMenuState;
  anchor?.classList.remove("is-open");
  el.classList.add("is-leaving");
  setTimeout(() => el.remove(), 100);
  openMenuState = null;
}
/* items: [{label, icon, hint, check, danger, disabled, fn}] | "sep" | {head} */
function openMenu(items, { x, y, anchor, minWidth } = {}) {
  closeMenu();
  const el = h("div", { class: "menu", role: "menu" });
  if (minWidth) el.style.minWidth = minWidth + "px";
  const actionable = [];
  items.forEach(it => {
    if (it === "sep") { el.appendChild(h("div", { class: "menu-sep" })); return; }
    if (it.head) { el.appendChild(h("div", { class: "menu-head" }, it.head)); return; }
    const b = h("button", { class: "menu-item" + (it.danger ? " is-danger" : ""), role: "menuitem", disabled: it.disabled },
      `${it.check !== undefined ? `<span class="menu-check">${it.check ? ico("check", "i i-sm") : ""}</span>` : ""}${it.icon ? ico(it.icon, "i i-sm") : ""}<span class="menu-label">${it.label}</span>${it.hint ? `<span class="menu-hint">${it.hint}</span>` : ""}`);
    if (it.disabled) b.style.opacity = .45;
    b.addEventListener("click", () => { closeMenu(); it.fn?.(); });
    b.addEventListener("pointerenter", () => hot(actionable.indexOf(b)));
    el.appendChild(b); if (!it.disabled) actionable.push(b);
  });
  layer.appendChild(el);
  const r = el.getBoundingClientRect();
  if (anchor) {
    const ar = anchor.getBoundingClientRect();
    x = ar.left; y = ar.bottom + 4; anchor.classList.add("is-open");
    if (y + r.height > innerHeight - 8) { y = ar.top - r.height - 4; el.style.transformOrigin = "bottom left"; }
  }
  el.style.left = clamp(x, 8, innerWidth - r.width - 8) + "px";
  el.style.top = clamp(y, 8, innerHeight - r.height - 8) + "px";
  let idx = items.findIndex(i => i.check) ; idx = idx >= 0 ? actionable.findIndex(b => b.querySelector(".menu-check .i")) : -1;
  function hot(i) { actionable.forEach((b, j) => b.classList.toggle("is-hot", j === i)); idx = i; }
  if (idx >= 0) hot(idx);
  openMenuState = {
    el, anchor,
    key(e) {
      if (e.key === "ArrowDown") { hot((idx + 1) % actionable.length); return true; }
      if (e.key === "ArrowUp") { hot((idx - 1 + actionable.length) % actionable.length); return true; }
      if (e.key === "Enter" && idx >= 0) { actionable[idx].click(); return true; }
      if (e.key === "Escape") { closeMenu(); return true; }
      return false;
    },
  };
}
document.addEventListener("pointerdown", e => {
  if (openMenuState && !openMenuState.el.contains(e.target) && !openMenuState.anchor?.contains(e.target)) closeMenu();
}, true);
addEventListener("blur", closeMenu);
addEventListener("resize", closeMenu);

function makeSelect(btn, { options, get, set, width }) {
  const paint = () => {
    const o = options().find(o => o.value === get());
    btn.innerHTML = `<span>${o ? o.short || o.label : ""}</span>${ico("chevDown", "i i-sm")}`;
  };
  btn.addEventListener("click", () => {
    if (openMenuState?.anchor === btn) { closeMenu(); return; }
    openMenu(options().map(o => ({ label: o.label, hint: o.hint, check: o.value === get(), disabled: o.disabled, fn: () => { set(o.value); paint(); } })), { anchor: btn, minWidth: Math.max(width || 0, btn.offsetWidth) });
  });
  paint();
  return paint;
}

/* сегменты с «переезжающей» плашкой */
function makeSeg(el, { options, get, set, icons }) {
  el.innerHTML = "";
  const thumb = h("span", { class: "seg-thumb" });
  el.appendChild(thumb);
  const items = options.map(o => {
    const b = h("button", { class: "seg-item", role: "tab", "data-tip": icons ? o.label : null }, icons ? ico(o.icon, "i i-sm") : `${o.label}${o.count != null ? ` <span class="count">${o.count}</span>` : ""}`);
    if (icons) b.setAttribute("aria-label", o.label);
    b.addEventListener("click", () => { set(o.value); paint(true); });
    el.appendChild(b);
    return b;
  });
  function paint(anim) {
    const i = options.findIndex(o => o.value === get());
    items.forEach((b, j) => { b.classList.toggle("is-on", j === i); b.setAttribute("aria-selected", j === i); });
    const b = items[i];
    if (!b) { thumb.style.opacity = 0; return; }
    if (!anim) thumb.style.transition = "none";
    thumb.style.opacity = 1;
    thumb.style.width = b.offsetWidth + "px";
    thumb.style.transform = `translateX(${b.offsetLeft}px)`;
    if (!anim) { thumb.offsetWidth; thumb.style.transition = ""; }
  }
  requestAnimationFrame(() => paint(false));
  el._paint = paint;
  return paint;
}
function makeSwitch(btn, { get, set }) {
  const paint = () => btn.setAttribute("aria-checked", get() ? "true" : "false");
  btn.addEventListener("click", () => { set(!get()); paint(); });
  paint();
  return paint;
}

/* =====================================================================
   Уведомления
   ===================================================================== */
function toast({ thumb, icon, neutral, title, sub, actions = [], ms = 4200, game }) {
  const host = game ? $("#game-toasts") : $("#toasts");
  const el = h("div", { class: "toast", role: "status" });
  el.innerHTML = `${thumb ? `<img class="toast-thumb" src="${thumb}" alt="">` : icon ? `<span class="toast-ico${neutral ? " is-neutral" : ""}">${ico(icon, "i i-sm")}</span>` : ""}
    <div class="toast-text"><div class="toast-title">${title}</div>${sub ? `<div class="toast-sub">${sub}</div>` : ""}</div>`;
  actions.forEach(a => {
    const b = h("button", { class: "btn btn-sm" + (game ? " btn-quiet" : "") }, a.label);
    b.addEventListener("click", () => { a.fn(); dismiss(); });
    el.appendChild(b);
  });
  if (game) { el.style.setProperty("--toast-ms", ms + "ms"); el.appendChild(h("div", { class: "toast-bar" }, "<i></i>")); }
  host.appendChild(el);
  let timer = setTimeout(dismiss, ms);
  el.addEventListener("pointerenter", () => clearTimeout(timer));
  el.addEventListener("pointerleave", () => { timer = setTimeout(dismiss, 1600); });
  function dismiss() { if (!el.isConnected) return; el.classList.add("is-leaving"); setTimeout(() => el.remove(), 180); }
  while (host.children.length > 3) host.firstElementChild.remove();
  return dismiss;
}

/* =====================================================================
   Навигация
   ===================================================================== */
const app = $("#app");
let page = "capture";
const CRUMB = { capture: "Захват", clips: "Клипы", settings: "Настройки" };
function setPage(p, opts = {}) {
  const prev = $(`.page[data-page="${page}"]`), next = $(`.page[data-page="${p}"]`);
  if (p !== page) {
    prev.classList.remove("is-in");
    prev.style.transition = "opacity 120ms"; prev.style.opacity = 0;
    setTimeout(() => { if (page !== prev.dataset.page) { prev.classList.remove("is-shown"); prev.style.opacity = ""; prev.style.transition = ""; } }, 130);
    next.classList.add("is-shown", "is-entering");
    next.style.opacity = ""; next.style.transition = "";
    requestAnimationFrame(() => requestAnimationFrame(() => { next.classList.remove("is-entering"); next.classList.add("is-in"); }));
  } else { next.classList.add("is-shown", "is-in"); }
  page = p;
  app.dataset.page = p;
  $$(".rail-item[data-page]").forEach(b => b.classList.toggle("is-active", b.dataset.page === p));
  moveRailHead();
  paintCrumbs();
  if (p === "clips") renderClips();
  if (p === "settings") { renderSettings(); if (opts.pane) selectPane(opts.pane); }
  if (p === "capture") renderRecent();
  closeMenu();
}
function moveRailHead() {
  const b = $(`.rail-item[data-page="${page}"]`), head = $("#rail-head");
  const inFoot = b.closest(".rail-foot");
  const railTop = $(".rail").getBoundingClientRect().top, r = b.getBoundingClientRect(), navTop = $("#rail-nav").getBoundingClientRect().top;
  head.style.setProperty("--y", (r.top - navTop + (r.height - 22) / 2) + "px");
  void railTop; void inFoot;
}
function paintCrumbs() {
  const c = $("#crumbs");
  if (page === "settings") c.innerHTML = `<span class="crumb-page">Настройки</span><span class="crumb-sep">/</span><span class="crumb-sub">${PANES.find(p => p.id === pane)?.title ?? ""}</span>`;
  else c.innerHTML = `<span class="crumb-page">${CRUMB[page]}</span>`;
}
$$(".rail-item[data-page]").forEach(b => b.addEventListener("click", () => setPage(b.dataset.page)));
$("#to-clips").addEventListener("click", () => setPage("clips"));
$("#capsule-state").addEventListener("click", () => setPage("capture"));
addEventListener("resize", () => { moveRailHead(); fitRecent(); $$(".seg").forEach(s => s._paint?.(false)); if (setNavHead) setNavHead(); });

/* =====================================================================
   ЗАХВАТ
   ===================================================================== */
const replaySwitch = $("#replay-switch");
function paintState() {
  // на body, а не на окне: панель поверх игры тоже красит индикатор по состоянию
  document.body.dataset.replay = E.replay;
  document.body.dataset.rec = E.rec ? "on" : "off";
  replaySwitch.setAttribute("aria-checked", E.replay !== "off" ? "true" : "false");
  const filled = E.fillStart == null ? 0 : Math.min(E.L, E.t - E.fillStart);
  const full = filled >= E.L - .05;
  let title, sub;
  if (E.replay === "off") { title = "Повтор выключен"; sub = "Aura ничего не держит в памяти"; }
  else if (E.replay === "error") { title = "Захват прервался"; sub = "Игра сменила режим экрана. Переподключаюсь, звук пишется дальше"; }
  else if (!full) { title = "Повтор набирается"; sub = `Ещё ${fmtDur(E.L - filled)} до полных ${fmtDur(E.L)}`; }
  else { title = "Повтор идёт"; sub = S.source === "auto" ? "Counter-Strike 2, захват окна игры" : "Монитор 1, весь экран"; }
  $("#deck-title").textContent = title;
  $("#deck-sub").textContent = sub;
  $("#timer").textContent = E.replay === "off" ? "0:00" : fmtDur(filled);
  $("#timer-of").textContent = E.replay === "off" ? "в памяти пусто" : full ? "в памяти" : `из ${fmtDur(E.L)}`;
  $("#tape-left").textContent = "−" + fmtDur(E.L);
  $("#tape-empty-sub").textContent = `Включите повтор, и лента начнёт заполняться: кадры, звук игры и микрофона за последние ${fmtDur(E.L)}`;
  $("#tape-mid").textContent = "";
  const recT = fmtDur(E.t - E.recStart);
  $("#rec-time").textContent = recT;
  $("#btn-rec-label").textContent = E.rec ? "Остановить" : "Запись";
  // шапка
  const lab = $("#capsule-label"), tm = $("#capsule-time");
  if (E.rec) { lab.textContent = "Запись"; tm.textContent = recT; lab.style.color = "var(--rec)"; }
  else {
    lab.style.color = "";
    lab.textContent = E.replay === "off" ? "Повтор выключен" : E.replay === "error" ? "Нет кадров" : full ? "Повтор" : "Набирается";
    tm.textContent = E.replay === "off" ? "" : fmtDur(filled);
  }
  // панель поверх игры
  $("#qp-title").textContent = E.rec ? "Идёт запись" : title;
  $("#qp-time").textContent = E.rec ? recT : E.replay === "off" ? "" : fmtDur(filled);
  paintQpTiles();
}

function specHtml() {
  const w = RES_W[S.res] ?? 2560, hh = S.res;
  const tracks = !S.mic ? "Только игра" : S.tracks === "separate" ? "Игра и микрофон, 2 дорожки" : "Игра и микрофон, 1 дорожка";
  return `<span><b>${w}×${hh}</b> ${S.fps} к/с</span><span>${S.codec} <b>${S.bitrate}</b> Мбит/с</span><span>${tracks}</span>`;
}

replaySwitch.addEventListener("click", () => {
  if (E.replay === "off") { startReplay(); return; }
  const filled = Math.min(E.L, E.t - E.fillStart);
  if (filled < 20) { stopReplay(); return; }
  openMenu([
    { head: `В памяти ${fmtDur(filled)}. После выключения они пропадут.` },
    { label: "Сохранить и выключить", icon: "save", fn: () => { saveReplay(); setTimeout(stopReplay, 1300); } },
    { label: "Выключить без сохранения", icon: "power", fn: stopReplay },
  ], { anchor: replaySwitch, minWidth: 290 });
});
function startReplay() { E.replay = "on"; E.fillStart = E.t; E.errorStart = null; E.sel = null; paintState(); paintSel(); renderHealth(); }
function stopReplay() { E.replay = "off"; E.fillStart = null; E.sel = null; E.markers = []; paintState(); paintSel(); renderHealth(); }

/* сохранение */
function currentRange() {
  const start = Math.max(E.fillStart, E.t - E.L);
  if (E.sel) return { a: Math.max(E.sel.a, start), b: Math.min(E.sel.b ?? E.t, E.t) };
  return { a: start, b: E.t };
}
function saveReplay() {
  if (E.saving) return;
  if (E.replay === "off") { startReplay(); return; }   // выключенный повтор: главная кнопка включает его
  if (E.replay !== "on") {
    toast({ icon: "info", neutral: true, title: E.replay === "off" ? "Повтор выключен" : "Сейчас нечего сохранять", sub: E.replay === "off" ? "В памяти пусто. Включите повтор, чтобы сохранять моменты." : "Захват переподключается", actions: E.replay === "off" ? [{ label: "Включить", fn: startReplay }] : [], game: scene.open });
    return;
  }
  const { a, b } = currentRange();
  const dur = b - a;
  if (dur < 1) return;
  E.saving = true;
  const ms = reduced ? 300 : Math.round(500 + dur * 3.4);
  const saveBtns = $$(".btn-save");
  saveBtns.forEach(bt => { bt.style.setProperty("--save-ms", ms + "ms"); bt.classList.remove("is-done"); bt.classList.add("is-saving"); $(".save-label", bt).textContent = "Сохраняю…"; });
  $(".capsule-save-label").textContent = "Сохраняю…";
  const selWas = E.sel; E.sel = null; paintSel();
  E.markers.push({ type: "saved", a, b, pending: true });
  setTimeout(() => {
    E.saving = false;
    E.markers.forEach(m => delete m.pending);
    const k0 = Math.floor(a / 10), k1 = Math.floor(b / 10);
    const pick = [0, 1, 2, 3, 4].map(i => frameIndex(Math.round(k0 + (k1 - k0) * (i + .5) / 5)));
    const clip = mkClip({ game: "cs2", date: new Date(2026, 8, 21, 18, 40 + (uid % 15), uid % 60), dur, bytes: clipMb(dur) * MIB, tapeFrames: pick, p: 4, mbps: S.bitrate, codec: S.codec, tracks: S.mic && S.tracks === "separate" ? 2 : 1, isNew: true });
    CLIPS.unshift(clip);
    saveBtns.forEach(bt => { bt.classList.remove("is-saving"); bt.classList.add("is-done"); $(".save-label", bt).textContent = `Сохранено ${fmtDur(dur)}`; $(".save-ico use", bt).setAttribute("href", "#i-check"); });
    $(".capsule-save-label").textContent = "Сохранено";
    setTimeout(() => {
      saveBtns.forEach(bt => { bt.classList.remove("is-done"); $(".save-ico use", bt).setAttribute("href", "#i-save"); });
      paintSaveLabel();
      $(".capsule-save-label").textContent = "Сохранить";
    }, 1500);
    const sub = `${fmtDur(dur)}, ${fmtSize(clip.bytes)}`;
    if (scene.open) toast({ thumb: poster(clip), title: "Повтор сохранён", sub, ms: S.notifySec * 1000 + 600, game: true });
    else toast({ thumb: poster(clip), title: selWas ? "Фрагмент сохранён" : "Повтор сохранён", sub, actions: [{ label: "Открыть", fn: () => { setPage("clips"); selectOnly(clip.id); } }] });
    renderRecent(); if (page === "clips") renderClips();
    renderHealth();
  }, ms);
}
function paintSaveLabel() {
  const part = E.sel ? fmtDur((E.sel.b ?? E.t) - E.sel.a) : null;
  const off = E.replay === "off";
  $$(".btn-save").forEach(bt => {
    if (bt.classList.contains("is-saving") || bt.classList.contains("is-done")) return;
    bt.classList.toggle("is-part", !!E.sel && bt.id === "btn-save");
    $(".save-ico use", bt).setAttribute("href", off ? "#i-replay" : "#i-save");
    $(".kbd-set", bt).style.display = off ? "none" : "";
    $(".save-label", bt).textContent = off ? "Включить повтор" : part && bt.id === "btn-save" ? `Сохранить ${part}` : "Сохранить повтор";
  });
  $(".capsule-save-label").textContent = off ? "Включить" : "Сохранить";
  $(".capsule-save .kbd-set").style.display = off ? "none" : "";
}
$("#btn-save").addEventListener("click", saveReplay);
$("#qp-save").addEventListener("click", saveReplay);
$("#capsule-save").addEventListener("click", saveReplay);

/* запись */
function toggleRec() {
  if (!E.rec) { E.rec = true; E.recStart = E.t; paintState(); return; }
  const dur = E.t - E.recStart;
  E.rec = false; paintState();
  const clip = mkClip({ game: "cs2", date: new Date(2026, 8, 21, 18, 52, uid % 60), dur, bytes: clipMb(dur) * MIB, tapeFrames: [3, 5, 7, 9, 11].map(i => frameIndex(Math.floor(E.t / 10) - i)), p: 0, mbps: S.bitrate, codec: S.codec, name: "Запись", isNew: true });
  CLIPS.unshift(clip);
  renderRecent(); if (page === "clips") renderClips();
  toast({ thumb: poster(clip), title: "Запись сохранена", sub: `${fmtDur(dur)}, ${fmtSize(clip.bytes)}`, game: scene.open, ms: scene.open ? S.notifySec * 1000 + 600 : 4200, actions: scene.open ? [] : [{ label: "Открыть", fn: () => { setPage("clips"); selectOnly(clip.id); } }] });
}
$("#btn-rec").addEventListener("click", toggleRec);
$("#btn-rec").dataset.tip = "Клавиша не назначена";

/* скриншот */
function screenshot() {
  const k = Math.floor(E.t / 10);
  const clip = mkClip({ kind: "shot", game: "cs2", date: new Date(2026, 8, 21, 18, 55, uid % 60), bytes: 3.1e6 + hash(uid) * 2e6, tape: frameIndex(k), w: 1648, h: 928, area: true, isNew: true });
  CLIPS.unshift(clip);
  if (E.replay !== "off") E.markers.push({ type: "shot", a: E.t });
  if (scene.open) { const f = $("#scene-flash"); f.classList.remove("is-on"); f.offsetWidth; f.classList.add("is-on"); }
  toast({ thumb: poster(clip), title: "Скриншот области скопирован", sub: "Лежит в буфере обмена и в папке скриншотов", game: scene.open, ms: scene.open ? S.notifySec * 1000 + 600 : 4200 });
  if (page === "clips") renderClips();
}
$("#btn-shot").addEventListener("click", () => {
  toast({ icon: "crop", neutral: true, title: "Выделите область на экране", sub: "В приложении окно Aura прячется, и экран замирает. Здесь снимок сделан сразу.", ms: 3000 });
  setTimeout(screenshot, 500);
});

/* лента: наведение, выбор отрезка */
const tapeEl = $("#tape"), tapeCv = $("#tape-canvas"), hoverEl = $("#tape-hover");
const peek = h("div", { class: "tape-peek" }, `<img alt=""><span class="mono"></span>`);
$("#deck").appendChild(peek);
function tapeTime(clientX) {
  const r = tapeEl.getBoundingClientRect();
  return { t: E.t - (r.right - clientX) / r.width * E.L, x: clientX - r.left, r };
}
const X_of = (t, w) => w - (E.t - t) / E.L * w;
let drag = null;
tapeEl.addEventListener("pointermove", e => {
  if (E.replay === "off") return;
  const { t, x, r } = tapeTime(e.clientX);
  const start = E.fillStart ?? E.t;
  hoverEl.style.transform = `translateX(${x}px)`;
  hoverEl.classList.add("is-on");
  hoverEl.classList.toggle("flip", x > r.width - 90);
  const saved = E.markers.find(m => m.type === "saved" && t >= m.a && t <= m.b);
  const note = E.markers.find(m => m.type !== "saved" && Math.abs(X_of(m.a, r.width) - x) < 5);
  $("#tape-hover-time").textContent = t < start ? "пусто" : "−" + fmtDur(E.t - t) + (note ? (note.type === "mark" ? ", метка" : ", скриншот") : saved ? ", уже сохранено" : "");
  const y = e.clientY - r.top;
  if (y < 72 && t >= start && !(E.replay === "error" && t >= E.errorStart)) {
    const k = Math.floor(t / (E.L / 18 <= 10 ? 10 : Math.ceil(E.L / 18 / 10) * 10));
    $("img", peek).src = `../media/tape/t${frameIndex(k)}.jpg`;
    $("span", peek).textContent = "−" + fmtDur(E.t - t);
    const dr = $("#deck").getBoundingClientRect();
    peek.style.left = clamp(e.clientX - dr.left - 96, 12, dr.width - 204) + "px";
    peek.style.top = (r.top - dr.top - 118) + "px";
    peek.classList.add("is-on");
  } else peek.classList.remove("is-on");
  if (drag) {
    const a = Math.max(start, Math.min(drag.t0, t)), b = Math.max(drag.t0, t);
    if (Math.abs(x - drag.x0) > 4) { E.sel = { a, b: b >= E.t - .3 ? null : b }; drag.moved = true; paintSel(); }
  }
});
tapeEl.addEventListener("pointerleave", () => { hoverEl.classList.remove("is-on"); peek.classList.remove("is-on"); });
tapeEl.addEventListener("pointerdown", e => {
  if (E.replay === "off" || e.button !== 0) return;
  tapeEl.setPointerCapture(e.pointerId);
  const { t, x } = tapeTime(e.clientX);
  drag = { t0: Math.max(t, E.fillStart ?? t), x0: x, moved: false };
});
tapeEl.addEventListener("pointerup", e => {
  if (!drag) return;
  if (!drag.moved) { const { t } = tapeTime(e.clientX); const a = Math.max(t, E.fillStart ?? t); E.sel = E.t - a < 1 ? null : { a, b: null }; paintSel(); }
  drag = null;
});
$("#sel-clear").addEventListener("click", () => { E.sel = null; paintSel(); });
function paintSel() {
  $("#sel-clear").hidden = !E.sel;
  const hint = $("#tape-hint");
  hint.style.visibility = E.replay === "off" ? "hidden" : "";
  if (E.sel) { hint.textContent = E.sel.b == null ? "Сохранится от отметки до «сейчас». Esc или «Весь повтор», чтобы отменить" : "Сохранится выделенный отрезок. Esc, чтобы отменить"; hint.classList.add("is-strong"); }
  else { hint.textContent = "Щёлкните по ленте, чтобы сохранить только часть: от этого места до «сейчас». Протяните, чтобы выбрать отрезок"; hint.classList.remove("is-strong"); }
  paintSaveLabel();
}

/* последние клипы */
let recentCols = 4;
function fitRecent() {
  const w = $("#recent-grid").clientWidth || 900;
  const cols = clamp(Math.floor((w + 16) / (196 + 16)), 2, 6);
  if (cols !== recentCols) { recentCols = cols; renderRecent(); }
}
function renderRecent() {
  const grid = $("#recent-grid");
  const vids = CLIPS.filter(c => c.kind === "video");
  const total = CLIPS.reduce((s, c) => s + c.bytes, 0);
  $("#recent-meta").textContent = CLIPS.length ? `${vids.length} ${plural(vids.length, "видео", "видео", "видео")}, ${fmtSizeTotal(total)}` : "";
  grid.style.setProperty("--cols", recentCols);
  if (!vids.length) {
    grid.style.display = "block";
    grid.innerHTML = `<div class="panel" style="padding:20px;display:flex;align-items:center;gap:16px"><div class="empty-cap" style="width:52px;height:48px;font-size:15px;border-radius:9px">${S.hk.save}</div><div><div style="font-weight:600">Здесь появятся сохранённые повторы</div><div class="row-sub">Нажмите ${S.hk.save} в игре, и последние ${fmtDur(E.L)} сохранятся одним файлом.</div></div></div>`;
    return;
  }
  grid.style.display = "";
  grid.innerHTML = "";
  vids.slice(0, recentCols * 2).forEach(c => grid.appendChild(clipCard(c, { compact: true })));
}

/* состояние */
function renderHealth() {
  const off = E.replay === "off", err = E.replay === "error";
  const hours = FREE_BYTES / (clipMb(3600) * MIB);
  const rows = [
    { ico: "monitor", name: "Видео", sub: off ? "Захват не идёт" : err ? "WGC не присылает кадры 5 с, пересобираю" : "Окно игры, без пропусков кадров", val: off ? "–" : err ? "0 к/с" : `<span id="fps">${S.fps}</span> к/с`, warn: err },
    { ico: "cpu", name: "Кодирование", sub: "NVENC на RTX 3070", val: off ? "–" : `${S.codec} ${S.bitrate} Мбит/с` },
    { ico: "speaker", name: "Звук игры", sub: "Динамики (Realtek(R) Audio)", val: `<span class="meter" id="m-game">${"<i></i>".repeat(10)}</span>` },
    { ico: "mic", name: "Микрофон", sub: E.micMuted ? "Выключен, в запись не попадает" : "Микрофон (USB Audio Device)", val: `<span class="meter${E.micMuted ? " is-muted" : ""}" id="m-mic">${"<i></i>".repeat(10)}</span><button class="mute${E.micMuted ? " is-off" : ""}" id="mic-mute" data-tip="${E.micMuted ? "Включить микрофон" : "Выключить микрофон"}">${ico(E.micMuted ? "micOff" : "mic", "i i-sm")}</button>` },
    { ico: "disk", name: "Диск E:", sub: `Хватит примерно на ${Math.round(hours)} ч записи`, val: "394,6 ГБ" },
    { ico: "ram", name: "Память", sub: off ? "Повтор выключен, память свободна" : `Держит повтор ${fmtDur(E.L)}`, val: off ? "0 МБ" : `${Math.round(ramMb())} МБ` },
  ];
  $("#health").innerHTML = rows.map(r => `<div class="h-row">${ico(r.ico)}<div style="min-width:0"><div class="h-name">${r.name}</div><div class="h-sub">${r.sub}</div></div><div class="h-val${r.warn ? " is-warn" : ""}">${r.val}</div></div>`).join("");
  $("#mic-mute").addEventListener("click", toggleMic);
}
function toggleMic() {
  E.micMuted = !E.micMuted;
  if (E.micMuted) E.muteSpans.push({ a: E.t, b: null }); else { const s = E.muteSpans.at(-1); if (s && s.b == null) s.b = E.t; }
  renderHealth(); paintQpTiles();
}
function paintMeters() {
  const g = $("#m-game"), m = $("#m-mic");
  if (!g) return;
  const lv = (el, a) => { const n = Math.round(a * 10); [...el.children].forEach((b, i) => { b.className = i < n ? (i >= 8 ? "hot" : "on") : ""; b.style.height = (4 + i) + "px"; }); };
  lv(g, E.replay === "off" ? gameAmp(E.t) * .9 : gameAmp(E.t));
  lv(m, E.micMuted ? 0 : micAmp(E.t));
  const fps = $("#fps"); if (fps && Math.random() < .08) fps.textContent = S.fps;
}

/* =====================================================================
   Карточка клипа (общая для «Захвата» и «Клипов»)
   ===================================================================== */
function clipCard(c, { compact } = {}) {
  const el = h("div", { class: "clip" + (c.isNew ? " is-new" : "") + (CL.sel.has(c.id) && !compact ? " is-selected" : ""), "data-id": c.id, tabindex: compact ? null : "0" });
  el.innerHTML = `<div class="clip-thumb"><img src="${poster(c)}" alt="" draggable="false">
      ${c.kind === "video" ? `<span class="clip-dur">${fmtDur(c.dur)}</span><span class="clip-scrub"></span>` : `<span class="clip-kind">${ico("camera", "i i-xs")}${c.area ? "область" : "экран"}</span>`}
      ${compact ? "" : `<span class="clip-check">${ico("check")}</span>`}</div>
    <div class="clip-info"><div class="clip-title">${clipTitle(c)}</div>
      <div class="clip-meta"><span>${compact ? `${dayLabel(c.date)}, ${hhmm(c.date)}` : hhmm(c.date)}</span><span>${fmtSize(c.bytes)}</span></div></div>`;
  if (c.isNew) setTimeout(() => { el.classList.remove("is-new"); delete c.isNew; }, 1500);
  const thumb = $(".clip-thumb", el), img = $("img", thumb), scrub = $(".clip-scrub", thumb);
  if (c.kind === "video") {
    thumb.addEventListener("pointermove", e => {
      const r = thumb.getBoundingClientRect(), p = clamp((e.clientX - r.left) / r.width, 0, .999);
      const src = frameSrc(c, Math.floor(p * 5));
      if (!img.src.endsWith(src)) img.src = src;
      scrub.style.width = (p * 100) + "%";
    });
    thumb.addEventListener("pointerleave", () => { img.src = poster(c); });
  }
  if (compact) {
    el.addEventListener("click", () => { setPage("clips"); selectOnly(c.id); });
    el.addEventListener("contextmenu", e => { e.preventDefault(); clipMenu(c, e.clientX, e.clientY); });
  }
  return el;
}

/* =====================================================================
   КЛИПЫ
   ===================================================================== */
const CL = { kind: "all", game: "all", sort: "new", view: "grid", q: "", sel: new Set(), anchor: null, busy: {} };
function visibleClips() {
  let list = CLIPS.filter(c => (CL.kind === "all" || (CL.kind === "video" ? c.kind === "video" : c.kind === "shot")) && (CL.game === "all" || c.game === CL.game));
  if (CL.q) { const q = CL.q.toLowerCase(); list = list.filter(c => (clipTitle(c) + " " + GAMES[c.game] + " " + dayLabel(c.date)).toLowerCase().includes(q)); }
  const by = { new: (a, b) => b.date - a.date, old: (a, b) => a.date - b.date, size: (a, b) => b.bytes - a.bytes, name: (a, b) => clipTitle(a).localeCompare(clipTitle(b), "ru") }[CL.sort];
  return list.sort(by);
}
let kindPaint, gamePaint, sortPaint;
function setupClipsBar() {
  const counts = () => ({ all: CLIPS.length, video: CLIPS.filter(c => c.kind === "video").length, shot: CLIPS.filter(c => c.kind === "shot").length });
  const cnt = counts();
  kindPaint = makeSeg($("#kind-seg"), { options: [{ value: "all", label: "Всё", count: cnt.all }, { value: "video", label: "Видео", count: cnt.video }, { value: "shot", label: "Скриншоты", count: cnt.shot }], get: () => CL.kind, set: v => { CL.kind = v; clearSel(); renderClipsBody(); } });
  gamePaint = makeSelect($("#game-select"), {
    options: () => [{ value: "all", label: "Все игры", hint: String(CLIPS.length) }, ...Object.entries(GAMES).map(([k, v]) => ({ value: k, label: v, hint: String(CLIPS.filter(c => c.game === k).length) })).filter(o => o.hint !== "0")],
    get: () => CL.game, set: v => { CL.game = v; clearSel(); renderClipsBody(); }, width: 260,
  });
  sortPaint = makeSelect($("#sort-select"), {
    options: () => [{ value: "new", label: "Сначала новые" }, { value: "old", label: "Сначала старые" }, { value: "size", label: "Сначала крупные" }, { value: "name", label: "По названию" }],
    get: () => CL.sort, set: v => { CL.sort = v; renderClipsBody(); },
  });
  makeSeg($("#view-seg"), { icons: true, options: [{ value: "grid", label: "Плитка", icon: "grid" }, { value: "list", label: "Список", icon: "list" }], get: () => CL.view, set: v => { CL.view = v; renderClipsBody(); } });
}
function renderClips() {
  setupClipsBar();
  renderClipsBody();
  renderInspector();
}
function renderClipsBody() {
  const body = $("#clips-body");
  const list = visibleClips();
  const vids = CLIPS.filter(c => c.kind === "video").length, shots = CLIPS.length - vids;
  $("#lib-meta").textContent = CLIPS.length ? `${vids} видео и ${shots} ${plural(shots, "скриншот", "скриншота", "скриншотов")}, ${fmtSizeTotal(CLIPS.reduce((s, c) => s + c.bytes, 0))}` : "";
  body.className = CL.sel.size > 1 ? "is-multi" : "";
  if (!CLIPS.length) {
    body.innerHTML = `<div class="empty"><div class="empty-key"><div class="empty-cap">${S.hk.save}</div><div><h3>Сохраните первый повтор</h3><p>Нажмите ${S.hk.save} во время игры, и последние ${fmtDur(E.L)} лягут сюда одним файлом. Aura держит их в памяти всё время, пока включён повтор.</p><div class="row"><button class="btn btn-primary" id="empty-save">Сохранить повтор сейчас</button><button class="btn" id="empty-keys">Настроить клавиши</button></div></div></div></div>`;
    $("#empty-save").addEventListener("click", saveReplay);
    $("#empty-keys").addEventListener("click", () => setPage("settings", { pane: "keys" }));
    return;
  }
  if (!list.length) {
    body.innerHTML = `<div class="empty"><div><h3>Ничего не нашлось</h3><p>${CL.q ? `По запросу «${CL.q}» нет клипов.` : "В этом разделе пока пусто."} Попробуйте другое название игры или сбросьте фильтры.</p><div class="row"><button class="btn" id="reset-filters">Сбросить фильтры</button></div></div></div>`;
    $("#reset-filters").addEventListener("click", () => { CL.kind = "all"; CL.game = "all"; CL.q = ""; $("#clip-search").value = ""; renderClips(); });
    return;
  }
  body.innerHTML = "";
  const grouped = CL.sort === "new" || CL.sort === "old";
  const groups = [];
  if (grouped) { for (const c of list) { const k = dayKey(c.date); if (!groups.length || groups.at(-1).key !== k) groups.push({ key: k, label: dayLabel(c.date), items: [] }); groups.at(-1).items.push(c); } }
  else groups.push({ key: "all", label: CL.sort === "size" ? "Сначала крупные" : "По названию", items: list });

  if (CL.view === "grid") {
    for (const g of groups) {
      const sec = h("section", { class: "day" });
      const bytes = g.items.reduce((s, c) => s + c.bytes, 0);
      sec.innerHTML = `<div class="day-head"><h3>${g.label}</h3><span class="sec-meta">${g.items.length} ${plural(g.items.length, "файл", "файла", "файлов")}, ${fmtSizeTotal(bytes)}</span><button class="btn btn-quiet btn-sm day-pick">Выбрать все</button></div>`;
      const grid = h("div", { class: "grid" });
      g.items.forEach(c => grid.appendChild(clipCard(c)));
      sec.appendChild(grid);
      $(".day-pick", sec).addEventListener("click", () => { g.items.forEach(c => CL.sel.add(c.id)); paintSelection(); });
      body.appendChild(sec);
    }
  } else {
    const head = h("div", { class: "list-headrow" });
    const cols = [["", ""], ["", ""], ["name", "Название"], ["game", "Игра"], ["new", "Дата"], ["dur", "Длина", "num"], ["size", "Размер", "num"], ["", "Формат"]];
    head.innerHTML = cols.map(([k, l, cls]) => k ? `<button data-sort="${k}" class="${cls || ""}${(CL.sort === k || (k === "new" && CL.sort === "old")) ? " is-on" : ""}">${l}${CL.sort === k ? ico("chevDown", "i i-xs") : ""}</button>` : `<span>${l}</span>`).join("");
    $$("button", head).forEach(b => b.addEventListener("click", () => {
      const k = b.dataset.sort; CL.sort = k === "new" ? (CL.sort === "new" ? "old" : "new") : k === "dur" || k === "game" ? CL.sort : k;
      sortPaint(); renderClipsBody();
    }));
    body.appendChild(head);
    const wrap = h("div", { class: "list" });
    for (const g of groups) {
      if (grouped) wrap.appendChild(h("div", { class: "list-group" }, g.label));
      g.items.forEach(c => {
        const r = h("div", { class: "list-row clip-row" + (CL.sel.has(c.id) ? " is-selected" : ""), "data-id": c.id, tabindex: "0" });
        r.innerHTML = `<span class="clip-check">${ico("check")}</span><div class="lthumb"><img src="${poster(c)}" alt="" loading="lazy"></div>
          <div class="lname${c.name ? "" : " is-default"}">${c.name || defaultName(c)}</div><div class="lcell">${GAMES[c.game]}</div><div class="lcell">${dayLabel(c.date)}, ${hhmm(c.date)}</div>
          <div class="lcell num">${c.kind === "video" ? fmtDur(c.dur) : "–"}</div><div class="lcell num">${fmtSize(c.bytes)}</div>
          <div class="lcell">${c.kind === "video" ? `${c.codec}, ${c.tracks === 2 ? "2 дорожки" : "1 дорожка"}` : `PNG, ${c.w}×${c.h}`}</div>`;
        wrap.appendChild(r);
      });
    }
    body.appendChild(wrap);
  }
}
/* выбор: щелчок, Ctrl, Shift, флажок */
$("#clips-body").addEventListener("click", e => {
  const card = e.target.closest("[data-id]"); if (!card || e.target.closest(".day-pick")) return;
  const id = +card.dataset.id;
  const order = visibleClips().map(c => c.id);
  if (e.shiftKey && CL.anchor != null) {
    const a = order.indexOf(CL.anchor), b = order.indexOf(id);
    if (!e.ctrlKey) CL.sel.clear();
    order.slice(Math.min(a, b), Math.max(a, b) + 1).forEach(i => CL.sel.add(i));
  } else if (e.ctrlKey || e.metaKey || e.target.closest(".clip-check")) {
    CL.sel.has(id) ? CL.sel.delete(id) : CL.sel.add(id); CL.anchor = id;
  } else { CL.sel.clear(); CL.sel.add(id); CL.anchor = id; }
  paintSelection();
});
$("#clips-body").addEventListener("dblclick", e => { const card = e.target.closest("[data-id]"); if (card) openEditor(byId(+card.dataset.id)); });
$("#clips-body").addEventListener("contextmenu", e => {
  const card = e.target.closest("[data-id]"); if (!card) return;
  e.preventDefault();
  const id = +card.dataset.id;
  if (!CL.sel.has(id)) { CL.sel.clear(); CL.sel.add(id); CL.anchor = id; paintSelection(); }
  clipMenu(byId(id), e.clientX, e.clientY);
});
$("#clips-body").addEventListener("pointerdown", e => { if (!e.target.closest("[data-id], button") && CL.sel.size) { clearSel(); } });
const byId = id => CLIPS.find(c => c.id === id);
function selectOnly(id) { CL.sel.clear(); CL.sel.add(id); CL.anchor = id; if (page === "clips") { renderClipsBody(); renderInspector(); requestAnimationFrame(() => $(`#clips-body [data-id="${id}"]`)?.scrollIntoView({ block: "nearest" })); } }
function clearSel() { CL.sel.clear(); paintSelection(); }
function paintSelection() {
  $$("#clips-body [data-id]").forEach(el => el.classList.toggle("is-selected", CL.sel.has(+el.dataset.id)));
  $("#clips-body").classList.toggle("is-multi", CL.sel.size > 1);
  $("#sel-count").textContent = CL.sel.size > 1 ? `Выбрано ${CL.sel.size}` : "";
  renderInspector();
}

function clipMenu(c, x, y) {
  const many = CL.sel.size > 1 && CL.sel.has(c.id);
  const items = many ? [
    { label: `Сжать для Discord (${CL.sel.size})`, icon: "box", fn: () => [...CL.sel].forEach(i => compress(byId(i))) },
    { label: "Показать в папке", icon: "folderOpen", fn: () => reveal(c) },
    "sep",
    { label: `Удалить в корзину (${CL.sel.size})`, icon: "trash", hint: "Del", danger: true, fn: () => removeClips([...CL.sel]) },
  ] : [
    { label: "Открыть", icon: "play", hint: "Enter", fn: () => openEditor(c, true) },
    ...(c.kind === "video" ? [{ label: "Редактировать в Aura", icon: "scissors", hint: "Ctrl E", fn: () => openEditor(c) }] : []),
    { label: "Показать в папке", icon: "folderOpen", fn: () => reveal(c) },
    { label: c.kind === "shot" ? "Копировать картинку" : "Копировать файл", icon: "copy", hint: "Ctrl C", fn: () => copyClip(c) },
    ...(c.kind === "video" ? [{ label: `Сжать для Discord`, icon: "box", fn: () => compress(c) }] : []),
    "sep",
    { label: "Переименовать", icon: "pencil", hint: "F2", fn: () => { setPage("clips"); selectOnly(c.id); setTimeout(startRename, 30); } },
    { label: "Удалить в корзину", icon: "trash", hint: "Del", danger: true, fn: () => removeClips([c.id]) },
  ];
  openMenu(items, { x, y, minWidth: 240 });
}
function openEditor(c, justOpen) {
  if (!c) return;
  if (justOpen && c.kind === "shot") { toast({ thumb: poster(c), title: "Открываю в просмотрщике", sub: clipPath(c).split("\\").pop() }); return; }
  toast({ thumb: poster(c), title: justOpen ? "Открываю в плеере" : "Редактор откроется отдельным окном", sub: justOpen ? clipPath(c).split("\\").pop() : "В прототипе редактора нет, он уже переделан в приложении" });
}
function reveal(c) { toast({ icon: "folderOpen", neutral: true, title: "Показано в проводнике", sub: clipPath(c) }); }
function copyClip(c) { toast({ icon: "copy", neutral: true, title: c.kind === "shot" ? "Картинка в буфере обмена" : "Файл в буфере обмена", sub: "Вставьте в Discord или в папку" }); }
function compress(c) {
  if (!c || c.kind !== "video" || CL.busy[c.id] != null) return;
  CL.busy[c.id] = 0;
  const t0 = performance.now(), dur = 2400;
  const target = S.attachMb * 0.93 + hash(c.id) * S.attachMb * 0.05;
  (function step() {
    const p = Math.min(1, (performance.now() - t0) / dur);
    CL.busy[c.id] = p;
    const bar = $(`#compress-${c.id} i`); if (bar) bar.style.transform = `scaleX(${p})`;
    const cardBusy = $(`#clips-body [data-id="${c.id}"] .clip-busy .bar i`); if (cardBusy) cardBusy.style.width = (p * 100) + "%";
    if (p < 1) { requestAnimationFrame(step); return; }
    delete CL.busy[c.id]; c.compressed = target;
    toast({ icon: "box", title: `Готово для Discord: ${num(target, 1)} МБ`, sub: "Копия лежит рядом с оригиналом и уже в буфере обмена", actions: [{ label: "Показать", fn: () => reveal(c) }] });
    if (page === "clips") { renderInspector(); $$(`#clips-body [data-id="${c.id}"] .clip-busy`).forEach(e => e.remove()); }
  })();
  const card = $(`#clips-body [data-id="${c.id}"] .clip-thumb`);
  if (card) card.appendChild(h("div", { class: "clip-busy" }, `Сжимаю<div class="bar"><i></i></div>`));
  renderInspector();
}
let lastRemoved = null;
function removeClips(ids) {
  const removed = CLIPS.filter(c => ids.includes(c.id));
  if (!removed.length) return;
  ids.forEach(id => $$(`[data-id="${id}"]`).forEach(el => el.classList.add("is-leaving")));
  setTimeout(() => {
    CLIPS = CLIPS.filter(c => !ids.includes(c.id));
    ids.forEach(id => CL.sel.delete(id));
    lastRemoved = removed;
    if (page === "clips") renderClips(); renderRecent();
  }, reduced ? 1 : 170);
  const n = removed.length;
  toast({ icon: "trash", neutral: true, title: n === 1 ? "Клип в корзине" : `${n} ${plural(n, "файл", "файла", "файлов")} в корзине`, sub: fmtSizeTotal(removed.reduce((s, c) => s + c.bytes, 0)) + " освобождено", actions: [{ label: "Вернуть", fn: () => { if (!lastRemoved) return; CLIPS.push(...lastRemoved); lastRemoved = null; if (page === "clips") renderClips(); renderRecent(); } }], ms: 6000 });
}

/* инспектор */
function renderInspector() {
  const box = $("#inspector");
  const ids = [...CL.sel].filter(id => byId(id));
  if (!ids.length) { box.classList.remove("is-open"); box.innerHTML = ""; return; }
  box.classList.add("is-open");
  if (ids.length > 1) {
    const cs = ids.map(byId), bytes = cs.reduce((s, c) => s + c.bytes, 0), dur = cs.reduce((s, c) => s + (c.dur || 0), 0);
    const vids = cs.filter(c => c.kind === "video");
    box.innerHTML = `<div class="insp"><div class="insp-head"><span class="sec-meta">Выбрано ${ids.length}</span><button class="btn btn-quiet btn-sm btn-icon" style="width:28px" id="insp-close" data-tip="Снять выделение" data-kbd="Esc">${ico("x", "i i-sm")}</button></div>
      <div class="insp-body"><div class="mosaic">${cs.slice(0, 3).map(c => `<img src="${poster(c)}" alt="">`).join("")}${cs.length > 3 ? `<span>ещё ${cs.length - 3}</span>` : ""}</div>
      <div class="insp-title"><h3>${ids.length} ${plural(ids.length, "файл", "файла", "файлов")}</h3></div>
      <div class="insp-sub">${fmtSizeTotal(bytes)}${dur ? `, всего ${fmtDur(dur)} видео` : ""}</div>
      <div class="insp-actions">
        ${vids.length ? `<button class="act" id="m-compress">${ico("box", "i i-sm")}<span class="act-label">Сжать для Discord</span><span class="act-hint">${vids.length} видео</span></button>` : ""}
        <button class="act" id="m-reveal">${ico("folderOpen", "i i-sm")}<span class="act-label">Показать в папке</span></button>
        <div class="act-sep"></div>
        <button class="act is-danger" id="m-del">${ico("trash", "i i-sm")}<span class="act-label">Удалить в корзину</span><span class="act-hint">Del</span></button>
      </div></div></div>`;
    $("#m-compress")?.addEventListener("click", () => vids.forEach(compress));
    $("#m-reveal").addEventListener("click", () => reveal(cs[0]));
    $("#m-del").addEventListener("click", () => removeClips(ids));
    $("#insp-close").addEventListener("click", clearSel);
    return;
  }
  const c = byId(ids[0]);
  const busy = CL.busy[c.id];
  const vid = c.kind === "video";
  box.innerHTML = `<div class="insp"><div class="insp-head"><span class="sec-meta">${vid ? "Видео" : "Скриншот"}</span><button class="btn btn-quiet btn-sm btn-icon" style="width:28px" id="insp-close" data-tip="Закрыть" data-kbd="Esc">${ico("x", "i i-sm")}</button></div>
    <div class="insp-body">
      <div class="insp-preview"><img src="${poster(c)}" alt="">${vid ? `<button class="insp-play" data-tip="Смотреть" data-kbd="Enter">${ico("play")}</button><span class="clip-dur">${fmtDur(c.dur)}</span><span class="clip-scrub" style="opacity:1;width:0"></span>` : ""}</div>
      <div class="insp-title"><h3 id="insp-name">${clipTitle(c)}</h3><button class="btn btn-quiet btn-sm btn-icon" style="width:28px" id="insp-rename" data-tip="Переименовать" data-kbd="F2">${ico("pencil", "i i-sm")}</button></div>
      <div class="insp-sub">${c.name ? GAMES[c.game] + ", " : ""}${dayLabel(c.date)}, ${hhmm(c.date)}</div>
      <dl class="insp-props">
        ${vid ? `<dt>Длина</dt><dd>${fmtDur(c.dur)}</dd><dt>Видео</dt><dd>${c.w}×${c.h}, ${c.fps} к/с</dd><dt>Кодек</dt><dd>${c.codec}, ${c.mbps} Мбит/с</dd><dt>Звук</dt><dd>${c.tracks === 2 ? "Игра и микрофон отдельно" : "Одна дорожка"}</dd>` : `<dt>Снимок</dt><dd>${c.area ? "Область" : "Весь экран"}, ${c.w}×${c.h}</dd><dt>Формат</dt><dd>PNG</dd>`}
        <dt>Размер</dt><dd>${fmtSize(c.bytes)}${c.compressed ? `, для Discord ${num(c.compressed, 1)} МБ` : ""}</dd>
        <dt>Файл</dt><dd class="path" title="${clipPath(c)}">${clipPath(c)}</dd>
      </dl>
      <div class="insp-actions">
        ${vid ? `<button class="btn btn-primary" id="a-edit"><span style="display:inline-flex;gap:8px;align-items:center">${ico("scissors", "i i-sm")}Открыть в редакторе</span><span class="kbd-set kbd-on-accent">${keysHtml("Ctrl E")}</span></button>` : `<button class="btn btn-primary" id="a-copy2"><span style="display:inline-flex;gap:8px;align-items:center">${ico("copy", "i i-sm")}Копировать картинку</span><span class="kbd-set kbd-on-accent">${keysHtml("Ctrl C")}</span></button>`}
        ${vid ? (busy != null ? `<div class="act" id="compress-${c.id}">${ico("box", "i i-sm")}<span class="act-label" style="flex:none">Сжимаю до ${S.attachMb} МБ</span><span class="act-progress"><i style="transform:scaleX(${busy})"></i></span></div>`
               : `<button class="act" id="a-compress">${ico("box", "i i-sm")}<span class="act-label">Сжать для Discord</span><span class="act-hint">до ${S.attachMb} МБ</span></button>`) : ""}
        <button class="act" id="a-reveal">${ico("folderOpen", "i i-sm")}<span class="act-label">Показать в папке</span></button>
        ${vid ? `<button class="act" id="a-copy">${ico("copy", "i i-sm")}<span class="act-label">Копировать файл</span><span class="act-hint">Ctrl C</span></button>` : ""}
        <div class="act-sep"></div>
        <button class="act is-danger" id="a-del">${ico("trash", "i i-sm")}<span class="act-label">Удалить в корзину</span><span class="act-hint">Del</span></button>
      </div>
    </div></div>`;
  $("#insp-close").addEventListener("click", clearSel);
  $("#insp-rename").addEventListener("click", startRename);
  $("#a-edit")?.addEventListener("click", () => openEditor(c));
  $("#a-copy2")?.addEventListener("click", () => copyClip(c));
  $("#a-copy")?.addEventListener("click", () => copyClip(c));
  $("#a-compress")?.addEventListener("click", () => compress(c));
  $("#a-reveal").addEventListener("click", () => reveal(c));
  $("#a-del").addEventListener("click", () => removeClips([c.id]));
  $(".insp-play")?.addEventListener("click", () => openEditor(c, true));
  const pv = $(".insp-preview"), pimg = $("img", pv), ps = $(".clip-scrub", pv);
  if (vid) {
    pv.addEventListener("pointermove", e => { const r = pv.getBoundingClientRect(), p = clamp((e.clientX - r.left) / r.width, 0, .999); pimg.src = frameSrc(c, Math.floor(p * 5)); ps.style.width = p * 100 + "%"; });
    pv.addEventListener("pointerleave", () => { pimg.src = poster(c); ps.style.width = 0; });
  }
}
function startRename() {
  const c = byId([...CL.sel][0]); if (!c || CL.sel.size !== 1) return;
  const hdr = $("#insp-name"); if (!hdr) return;
  const inp = h("input", { value: clipTitle(c), spellcheck: "false" });
  hdr.replaceWith(inp); inp.focus(); inp.select();
  const done = ok => {
    if (!inp.isConnected) return;
    if (ok) { const v = inp.value.trim(); c.name = v && v !== GAMES[c.game] ? v : ""; }
    renderInspector(); $$(`#clips-body [data-id="${c.id}"] .clip-title, #clips-body [data-id="${c.id}"] .lname`).forEach(e => e.textContent = clipTitle(c));
    if (ok) toast({ icon: "pencil", neutral: true, title: "Переименовано", sub: clipPath(c).split("\\").pop() });
  };
  inp.addEventListener("keydown", e => { if (e.key === "Enter") done(true); if (e.key === "Escape") { e.stopPropagation(); done(false); } });
  inp.addEventListener("blur", () => done(true));
}
$("#clip-search").addEventListener("input", e => { CL.q = e.target.value.trim(); clearSel(); renderClipsBody(); });
$("#open-folder").addEventListener("click", () => toast({ icon: "folderOpen", neutral: true, title: "Папка записей открыта", sub: S.saveRoot }));

/* =====================================================================
   НАСТРОЙКИ
   ===================================================================== */
const gpuCodecs = [
  { value: "H.264", label: "H.264", hint: "везде" },
  { value: "HEVC", label: "HEVC", hint: "меньше файл" },
  { value: "AV1", label: "AV1", hint: "RTX 40+", disabled: true },
];
const devicesOut = [{ value: "realtek", label: "Динамики (Realtek(R) Audio)" }, { value: "usb", label: "Наушники (USB Audio Device)" }, { value: "default", label: "Как в Windows" }];
const devicesIn = [{ value: "usb", label: "Микрофон (USB Audio Device)" }, { value: "realtek", label: "Микрофон (Realtek(R) Audio)" }, { value: "default", label: "Как в Windows" }];
const QUALITY_KEYS = ["preset", "res", "fps", "bitrate", "codec", "bits10", "replayLen"];
const HK_LABELS = { save: "Сохранить повтор", save30: "Сохранить последние 30 секунд", toggle: "Включить или выключить повтор", mark: "Поставить метку", recStart: "Начать запись", recStop: "Остановить запись", shot: "Скриншот экрана", area: "Скриншот области", panel: "Панель поверх игры", folder: "Открыть папку записей" };
const HK_DEFAULT = { save: "Alt+F10", save30: "Alt+Shift+F10", toggle: "Alt+F11", mark: "Alt+M", recStart: "Alt+F9", recStop: "Alt+Shift+F9", shot: "Alt+PrintScreen", area: "PrintScreen", panel: "Alt+Q", folder: "Alt+F8" };
const HK_TAKEN = { "Alt+Z": "Занято оверлеем NVIDIA", "Win+Alt+R": "Занято Xbox Game Bar", "Win+Alt+G": "Занято Xbox Game Bar", "PrintScreen": "Windows откроет «Ножницы», если это включено в параметрах", "Alt+Tab": "Системное сочетание Windows", "Alt+F4": "Закрывает окно игры" };

const PANES = [
  { id: "capture", title: "Запись", icon: "monitor", lede: "Качество картинки, длина повтора и что попадает в кадр.", groups: [
    { title: "Качество", rows: [
      { key: "preset", label: "Набор", type: "seg", options: [["eco", "Эконом"], ["normal", "Обычный"], ["high", "Высокий"], ["max", "Максимум"], ["custom", "Своё"]], sub: () => S.preset === "custom" ? "Свои значения ниже. Выберите набор, чтобы подставить готовые" : PRESETS[S.preset].note, onChange: v => { if (v !== "custom") { Object.assign(S, { res: PRESETS[v].res, fps: PRESETS[v].fps, bitrate: PRESETS[v].bitrate }); flashRows(["res", "fps", "bitrate"]); } } },
      { key: "res", label: "Разрешение", type: "seg", options: [[720, "720p"], [1080, "1080p"], [1440, "1440p"], [2160, "4K"]], sub: () => S.res > 1440 ? `<span class="row-warn">Выше разрешения монитора 2560×1440: файл больше, а чётче не станет</span>` : "Монитор 2560×1440, выше родного писать нет смысла", custom: true },
      { key: "fps", label: "Кадров в секунду", type: "seg", options: [[30, "30"], [60, "60"], [120, "120"], [144, "144"]], sub: () => S.fps >= 120 && S.bitrate < 50 ? `<span class="row-warn">Для ${S.fps} к/с нужен битрейт от 50 Мбит/с, иначе картинка поплывёт в движении</span>` : "Монитор 240 Гц. Для обычных клипов хватает 60", custom: true },
      { key: "bitrate", label: "Битрейт", type: "slider", min: 5, max: 80, step: 1, fmt: v => `${v} Мбит/с`, sub: () => `Файл повтора ≈ <b>${Math.round(clipMb(S.replayLen))} МБ</b>, час записи ≈ <b>${num(clipMb(3600) / 1024, 1)} ГБ</b>`, custom: true },
      { key: "codec", label: "Кодек", type: "select", options: gpuCodecs.map(o => [o.value, o.label, o.hint, o.disabled]), sub: () => ({ "H.264": "Открывается везде, но файл больше", HEVC: "Тот же вид при меньшем файле. Telegram и Discord показывают, старые плееры нет", AV1: "" })[S.codec] },
      { key: "bits10", label: "Десять бит цвета", type: "switch", sub: () => S.codec === "HEVC" ? "Ровнее градиенты неба и теней. Файл больше примерно на 10%" : "Только для HEVC", disabled: () => S.codec !== "HEVC" },
    ] },
    { title: "Повтор", meta: () => `в памяти ≈ ${Math.round(ramMb())} МБ`, rows: [
      { key: "replayLen", label: "Длина повтора", type: "seg", options: [[30, "30 с"], [60, "1 мин"], [180, "3 мин"], [300, "5 мин"], [600, "10 мин"], [900, "15 мин"]], sub: () => `Сколько последних минут держать в памяти. Сейчас это ≈ <b>${Math.round(ramMb())} МБ</b> оперативной памяти из 32 ГБ` },
    ] },
    { title: "Что записывать", rows: [
      { key: "source", label: "Источник", type: "select", options: [["auto", "Игра, иначе монитор 1"], ["mon", "Всегда монитор 1"]], sub: () => S.source === "auto" ? "Aura сама находит игру в полноэкранном режиме и пишет только её окно" : "Пишется весь экран: 2560×1440, 240 Гц" },
      { key: "cursor", label: "Курсор мыши", type: "switch", sub: () => "В шутерах обычно не нужен" },
    ] },
  ] },
  { id: "audio", title: "Звук", icon: "speaker", lede: "Что слышно в клипах и как это ложится в файл.", groups: [
    { title: "Источники", rows: [
      { key: "gameAudio", label: "Звук игры", type: "switch", sub: () => "Всё, что играет на выбранном устройстве", meter: "game" },
      { key: "gameDevice", label: "Устройство", type: "select", options: devicesOut.map(o => [o.value, o.label]), sub: () => "", when: () => S.gameAudio, sub2: true },
      { key: "mic", label: "Микрофон", type: "switch", sub: () => "Ваш голос отдельной дорожкой", meter: "mic" },
      { key: "micDevice", label: "Устройство", type: "select", options: devicesIn.map(o => [o.value, o.label]), sub: () => "", when: () => S.mic, sub2: true },
    ] },
    { title: "Файл", rows: [
      { key: "tracks", label: "Дорожки", type: "seg", options: [["mixed", "Одна общая"], ["separate", "Две отдельные"]], sub: () => S.tracks === "separate" ? "Голос можно убрать или свести заново в редакторе" : "Файл откроется одинаково в любом плеере", when: () => S.mic && S.gameAudio },
    ] },
    { title: "Микрофон", rows: [
      { key: "noise", label: "Шумоподавление", type: "switch", sub: () => "Убирает фоновый гул в паузах между фразами", when: () => S.mic },
      { key: "gate", label: "Порог тишины", type: "slider", min: -70, max: -20, step: 1, fmt: v => `${v} дБ`, sub: () => "Тише этого уровня микрофон молчит", when: () => S.mic && S.noise, sub2: true },
    ] },
  ] },
  { id: "keys", title: "Клавиши", icon: "keyboard", lede: "Работают везде, даже когда Aura свёрнута в трей.", groups: [
    { title: "Повтор", rows: ["save", "save30", "toggle", "mark"].map(k => ({ key: k, hk: true, label: HK_LABELS[k], type: "hotkey", isNew: k === "mark", sub: () => k === "mark" ? "Метка появится на ленте и в редакторе, чтобы быстро найти момент" : "" })) },
    { title: "Запись", rows: ["recStart", "recStop"].map(k => ({ key: k, hk: true, label: HK_LABELS[k], type: "hotkey", sub: () => "" })) },
    { title: "Скриншоты", rows: ["shot", "area"].map(k => ({ key: k, hk: true, label: HK_LABELS[k], type: "hotkey", sub: () => k === "area" ? "Экран замирает, остаётся выделить нужное" : "" })) },
    { title: "Aura", rows: ["panel", "folder"].map(k => ({ key: k, hk: true, label: HK_LABELS[k], type: "hotkey", isNew: k === "panel", sub: () => k === "panel" ? "Маленькая панель поверх игры: сохранить, запись, микрофон, скриншот" : "" })), footer: true },
  ] },
  { id: "files", title: "Файлы", icon: "folder", lede: "Куда ложатся записи и как они называются.", groups: [
    { title: "Папки", rows: [
      { key: "saveRoot", label: "Видео", type: "folder" },
      { key: "shotRoot", label: "Скриншоты", type: "folder" },
    ] },
    { title: "Имена", rows: [
      { key: "byGame", label: "Раскладывать по папкам игр", type: "switch", sub: () => S.byGame ? `Например, <span class="path">${S.saveRoot}\\Counter-Strike 2\\</span>` : `Все записи в <span class="path">${S.saveRoot}\\</span>` },
      { key: "template", label: "Шаблон имени", type: "text", sub: () => `Получится <span class="path">${S.template.replace("{game}", "Counter-Strike 2").replace("{date}", "2026-09-21").replace("{time}", "18-26-56")}.mp4</span>` },
    ] },
    { title: "Место", meta: () => "на диске E:", rows: [{ type: "disk" }] },
    { title: "Отправка", rows: [
      { key: "attachMb", label: "Сжимать для Discord до", type: "seg", options: [[10, "10 МБ"], [20, "20 МБ"], [50, "50 МБ"], [500, "500 МБ"]], sub: () => ({ 10: "Лимит Discord без Nitro", 20: "С запасом для чатов и Telegram", 50: "Лимит Nitro Basic", 500: "Лимит Nitro" })[S.attachMb] },
    ] },
  ] },
  { id: "notify", title: "Уведомления", icon: "bell", lede: "Что Aura показывает поверх игры. Фокус у игры не забирается.", groups: [
    { title: "Поверх игры", rows: [
      { key: "notify", label: "Показывать уведомления", type: "switch", sub: () => "После сохранения повтора, записи и скриншота" },
      { key: "corner", label: "Угол экрана", type: "corner", when: () => S.notify, sub: () => ({ TL: "Слева сверху", TC: "Сверху по центру", TR: "Справа сверху", BL: "Слева снизу", BR: "Справа снизу" })[S.corner] },
      { key: "notifySec", label: "Сколько держать", type: "slider", min: 1, max: 6, step: 1, fmt: v => `${v} с`, when: () => S.notify, sub: () => "" },
      { key: "sound", label: "Звук при сохранении", type: "select", options: [["soft", "Мягкий"], ["classic", "Классический"], ["file", "Свой файл…"], ["none", "Без звука"]], sub: () => "Сигнал, что запись легла на диск", play: true },
      { type: "preview", label: "Как это выглядит", sub: () => "Покажем уведомление в выбранном углу" },
    ] },
  ] },
  { id: "app", title: "Приложение", icon: "app", lede: "Запуск, оформление и обновления.", groups: [
    { title: "Запуск", rows: [
      { key: "autostart", label: "Запускать вместе с Windows", type: "switch", sub: () => "" },
      { key: "trayStart", label: "Стартовать свёрнутой в трей", type: "switch", sub: () => "" },
      { key: "autoReplay", label: "Сразу включать повтор", type: "switch", sub: () => "Повтор начинает писаться сразу после запуска" },
    ] },
    { title: "Оформление", rows: [
      { key: "theme", label: "Тема", type: "seg", options: [["dark", "Тёмная"], ["light", "Светлая"], ["system", "Как в Windows"]], sub: () => "", onChange: applyTheme },
      { key: "scale", label: "Размер интерфейса", type: "seg", options: [["auto", "Авто"], ["compact", "Компактный"], ["normal", "Обычный"], ["large", "Крупный"]], sub: () => "Крупнее на 2K и 4K, компактнее на Full HD", onChange: applyScale },
    ] },
    { title: "Обновления", rows: [
      { type: "version" },
      { key: "updates", label: "Проверять при запуске", type: "switch", sub: () => "" },
      { key: "driver", label: "Следить за драйвером видеокарты", type: "switch", sub: () => "Проверка раз в неделю, в фоне" },
    ] },
    { title: "Обслуживание", rows: [
      { type: "button", label: "Папка с логами", sub: () => "Пригодится, если что-то пошло не так", btn: "Открыть", fn: () => toast({ icon: "folderOpen", neutral: true, title: "Открыта папка с логами", sub: "%LOCALAPPDATA%\\Aura\\logs" }) },
      { type: "button", label: "Кэш миниатюр", sub: () => "48 МБ. Миниатюры создадутся заново", btn: "Очистить", fn: () => toast({ icon: "check", title: "Кэш очищен", sub: "Освобождено 48 МБ" }) },
      { type: "button", label: "Сбросить все настройки", sub: () => "Клавиши, папки и качество вернутся к стандартным", btn: "Сбросить…", danger: true, fn: e => openMenu([{ head: "Сбросить все настройки Aura?" }, { label: "Сбросить", icon: "undo", danger: true, fn: () => toast({ icon: "undo", neutral: true, title: "Настройки сброшены", sub: "В прототипе ничего не изменилось" }) }, { label: "Отмена", fn: () => {} }], { anchor: e.currentTarget, minWidth: 240 }) },
    ] },
  ] },
  { id: "system", title: "Система", icon: "cpu", lede: "На чём работает запись и как она себя чувствует.", groups: [
    { title: "Видеокарта", rows: [{ type: "gpu" }] },
    { title: "Конвейер записи", meta: () => "обновляется раз в секунду", rows: [{ type: "live" }] },
    { title: "Компьютер", rows: [
      ["Процессор", "AMD Ryzen 5 9600X, 6 ядер, 12 потоков"], ["Оперативная память", "32 ГБ, 5600 МГц"], ["Материнская плата", "Gigabyte B650 GAMING X AX V2"], ["Система", "Windows 11 IoT Корпоративная LTSC, сборка 26100"], ["Экран", "2560×1440, 240 Гц"],
    ].map(([l, v]) => ({ type: "info", label: l, value: v })) },
  ] },
];
let pane = "capture";
let setNavHead = null;
let pending = null;   // снимок качества до правок

function renderSettings() {
  const nav = $("#set-nav");
  if (!nav.childElementCount) {
    nav.appendChild(h("span", { class: "set-nav-head", id: "set-nav-head" }));
    PANES.forEach(p => {
      const b = h("button", { class: "set-item", "data-pane": p.id }, `${ico(p.icon, "i i-sm")}<span>${p.title}</span>`);
      b.addEventListener("click", () => { $("#set-search").value = ""; selectPane(p.id); });
      nav.appendChild(b);
    });
    setNavHead = () => {
      const b = $(`.set-item[data-pane="${pane}"]`), head = $("#set-nav-head");
      if (!b || $("#set-search").value) { head.style.opacity = 0; return; }
      head.style.opacity = 1; head.style.setProperty("--y", (b.offsetTop + (b.offsetHeight - 18) / 2) + "px");
    };
  }
  selectPane(pane, true);
}
function selectPane(id, silent) {
  pane = id;
  $$(".set-item").forEach(b => b.classList.toggle("is-on", b.dataset.pane === id && !$("#set-search").value));
  setNavHead?.();
  paintCrumbs();
  const p = PANES.find(p => p.id === id);
  const content = $("#set-content");
  content.innerHTML = `<h1 class="pane-title">${p.title}</h1><div class="pane-lede">${p.lede}</div>`;
  p.groups.forEach(g => content.appendChild(renderGroup(g)));
  if (!silent) { $("#set-scroll").scrollTop = 0; content.animate?.([{ opacity: 0, transform: "translateY(4px)" }, { opacity: 1, transform: "none" }], { duration: reduced ? 1 : 200, easing: "cubic-bezier(.22,1,.36,1)" }); }
}
function renderGroup(g, rowsOverride) {
  const box = h("section", { class: "group" });
  box.innerHTML = g.title ? `<div class="group-title">${g.title}${g.meta ? `<span class="sec-meta">${g.meta()}</span>` : ""}</div>` : "";
  const rows = h("div", { class: "rows" });
  (rowsOverride || g.rows).forEach(r => rows.appendChild(renderRow(r)));
  box.appendChild(rows);
  if (g.footer) {
    const f = h("div", { style: "margin-top:12px;display:flex;gap:8px" });
    const b = h("button", { class: "btn btn-sm" }, `${ico("undo", "i i-sm")}Вернуть стандартные`);
    b.addEventListener("click", () => { Object.assign(S.hk, HK_DEFAULT); selectPane("keys", true); syncKeycaps(); toast({ icon: "undo", neutral: true, title: "Клавиши как в Aura по умолчанию", sub: "Сохранить повтор: Alt F10" }); });
    f.appendChild(b); box.appendChild(f);
  }
  return box;
}
function refreshPane() { const st = $("#set-scroll").scrollTop; if ($("#set-search").value) runSettingsSearch(); else selectPane(pane, true); $("#set-scroll").scrollTop = st; }
function flashRows(keys) { requestAnimationFrame(() => keys.forEach(k => { const r = $(`.row[data-key="${k}"]`); if (r) { r.classList.remove("is-flash"); r.offsetWidth; r.classList.add("is-flash"); } })); }

function setVal(r, v) {
  if (r.hk) S.hk[r.key] = v; else S[r.key] = v;
  if (QUALITY_KEYS.includes(r.key)) {
    if (!pending) pending = JSON.parse(JSON.stringify({ preset: S.preset, res: S.res, fps: S.fps, bitrate: S.bitrate, codec: S.codec, bits10: S.bits10, replayLen: S.replayLen }));
    if (r.custom) {
      const hit = Object.entries(PRESETS).find(([, p]) => p.res === S.res && p.fps === S.fps && p.bitrate === S.bitrate);
      S.preset = hit ? hit[0] : "custom";
    }
    const changed = Object.keys(pending).some(k => pending[k] !== S[k]);
    $("#apply-bar").classList.toggle("is-on", changed);
    if (!changed) pending = null;
  }
  r.onChange?.(v);
  if (r.key === "mic" || r.key === "tracks" || r.key === "codec" || r.key === "bitrate") { $("#spec").innerHTML = specHtml(); }
  if (r.key === "mic") { if (!S.mic && !E.micMuted) toggleMic(); else if (S.mic && E.micMuted) toggleMic(); }
}
function renderRow(r) {
  const val = r.hk ? S.hk[r.key] : S[r.key];
  const row = h("div", { class: "row" + (r.sub2 ? " is-sub" : ""), "data-key": r.key || r.type });
  if (r.when && !r.when()) row.classList.add("is-hidden");
  const sub = r.sub ? r.sub() : "";
  const labelHtml = `<div><div class="row-label">${r.label ?? ""}${r.isNew ? '<span class="mark-new">новое</span>' : ""}</div>${sub ? `<div class="row-sub">${sub}</div>` : ""}</div>`;
  const ctl = h("div", { class: "row-ctl" });
  const rerenderAfter = () => refreshPane();

  switch (r.type) {
    case "seg": {
      row.innerHTML = labelHtml; const seg = h("div", { class: "seg" });
      makeSeg(seg, { options: r.options.map(([value, label]) => ({ value, label })), get: () => r.hk ? S.hk[r.key] : S[r.key], set: v => { setVal(r, v); setTimeout(rerenderAfter, 190); } });
      ctl.appendChild(seg); break;
    }
    case "switch": {
      row.innerHTML = labelHtml;
      if (r.meter) ctl.appendChild(h("span", { class: "meter", id: "sm-" + r.meter }, "<i></i>".repeat(10)));
      const sw = h("button", { class: "switch", role: "switch" }, "<span></span>");
      if (r.disabled?.()) { sw.style.opacity = .4; sw.style.pointerEvents = "none"; }
      makeSwitch(sw, { get: () => S[r.key], set: v => { setVal(r, v); setTimeout(rerenderAfter, 170); } });
      ctl.appendChild(sw); break;
    }
    case "select": {
      row.innerHTML = labelHtml; const b = h("button", { class: "select" });
      makeSelect(b, { options: () => r.options.map(([value, label, hint, disabled]) => ({ value, label, hint, disabled })), get: () => S[r.key], set: v => { setVal(r, v); rerenderAfter(); } });
      ctl.appendChild(b);
      if (r.play) { const p = h("button", { class: "btn btn-icon", style: "width:32px", "data-tip": "Послушать" }, ico("play", "i i-sm")); p.addEventListener("click", () => toast({ icon: "speaker", neutral: true, title: "Звук сохранения", sub: "Здесь прозвучал бы выбранный сигнал", ms: 1800 })); ctl.appendChild(p); }
      break;
    }
    case "slider": {
      row.innerHTML = labelHtml;
      const wrap = h("div", { class: "slider" });
      const inp = h("input", { type: "range", min: r.min, max: r.max, step: r.step, value: val });
      const out = h("span", { class: "val" }, r.fmt(val));
      const paint = () => inp.style.setProperty("--p", ((inp.value - r.min) / (r.max - r.min) * 100) + "%");
      inp.addEventListener("input", () => { setVal(r, +inp.value); out.textContent = r.fmt(+inp.value); paint(); const s = $(".row-sub", row); if (s && r.sub) s.innerHTML = r.sub(); updateGroupMeta(); });
      inp.addEventListener("change", () => { if (r.key === "bitrate") refreshPane(); });
      paint(); wrap.append(inp, out); ctl.appendChild(wrap); break;
    }
    case "hotkey": {
      row.innerHTML = labelHtml;
      ctl.appendChild(hotkeyControl(r, row)); break;
    }
    case "folder": {
      row.innerHTML = `<div><div class="row-label">${r.label}</div><div class="row-sub path">${val}</div></div>`;
      const o = h("button", { class: "btn btn-quiet btn-icon", style: "width:32px", "data-tip": "Открыть" }, ico("folderOpen", "i i-sm"));
      o.addEventListener("click", () => toast({ icon: "folderOpen", neutral: true, title: "Открыто в проводнике", sub: val }));
      const c = h("button", { class: "btn btn-sm" }, "Изменить…");
      c.addEventListener("click", () => toast({ icon: "folder", neutral: true, title: "Здесь откроется выбор папки", sub: "Системный диалог Windows" }));
      ctl.append(o, c); break;
    }
    case "text": {
      row.innerHTML = labelHtml;
      const inp = h("input", { class: "field", value: val, spellcheck: "false" });
      inp.addEventListener("input", () => { S[r.key] = inp.value; $(".row-sub", row).innerHTML = r.sub(); });
      const vars = h("button", { class: "btn btn-quiet btn-sm", "data-tip": "Что можно вставить" }, "{…}");
      vars.addEventListener("click", () => openMenu([{ head: "Подстановки" }, ...[["{game}", "Название игры"], ["{date}", "Дата, 2026-09-21"], ["{time}", "Время, 18-26-56"], ["{length}", "Длина, 3m00s"]].map(([k, l]) => ({ label: l, hint: k, fn: () => { inp.value += " " + k; inp.dispatchEvent(new Event("input")); } }))], { anchor: vars, minWidth: 240 }));
      ctl.append(inp, vars); break;
    }
    case "corner": {
      row.innerHTML = labelHtml;
      const c = h("div", { class: "corner", role: "radiogroup" });
      ["TL", "TC", "TR", "BL", "gap", "BR"].forEach(k => {
        const b = h("button", { class: k === "gap" ? "gap" : "", "data-tip": k === "gap" ? null : ({ TL: "Слева сверху", TC: "Сверху по центру", TR: "Справа сверху", BL: "Слева снизу", BR: "Справа снизу" })[k] });
        if (k !== "gap") { b.classList.toggle("is-on", S.corner === k); b.addEventListener("click", () => { S.corner = k; $$("button", c).forEach(x => x.classList.remove("is-on")); b.classList.add("is-on"); $(".row-sub", row).innerHTML = r.sub(); previewGameToast(); }); }
        c.appendChild(b);
      });
      ctl.appendChild(c); break;
    }
    case "preview": {
      row.innerHTML = labelHtml;
      const a = h("button", { class: "btn btn-sm" }, `${ico("film", "i i-sm")}Повтор`);
      const b = h("button", { class: "btn btn-sm" }, `${ico("crop", "i i-sm")}Скриншот`);
      a.addEventListener("click", () => previewGameToast("replay")); b.addEventListener("click", () => previewGameToast("shot"));
      ctl.append(a, b); break;
    }
    case "disk": {
      row.classList.add("row-wide");
      const games = Object.keys(GAMES).map(k => ({ k, b: CLIPS.filter(c => c.game === k).reduce((s, c) => s + c.bytes, 0) })).filter(x => x.b).sort((a, b) => b.b - a.b);
      const total = games.reduce((s, x) => s + x.b, 0);
      const shades = PAL.light ? ["#3A444C", "#5D6770", "#838C94", "#A9B1B7", "#C9CFD3"] : ["#D5DBDF", "#9CA5AD", "#6F7982", "#4C555D", "#353C42"];
      const top = games.slice(0, 4), rest = games.slice(4).reduce((s, x) => s + x.b, 0);
      const parts = [...top.map((x, i) => ({ l: GAMES[x.k], b: x.b, c: shades[i] })), ...(rest ? [{ l: "Остальные", b: rest, c: shades[4] }] : [])];
      row.innerHTML = `<div><div style="display:flex;align-items:baseline;gap:12px"><div class="row-label">Записи занимают ${fmtSizeTotal(total)}</div><div class="fill"></div><div class="row-sub" style="margin:0">свободно 394,6 ГБ, это ≈ ${Math.round(FREE_BYTES / (clipMb(3600) * MIB))} ч записи</div></div>
        <div class="disk">${parts.map(p => `<i style="flex:${p.b} 0 0;background:${p.c}"></i>`).join("")}</div>
        <div class="legend">${parts.map(p => `<span><i style="background:${p.c}"></i>${p.l} ${fmtSizeTotal(p.b)}</span>`).join("")}</div></div>`;
      break;
    }
    case "version": {
      row.innerHTML = `<div><div class="row-label">Aura 1.1.105</div><div class="row-sub" id="upd-sub">Проверено сегодня в 16:04</div></div>`;
      const b = h("button", { class: "btn btn-sm" }, "Проверить");
      b.addEventListener("click", () => { b.innerHTML = "Проверяю…"; b.classList.add("is-disabled"); setTimeout(() => { b.innerHTML = "Проверить"; b.classList.remove("is-disabled"); $("#upd-sub").innerHTML = `<span class="h-ok">Установлена последняя версия</span>`; }, 1100); });
      ctl.appendChild(b); break;
    }
    case "button": {
      row.innerHTML = labelHtml;
      const b = h("button", { class: "btn btn-sm" + (r.danger ? " btn-danger" : "") }, r.btn);
      b.addEventListener("click", e => r.fn(e)); ctl.appendChild(b); break;
    }
    case "gpu": {
      row.innerHTML = `<div style="display:flex;gap:12px;align-items:center">${ico("cpu")}<div><div class="row-label">NVIDIA GeForce RTX 3070</div><div class="row-sub">8 ГБ, драйвер 616.64. Кодирует NVENC, процессор почти не занят</div></div></div>`;
      const b = h("button", { class: "btn btn-sm" }, `${ico("shield", "i i-sm")}Проверить драйвер`);
      b.addEventListener("click", () => { b.classList.add("is-disabled"); setTimeout(() => { b.classList.remove("is-disabled"); toast({ icon: "check", title: "Драйвер свежий", sub: "616.64, обновлений нет" }); }, 900); });
      ctl.appendChild(b); break;
    }
    case "live": {
      row.classList.add("row-wide"); row.style.padding = "0";
      row.innerHTML = `<div class="sys-live">
        <div class="sys-cell"><div class="row-sub">Захват</div><div class="v"><span id="sys-cap">${S.fps},0</span> <span class="row-sub" style="display:inline">к/с</span></div><canvas id="spark-cap"></canvas></div>
        <div class="sys-cell"><div class="row-sub">Кодирование</div><div class="v"><span id="sys-enc">${S.fps},0</span> <span class="row-sub" style="display:inline">к/с</span></div><canvas id="spark-enc"></canvas></div>
        <div class="sys-cell"><div class="row-sub">Память приложения</div><div class="v"><span id="sys-mem">1015</span> <span class="row-sub" style="display:inline">МБ</span></div><canvas id="spark-mem"></canvas></div></div>`;
      break;
    }
    case "info": {
      row.innerHTML = `<div class="row-label" style="font-weight:400;color:var(--t2)">${r.label}</div>`;
      ctl.innerHTML = `<span>${r.value}</span>`; break;
    }
  }
  if (!["disk", "live"].includes(r.type)) row.appendChild(ctl);
  return row;
}
function updateGroupMeta() { $$(".group").forEach((g, i) => { const p = PANES.find(p => p.id === pane); const gr = p?.groups[i]; const m = $(".group-title .sec-meta", g); if (gr?.meta && m) m.textContent = gr.meta(); }); }

/* горячие клавиши */
let listening = null;
function hotkeyControl(r, row) {
  const b = h("button", { class: "hk" });
  const paint = (text, cls) => {
    const v = S.hk[r.key];
    b.className = "hk" + (cls ? " " + cls : "");
    b.innerHTML = text ? `<span class="hk-none">${text}</span>` : v ? `<span class="kbd-set">${keysHtml(v.replace(/\+/g, " "))}</span><span class="hk-x" data-tip="Убрать">${ico("x", "i i-xs")}</span>` : `<span class="hk-none">Не задано</span>`;
    $(".hk-x", b)?.addEventListener("click", e => { e.stopPropagation(); S.hk[r.key] = ""; paint(); syncKeycaps(); });
  };
  const warnSlot = () => $(".row-sub", row) || (() => { const d = h("div", { class: "row-sub" }); row.firstElementChild.appendChild(d); return d; })();
  const baseSub = r.sub();
  b.addEventListener("click", () => {
    if (listening) listening.cancel();
    paint("Нажмите сочетание", "is-listening");
    warnSlot().innerHTML = "Esc, чтобы отменить, Backspace, чтобы убрать";
    listening = {
      key(e) {
        e.preventDefault();
        if (e.key === "Escape") { this.cancel(); return; }
        if (e.key === "Backspace") { S.hk[r.key] = ""; this.done(); return; }
        if (["Control", "Shift", "Alt", "Meta"].includes(e.key)) return;
        const mods = [e.ctrlKey && "Ctrl", e.altKey && "Alt", e.shiftKey && "Shift", e.metaKey && "Win"].filter(Boolean);
        let k = e.key.length === 1 ? e.key.toUpperCase() : e.key;
        if (k === " ") k = "Space"; if (e.code?.startsWith("Key")) k = e.code.slice(3); if (e.code?.startsWith("Digit")) k = e.code.slice(5);
        const combo = [...mods, k].join("+");
        const dupe = Object.entries(S.hk).find(([kk, v]) => v === combo && kk !== r.key);
        const taken = HK_TAKEN[combo];
        S.hk[r.key] = combo;
        this.done(dupe ? `<span class="row-warn">Это сочетание было у «${HK_LABELS[dupe[0]]}», там оно снято</span>` : taken ? `<span class="row-warn">${taken}. Лучше выбрать другое</span>` : null);
        if (dupe) S.hk[dupe[0]] = "";
      },
      cancel() { listening = null; paint(); warnSlot().innerHTML = baseSub; },
      done(msg) { listening = null; paint(null, msg ? "is-conflict" : ""); warnSlot().innerHTML = msg || baseSub; syncKeycaps(); if (msg && msg.includes("снято")) setTimeout(refreshPane, 1800); },
    };
  });
  paint();
  return b;
}
function syncKeycaps() {
  $$(".btn-save .kbd-set, .capsule-save .kbd-set").forEach(e => e.innerHTML = keysHtml((S.hk.save || "").replace(/\+/g, " ")));
  $("#btn-shot").dataset.kbd = (S.hk.area || "").replace(/\+/g, " ");
  $("#open-overlay").dataset.kbd = (S.hk.panel || "").replace(/\+/g, " ");
  $("#btn-rec").dataset.tip = S.hk.recStart ? "Начать запись" : "Клавиша не назначена";
  $("#btn-rec").dataset.kbd = (S.hk.recStart || "").replace(/\+/g, " ");
  $(".qp .qp-head .kbd-set").innerHTML = keysHtml((S.hk.panel || "").replace(/\+/g, " "));
  paintQpTiles(true);
}

/* применение качества */
$("#apply-ok").addEventListener("click", () => {
  pending = null; $("#apply-bar").classList.remove("is-on");
  $("#spec").innerHTML = specHtml();
  if (E.replay !== "off") { E.fillStart = E.t; E.sel = null; E.markers = []; paintSel(); }
  paintState(); renderHealth();
  toast({ icon: "check", title: "Качество применено", sub: `${RES_W[S.res]}×${S.res}, ${S.fps} к/с, ${S.bitrate} Мбит/с. Повтор набирается заново` });
});
$("#apply-revert").addEventListener("click", () => {
  if (pending) Object.assign(S, pending);
  pending = null; $("#apply-bar").classList.remove("is-on"); refreshPane();
});

/* поиск по настройкам */
$("#set-search").addEventListener("input", runSettingsSearch);
function runSettingsSearch() {
  const q = $("#set-search").value.trim().toLowerCase();
  $$(".set-item").forEach(b => b.classList.toggle("is-on", !q && b.dataset.pane === pane));
  setNavHead?.();
  if (!q) { selectPane(pane, true); return; }
  const content = $("#set-content");
  content.innerHTML = `<h1 class="pane-title">Поиск</h1><div class="pane-lede">По запросу «${$("#set-search").value.trim()}»</div>`;
  let found = 0;
  PANES.forEach(p => p.groups.forEach(g => {
    const rows = g.rows.filter(r => r.label && (r.label + " " + (r.sub ? r.sub().replace(/<[^>]+>/g, "") : "") + " " + g.title + " " + p.title).toLowerCase().includes(q));
    if (!rows.length) return;
    found += rows.length;
    const grp = renderGroup({ title: `${p.title}, ${g.title.toLowerCase()}` }, rows);
    $$(".row-label", grp).forEach(l => { l.innerHTML = l.innerHTML.replace(new RegExp(`(${q.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")})`, "i"), "<mark>$1</mark>"); });
    content.appendChild(grp);
  }));
  $$(".set-item").forEach(b => {
    const p = PANES.find(p => p.id === b.dataset.pane);
    const n = p.groups.reduce((s, g) => s + g.rows.filter(r => r.label && (r.label + " " + g.title + " " + p.title).toLowerCase().includes(q)).length, 0);
    let c = $(".set-count", b); if (!c) { c = h("span", { class: "set-count" }); b.appendChild(c); }
    c.textContent = n || "";
  });
  if (!found) content.insertAdjacentHTML("beforeend", `<div class="empty" style="min-height:260px"><div><h3>Такой настройки нет</h3><p>Попробуйте другое слово: «битрейт», «микрофон», «папка», «клавиша».</p></div></div>`);
}
$("#set-search").addEventListener("blur", () => { if (!$("#set-search").value) $$(".set-count").forEach(c => c.textContent = ""); });

/* тема и масштаб */
function applyTheme() {
  const t = S.theme === "system" ? (matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark") : S.theme;
  document.documentElement.dataset.theme = t;
  readPalette();
  if (page === "settings" && pane === "files") refreshPane();
}
function applyScale() { document.body.style.zoom = { auto: 1, compact: .92, normal: 1, large: 1.1 }[S.scale]; moveRailHead(); }

/* уведомление-образец в выбранном углу */
function previewGameToast(kind = "replay") {
  const host = h("div", { style: "position:fixed;inset:0;z-index:58;pointer-events:none" });
  const pos = { TL: "top:24px;left:24px", TC: "top:24px;left:50%;transform:translateX(-50%)", TR: "top:24px;right:24px", BL: "bottom:24px;left:24px", BR: "bottom:24px;right:24px" }[S.corner];
  host.innerHTML = `<div class="game-toasts" style="position:absolute;inset:auto;${pos};align-items:${S.corner.endsWith("L") ? "flex-start" : S.corner === "TC" ? "center" : "flex-end"}"></div>`;
  document.body.appendChild(host);
  const holder = $(".game-toasts", host);
  const el = h("div", { class: "toast" });
  const c = CLIPS.find(c => c.kind === (kind === "shot" ? "shot" : "video")) || CLIPS[0];
  el.style.setProperty("--toast-ms", S.notifySec * 1000 + "ms");
  el.innerHTML = `${c ? `<img class="toast-thumb" src="${poster(c)}" alt="">` : ""}<div class="toast-text"><div class="toast-title">${kind === "shot" ? "Скриншот скопирован" : "Повтор сохранён"}</div><div class="toast-sub">${kind === "shot" ? "Лежит в буфере обмена" : `Counter-Strike 2, ${fmtDur(E.L)}, ${Math.round(clipMb(E.L))} МБ`}</div></div><div class="toast-bar"><i></i></div>`;
  holder.appendChild(el);
  setTimeout(() => { el.classList.add("is-leaving"); setTimeout(() => host.remove(), 200); }, S.notifySec * 1000);
}

/* =====================================================================
   ПАНЕЛЬ ПОВЕРХ ИГРЫ
   ===================================================================== */
const scene = { open: false, focus: 0 };
const sceneEl = $("#scene");
const QP_TILES = [
  { id: "rec", label: "Запись", fn: () => toggleRec() },
  { id: "mic", label: "Микрофон", fn: () => toggleMic() },
  { id: "replay", label: "Повтор", fn: () => E.replay === "off" ? startReplay() : stopReplay() },
  { id: "shot", label: "Скриншот", fn: () => screenshot() },
];
function paintQpTiles(rebuild) {
  const grid = $("#qp-grid");
  if (!grid.childElementCount || rebuild) {
    grid.innerHTML = "";
    QP_TILES.forEach((t, i) => {
      const b = h("button", { class: "qp-tile", "data-i": i + 1 });
      b.addEventListener("click", () => { t.fn(); scene.focus = i + 1; paintQpFocus(); });
      b.addEventListener("pointerenter", () => { scene.focus = i + 1; paintQpFocus(); });
      grid.appendChild(b);
    });
  }
  const [rec, mic, rep, shot] = grid.children;
  const set = (b, icon, label, state, cls) => { b.className = "qp-tile" + (cls ? " " + cls : "") + (scene.focus === +b.dataset.i ? " is-focus" : ""); b.innerHTML = `<span class="qp-tile-num">${b.dataset.i}</span>${ico(icon)}<span class="qp-tile-label">${label}</span><span class="qp-tile-state">${state}</span>`; };
  set(rec, "rec", E.rec ? "Стоп" : "Запись", E.rec ? fmtDur(E.t - E.recStart) : (S.hk.recStart || "выкл"), E.rec ? "is-rec" : "");
  set(mic, E.micMuted ? "micOff" : "mic", "Микрофон", E.micMuted ? "выкл" : "вкл", E.micMuted ? "is-off" : "");
  set(rep, "replay", "Повтор", E.replay === "off" ? "выкл" : fmtDur(Math.min(E.L, E.t - (E.fillStart ?? E.t))), E.replay === "on" ? "is-on" : E.replay === "off" ? "is-off" : "");
  set(shot, "crop", "Скриншот", S.hk.area || "область");
}
function paintQpFocus() {
  $$(".qp-tile").forEach(b => b.classList.toggle("is-focus", scene.focus === +b.dataset.i));
  $("#qp-save").classList.toggle("is-focus", scene.focus === 0);
}
function openOverlay() {
  if (scene.open) { closeOverlay(); return; }
  scene.open = true; scene.focus = 0;
  closeMenu();
  sceneEl.classList.add("is-shown"); sceneEl.setAttribute("aria-hidden", "false");
  requestAnimationFrame(() => requestAnimationFrame(() => sceneEl.classList.add("is-in")));
  paintQpTiles(); paintQpFocus();
  $("#qp-save").focus({ preventScroll: true });
}
function closeOverlay() {
  if (!scene.open) return;
  scene.open = false;
  sceneEl.classList.remove("is-in");
  setTimeout(() => { if (!scene.open) { sceneEl.classList.remove("is-shown"); sceneEl.setAttribute("aria-hidden", "true"); $("#game-toasts").innerHTML = ""; } }, 240);
}
$("#open-overlay").addEventListener("click", openOverlay);
$("#qp-open").addEventListener("click", () => { closeOverlay(); setPage("capture"); });
sceneEl.addEventListener("pointerdown", e => { if (!e.target.closest(".qp, .toast")) closeOverlay(); });
function overlayKey(e) {
  if (e.key === "Escape") { closeOverlay(); return true; }
  if (["1", "2", "3", "4"].includes(e.key)) { const i = +e.key; scene.focus = i; QP_TILES[i - 1].fn(); paintQpFocus(); return true; }
  if (e.key === "ArrowRight") { scene.focus = scene.focus === 0 ? 1 : Math.min(4, scene.focus + 1); paintQpFocus(); return true; }
  if (e.key === "ArrowLeft") { scene.focus = scene.focus <= 1 ? (scene.focus === 1 ? 1 : 0) : scene.focus - 1; paintQpFocus(); return true; }
  if (e.key === "ArrowDown") { if (scene.focus === 0) scene.focus = 1; paintQpFocus(); return true; }
  if (e.key === "ArrowUp") { scene.focus = 0; paintQpFocus(); $("#qp-save").focus(); return true; }
  if (e.key === "Enter" || e.key === " ") { if (scene.focus === 0) saveReplay(); else QP_TILES[scene.focus - 1].fn(); paintQpFocus(); return true; }
  return false;
}

/* =====================================================================
   Клавиатура
   ===================================================================== */
function comboOf(e) {
  const mods = [e.ctrlKey && "Ctrl", e.altKey && "Alt", e.shiftKey && "Shift"].filter(Boolean);
  let k = e.key.length === 1 ? e.key.toUpperCase() : e.key;
  if (e.code?.startsWith("Key")) k = e.code.slice(3); if (e.code?.startsWith("Digit")) k = e.code.slice(5);
  return [...mods, k].join("+");
}
document.addEventListener("keydown", e => {
  if (listening) { listening.key(e); return; }
  if (openMenuState && openMenuState.key(e)) { e.preventDefault(); return; }
  const combo = comboOf(e);
  const typing = e.target instanceof Element && e.target.matches("input, textarea");
  if (combo === S.hk.save) { e.preventDefault(); saveReplay(); return; }
  if (combo === S.hk.panel) { e.preventDefault(); openOverlay(); return; }
  if (combo === S.hk.area && !typing) { e.preventDefault(); screenshot(); return; }
  if (S.hk.mark && combo === S.hk.mark && E.replay !== "off") { E.markers.push({ type: "mark", a: E.t }); toast({ icon: "mark", neutral: true, title: "Метка поставлена", sub: "Найдёте её на ленте и в редакторе", game: scene.open, ms: 1800 }); return; }
  if (scene.open) { if (overlayKey(e)) e.preventDefault(); return; }
  if (e.ctrlKey && e.key === "1") { e.preventDefault(); setPage("capture"); return; }
  if (e.ctrlKey && e.key === "2") { e.preventDefault(); setPage("clips"); return; }
  if (e.ctrlKey && (e.key === "," || e.code === "Comma")) { e.preventDefault(); setPage("settings"); return; }
  if (typing) { if (e.key === "Escape") e.target.blur(); return; }
  if (page === "capture" && e.key === "Escape" && E.sel) { E.sel = null; paintSel(); return; }
  if (page === "clips") {
    if (e.ctrlKey && e.code === "KeyF") { e.preventDefault(); $("#clip-search").focus(); return; }
    if (e.ctrlKey && e.code === "KeyA") { e.preventDefault(); visibleClips().forEach(c => CL.sel.add(c.id)); paintSelection(); return; }
    if (e.key === "Escape" && CL.sel.size) { clearSel(); return; }
    const one = CL.sel.size === 1 ? byId([...CL.sel][0]) : null;
    if (e.key === "Delete" && CL.sel.size) { removeClips([...CL.sel]); return; }
    if (e.key === "F2" && one) { e.preventDefault(); startRename(); return; }
    if (e.key === "Enter" && one) { openEditor(one, true); return; }
    if (e.ctrlKey && e.code === "KeyE" && one) { e.preventDefault(); openEditor(one); return; }
    if (e.ctrlKey && e.code === "KeyC" && one) { copyClip(one); return; }
    if (["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(e.key)) { e.preventDefault(); moveSel(e.key); return; }
  }
  if (page === "settings" && e.ctrlKey && e.code === "KeyF") { e.preventDefault(); $("#set-search").focus(); }
});
function moveSel(key) {
  const cards = $$("#clips-body [data-id]");
  if (!cards.length) return;
  const cur = cards.find(c => CL.sel.has(+c.dataset.id) && +c.dataset.id === CL.anchor) || cards.find(c => CL.sel.has(+c.dataset.id));
  let next;
  if (!cur) next = cards[0];
  else {
    const r = cur.getBoundingClientRect(), cx = r.left + r.width / 2, cy = r.top + r.height / 2;
    const i = cards.indexOf(cur);
    if (key === "ArrowRight") next = cards[i + 1];
    else if (key === "ArrowLeft") next = cards[i - 1];
    else {
      const dir = key === "ArrowDown" ? 1 : -1;
      next = cards.filter(c => { const q = c.getBoundingClientRect(); return dir > 0 ? q.top > r.bottom - 4 : q.bottom < r.top + 4; })
        .sort((a, b) => { const qa = a.getBoundingClientRect(), qb = b.getBoundingClientRect(); return (Math.abs(qa.top - cy) - Math.abs(qb.top - cy)) * 4 + Math.abs(qa.left + qa.width / 2 - cx) - Math.abs(qb.left + qb.width / 2 - cx); })[0];
    }
  }
  if (!next) return;
  selectOnly(+next.dataset.id);
  const el = $(`#clips-body [data-id="${next.dataset.id}"]`); el?.focus({ preventScroll: true }); el?.scrollIntoView({ block: "nearest" });
}

/* =====================================================================
   Демо-панель
   ===================================================================== */
const DEMO = { empty: false };
function renderDemo() {
  const p = $("#demo-panel");
  p.innerHTML = `<h4>Состояния прототипа</h4>
    <div class="demo-row"><span>Повтор</span><div class="seg" id="d-replay"></div></div>
    <div class="demo-row"><span>Буфер с нуля</span><button class="btn btn-sm" id="d-fill">Начать</button></div>
    <div class="demo-row"><span>Пустая библиотека</span><button class="switch" id="d-empty" role="switch"><span></span></button></div>
    <div class="demo-row"><span>Время быстрее ×8</span><button class="switch" id="d-speed" role="switch"><span></span></button></div>
    <div class="demo-row"><span>Тема</span><div class="seg" id="d-theme"></div></div>
    <p class="demo-note">${S.hk.save} сохранить, ${S.hk.area} скриншот, ${S.hk.panel.replace("+", " ")} панель поверх игры, Ctrl 1 и Ctrl 2 разделы, Ctrl , настройки.</p>`;
  makeSeg($("#d-replay"), { options: [{ value: "on", label: "Идёт" }, { value: "off", label: "Выкл" }, { value: "error", label: "Сбой" }], get: () => E.replay, set: v => {
    if (v === "on") { if (E.replay === "off") startReplay(); else { E.replay = "on"; E.errorStart = null; } }
    else if (v === "off") stopReplay();
    else { if (E.replay === "off") startReplay(); E.replay = "error"; E.errorStart = E.t; }
    paintState(); renderHealth();
  } });
  $("#d-fill").addEventListener("click", () => { if (E.replay === "off") startReplay(); else { E.replay = "on"; E.errorStart = null; E.fillStart = E.t - 20; E.markers = []; E.sel = null; paintSel(); } paintState(); renderHealth(); $("#d-replay")._paint(true); });
  makeSwitch($("#d-empty"), { get: () => DEMO.empty, set: v => { DEMO.empty = v; if (v) { EMPTY_BACKUP.list = CLIPS; CLIPS = []; } else { CLIPS = EMPTY_BACKUP.list || CLIPS; } CL.sel.clear(); renderRecent(); if (page === "clips") renderClips(); } });
  makeSwitch($("#d-speed"), { get: () => E.speed > 1, set: v => { E.speed = v ? 8 : 1; } });
  makeSeg($("#d-theme"), { options: [{ value: "dark", label: "Тёмная" }, { value: "light", label: "Светлая" }], get: () => document.documentElement.dataset.theme, set: v => { S.theme = v; applyTheme(); if (page === "settings") refreshPane(); } });
}
$("#demo-toggle").addEventListener("click", () => { const d = $("#demo"); d.classList.toggle("is-open"); if (d.classList.contains("is-open")) renderDemo(); });
document.addEventListener("pointerdown", e => { if (!e.target.closest("#demo") && !e.target.closest(".menu")) $("#demo").classList.remove("is-open"); });

/* =====================================================================
   Главный цикл
   ===================================================================== */
const sparks = { cap: [], enc: [], mem: [] };
let last = performance.now(), secAcc = 0, stepAcc = 0;
function frame(now) {
  // время идёт по часам, а не по числу кадров: фоновая вкладка рисует реже
  const dt = Math.min(1, (now - last) / 1000); last = now;
  E.t += dt * E.speed;
  if (E.sel && E.sel.b != null && E.sel.b < E.t - E.L) { E.sel = null; paintSel(); }
  if (E.sel && E.sel.a < E.t - E.L) E.sel.a = E.t - E.L;
  E.markers = E.markers.filter(m => (m.b ?? m.a) > E.t - 900);
  secAcc += dt; stepAcc += dt;
  // при «меньше движения» лента сдвигается раз в секунду, а не плавно
  const drawNow = !reduced || stepAcc >= 1;
  if (drawNow) stepAcc = 0;
  if (!document.hidden && drawNow) {
    if (scene.open) drawTape($("#qp-tape"), "mini");
    if (page === "capture") { drawTape(tapeCv, "main"); paintMeters(); }
    else drawTape($("#capsule-wave"), "capsule");
    if (page === "settings" && pane === "audio") { const g = $("#sm-game"), m = $("#sm-mic"); const lv = (el, a) => { if (!el) return; const n = Math.round(a * 10); [...el.children].forEach((b, i) => { b.className = i < n ? (i >= 8 ? "hot" : "on") : ""; b.style.height = (4 + i) + "px"; }); }; lv(g, S.gameAudio ? gameAmp(E.t) : 0); lv(m, S.mic && !E.micMuted ? micAmp(E.t) : 0); }
  }
  if (secAcc >= 1) {
    secAcc = 0;
    paintState();
    sparks.cap.push(E.replay === "on" ? S.fps - hash(E.t) * .4 : 0); sparks.enc.push(E.replay === "off" ? 0 : S.fps - hash(E.t + 1) * .2); sparks.mem.push(990 + hash(E.t + 2) * 60);
    for (const k in sparks) if (sparks[k].length > 60) sparks[k].shift();
    if (page === "settings" && pane === "system") paintSparks();
  } else if (E.rec || !reduced) {
    // таймер записи и счётчик набора обновляются чаще, чем раз в секунду
    if (E.rec || (E.fillStart != null && E.t - E.fillStart < E.L)) paintStateLight();
  }
  requestAnimationFrame(frame);
}
let lightTick = 0;
function paintStateLight() { if (++lightTick % 6) return; paintState(); }
function paintSparks() {
  const set = (id, arr, min, max, fmt) => {
    const cv = $("#spark-" + id); if (!cv) return;
    const { dpr, W, H } = fitCanvas(cv), ctx = cv.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0); ctx.clearRect(0, 0, W, H);
    ctx.strokeStyle = PAL.acc; ctx.lineWidth = 1.5; ctx.beginPath();
    arr.forEach((v, i) => { const x = W - (arr.length - 1 - i) * (W / 59), y = H - 2 - (clamp(v, min, max) - min) / (max - min) * (H - 4); i ? ctx.lineTo(x, y) : ctx.moveTo(x, y); });
    ctx.stroke();
    const el = $("#sys-" + id); if (el && arr.length) el.textContent = fmt(arr.at(-1));
  };
  set("cap", sparks.cap, 0, S.fps + 2, v => num(v, 1));
  set("enc", sparks.enc, 0, S.fps + 2, v => num(v, 1));
  set("mem", sparks.mem, 900, 1100, v => Math.round(v));
}

/* =====================================================================
   Запуск
   ===================================================================== */
readPalette();
paintKbds();
$("#spec").innerHTML = specHtml();
paintState();
paintSel();
renderHealth();
setPage("capture");
requestAnimationFrame(() => { fitRecent(); renderRecent(); moveRailHead(); });
new ResizeObserver(() => fitRecent()).observe($("#recent-grid"));
document.fonts?.ready.then(() => { moveRailHead(); $$(".seg").forEach(s => s._paint?.(false)); });
requestAnimationFrame(frame);
