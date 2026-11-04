// Small DOM + formatting helpers shared by every view. CSP-safe: never sets a `style`
// attribute or inline handler; styles go through classes or the CSSOM (el.style.x = ...).

const PROPS = new Set(['value', 'checked', 'disabled', 'hidden', 'readOnly', 'selected', 'open', 'indeterminate']);

function append(el, children) {
  for (const c of children) {
    if (c == null || c === false || c === true) continue;
    if (Array.isArray(c)) append(el, c);
    else el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
}

/** h('button', { class: 'btn', onclick: fn, 'aria-label': 'x' }, 'Text', childNode, [more]) */
export function h(tag, props, ...children) {
  const el = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v == null || v === false) continue;
      if (k === 'class') el.className = Array.isArray(v) ? v.filter(Boolean).join(' ') : v;
      else if (k === 'dataset') Object.assign(el.dataset, v);
      else if (k === 'text') el.textContent = v;
      else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
      else if (PROPS.has(k)) el[k] = v;
      else el.setAttribute(k, v === true ? '' : String(v));
    }
  }
  append(el, children);
  return el;
}

const SVG_NS = 'http://www.w3.org/2000/svg';
export function svg(tag, attrs, ...children) {
  const el = document.createElementNS(SVG_NS, tag);
  if (attrs) for (const [k, v] of Object.entries(attrs)) if (v != null) el.setAttribute(k, String(v));
  append(el, children);
  return el;
}

let uid = 0;
export const nextId = (prefix = 'wt') => `${prefix}-${++uid}`;

// ---------- icons (24×24 stroke glyphs) ----------
const ICONS = {
  dashboard: 'M4 13h6V4H4zM14 20h6V11h-6zM4 20h6v-3H4zM14 7h6V4h-6z',
  config: 'M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0M14 4v4M8 10v4M16 16v4',
  logs: 'M5 4h14v16H5zM8 8h8M8 12h8M8 16h5',
  updates: 'M20 12a8 8 0 1 1-2.3-5.7M20 4v5h-5',
  account: 'M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM4 21a8 8 0 0 1 16 0',
  signout: 'M15 4h4v16h-4M10 8l-4 4 4 4M6 12h10',
  critical: 'M12 3l9.5 17h-19zM12 10v4M12 17.2v.1',
  warning: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 7.5v6M12 16.5v.1',
  ok: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM8 12.2l2.8 2.8L16 9.5',
  info: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 11v6M12 7.5v.1',
  restart: 'M4 12a8 8 0 0 1 13.7-5.7L20 8.5M20 4v4.5h-4.5M20 12a8 8 0 0 1-13.7 5.7L4 15.5M4 20v-4.5h4.5',
  search: 'M10.5 4a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13zM15.5 15.5 20 20',
  close: 'M6 6l12 12M18 6 6 18',
  plus: 'M12 5v14M5 12h14',
  check: 'M5 12.5l4.5 4.5L19 7.5',
  lock: 'M6 11h12v9H6zM8.5 11V8a3.5 3.5 0 0 1 7 0v3',
  undo: 'M9 5 4 10l5 5M4 10h10a5 5 0 0 1 0 10h-4',
  refresh: 'M20 12a8 8 0 1 1-2.3-5.7M20 4v5h-5',
  mail: 'M3.5 6h17v12h-17zM4 7l8 6 8-6',
  eye: 'M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12zM12 9.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5z',
  list: 'M9 6h11M9 12h11M9 18h11M4.5 6h.1M4.5 12h.1M4.5 18h.1',
  stopped: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM9 9h6v6H9z',
  sun: 'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8zM12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4',
  moon: 'M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z',
  monitor: 'M3 4h18v12H3zM8 20h8M12 16v4',
  chevron: 'M6 15l6-6 6 6',
  copy: 'M9 9h11v11H9zM5 15H4V4h11v1',
  download: 'M12 4v11M7 10.5l5 5 5-5M5 20h14',
  pause: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM10 8.5v7M14 8.5v7',
  bell: 'M6 16.5V11a6 6 0 0 1 12 0v5.5l1.5 2h-15zM10 20.5h4',
};

export function icon(name, cls = '') {
  return svg('svg', { class: `icon ${cls}`.trim(), viewBox: '0 0 24 24', 'aria-hidden': 'true', focusable: 'false' },
    svg('path', { d: ICONS[name] || ICONS.info }));
}

/** Severity/status → `■ LABEL` (glyph + visible word), so state is never conveyed by colour alone. */
export function statusTag(kind, label) {
  const k = ['critical', 'warning', 'ok', 'info', 'stopped', 'stale'].includes(kind) ? kind : 'info';
  return h('span', { class: `tag tag-${k}` }, h('span', { class: 'tag-glyph', 'aria-hidden': 'true' }, '■'), h('span', null, label));
}

/** The brand's `/ SECTION` tag. */
export function sectionTag(text, cls = '') {
  return h('p', { class: `section-tag ${cls}`.trim() }, `/ ${text}`);
}

// ---------- formatting ----------
const nf1 = new Intl.NumberFormat(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
const nf0 = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });

export const isNum = (v) => typeof v === 'number' && Number.isFinite(v);
export const fmtPct = (v) => (isNum(v) ? `${nf1.format(v)}%` : '–');
export const fmt1 = (v) => (isNum(v) ? nf1.format(v) : '–');
export const fmt0 = (v) => (isNum(v) ? nf0.format(v) : '–');
export const fmtGb = (v) => (isNum(v) ? `${nf1.format(v)} GB` : '–');
export const fmtMbAsGb = (mb) => (isNum(mb) ? `${nf1.format(mb / 1024)} GB` : '–');

const dtf = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const tf = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' });
const tfs = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });

const toDate = (iso) => { const d = iso ? new Date(iso) : null; return d && !Number.isNaN(d.getTime()) ? d : null; };
export const fmtDateTime = (iso) => { const d = toDate(iso); return d ? dtf.format(d) : '–'; };
export const fmtTime = (iso, seconds = false) => { const d = toDate(iso); return d ? (seconds ? tfs : tf).format(d) : '–'; };

const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
export function fmtRelative(iso) {
  const d = toDate(iso);
  if (!d) return '';
  const s = Math.round((d.getTime() - Date.now()) / 1000);
  const a = Math.abs(s);
  if (a < 45) return rtf.format(Math.round(s), 'second');
  if (a < 2700) return rtf.format(Math.round(s / 60), 'minute');
  if (a < 86400) return rtf.format(Math.round(s / 3600), 'hour');
  return rtf.format(Math.round(s / 86400), 'day');
}

export function fmtDuration(seconds) {
  if (!isNum(seconds) || seconds < 0) return '–';
  const d = Math.floor(seconds / 86400);
  const hrs = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  if (d) return `${d} d ${hrs} h`;
  if (hrs) return `${hrs} h ${m} min`;
  if (m) return `${m} min`;
  return `${Math.floor(seconds)} s`;
}

/** "CpuSpikeDetected" → "CPU spike detected" */
export function humanize(type) {
  if (!type) return '';
  const words = String(type).replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2').split(/[\s_]+/);
  const ACR = { cpu: 'CPU', ram: 'RAM', iis: 'IIS', smtp: 'SMTP', nic: 'NIC', io: 'I/O' };
  return words.map((w, i) => ACR[w.toLowerCase()] ?? (i === 0 ? w[0].toUpperCase() + w.slice(1).toLowerCase() : w.toLowerCase())).join(' ');
}

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export function plural(n, one, many = `${one}s`) {
  return `${n} ${n === 1 ? one : many}`;
}
