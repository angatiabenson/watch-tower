// One editor per schema field type (see API.md table). Editors are dumb: they render a
// control, parse what the user typed, validate it, and report (value, error, display)
// upwards. The config view owns drafts and decides what counts as a change.
import { h, icon, nextId, plural } from '../dom.js';
import { api, cached } from '../api.js';
import { openPicker } from './picker.js';

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const TIME_RE = /^([01]\d|2[0-3]):[0-5]\d$/;

const loadTimeZones = cached(() => api.timeZones());

export function checkFormat(format, v) {
  if (!v || !format) return null;
  if (format === 'email' && !EMAIL_RE.test(v)) return 'Enter an email address like name@example.com.';
  if (format === 'url') return checkUrl(v);
  return null;
}

function checkUrl(v) {
  if (!v) return null;
  try {
    const u = new URL(v);
    if (u.protocol !== 'http:' && u.protocol !== 'https:') return 'Use an http:// or https:// address.';
    return null;
  } catch {
    return 'Enter a full address, starting with https://.';
  }
}

function rangeText(f) {
  const has = (n) => typeof n === 'number';
  if (has(f.min) && has(f.max)) return `between ${f.min} and ${f.max}`;
  if (has(f.min)) return `${f.min} or more`;
  if (has(f.max)) return `${f.max} or less`;
  return '';
}

/** Parse + validate a number input. */
function parseNumber(f, raw, required) {
  const t = raw.trim();
  if (t === '') return required ? { error: 'Enter a number.' } : { value: null };
  const n = Number(t);
  if (!Number.isFinite(n)) return { error: 'Enter a number.' };
  if (f.type === 'Int' && !Number.isInteger(n)) return { error: 'Enter a whole number.' };
  if ((typeof f.min === 'number' && n < f.min) || (typeof f.max === 'number' && n > f.max)) {
    return { error: `Must be ${rangeText(f)}.` };
  }
  return { value: n };
}

/**
 * @param {object} f schema field
 * @param {object} o { id, describedBy, value (current), original, hasValue, readOnly, pendingClear, onChange(value, error, display) }
 *   For secrets, value `undefined` = keep, string = replace, '' = clear.
 */
export function createEditor(f, o) {
  switch (f.type) {
    case 'Bool': return boolEditor(f, o);
    case 'Int':
    case 'Number': return numberEditor(f, o);
    case 'Enum': return enumEditor(f, o);
    case 'Secret': return secretEditor(f, o);
    case 'Time': return textEditor(f, o, { kind: 'time' });
    case 'Url': return textEditor(f, o, { kind: 'url' });
    case 'TimeZone': return timeZoneEditor(f, o);
    case 'List':
    case 'Services':
    case 'AppPools': return listEditor(f, o);
    case 'ObjectList': return objectListEditor(f, o);
    default: return textEditor(f, o, { kind: 'text' });
  }
}

function baseAttrs(o) {
  return { id: o.id, 'aria-describedby': o.describedBy || null, disabled: o.readOnly };
}

function textEditor(f, o, { kind }) {
  const attrs = {
    ...baseAttrs(o),
    class: 'input',
    type: kind === 'url' ? 'url' : f.format === 'email' ? 'email' : 'text',
    value: o.display ?? o.value ?? '',
    placeholder: f.placeholder || (kind === 'time' ? 'HH:mm, e.g. 07:30' : null),
    spellcheck: 'false',
    autocomplete: 'off',
    inputmode: kind === 'time' ? 'numeric' : null,
    maxlength: kind === 'time' ? 5 : null,
  };
  const input = h('input', attrs);
  if (kind === 'time') input.classList.add('input-time');
  const validate = (v) => {
    if (kind === 'time') {
      if (!v) return o.original ? 'Enter a time as HH:mm (24-hour).' : null;
      return TIME_RE.test(v) ? null : 'Enter a time as HH:mm (24-hour), e.g. 07:30.';
    }
    if (kind === 'url') return checkUrl(v);
    return checkFormat(f.format, v);
  };
  const emit = () => {
    let v = input.value.trim();
    if (kind === 'time' && /^\d:\d\d$/.test(v)) v = `0${v}`;
    o.onChange(v, validate(v), input.value);
  };
  input.addEventListener('input', emit);
  input.addEventListener('blur', () => { if (kind !== 'time' || !input.value) return; emit(); });
  return { el: input, focus: () => input.focus() };
}

function numberEditor(f, o) {
  const input = h('input', {
    ...baseAttrs(o),
    class: 'input input-num',
    type: 'number',
    inputmode: f.type === 'Int' ? 'numeric' : 'decimal',
    step: f.type === 'Int' ? '1' : 'any',
    min: f.min ?? null,
    max: f.max ?? null,
    value: o.display ?? (o.value ?? ''),
    placeholder: f.placeholder || null,
  });
  const range = rangeText(f);
  input.addEventListener('input', () => {
    // A number input reports '' while its text is not a valid number ("1e", "-").
    if (input.validity.badInput) { o.onChange(undefined, 'Enter a number.', input.value); return; }
    const r = parseNumber(f, input.value, o.original != null);
    o.onChange(r.value, r.error || null, input.value);
  });
  const el = h('div', { class: 'num-wrap' }, input, range ? h('span', { class: 'num-range muted small', 'aria-hidden': 'true' }, range) : null);
  return { el, focus: () => input.focus() };
}

function boolEditor(f, o) {
  const input = h('input', { ...baseAttrs(o), type: 'checkbox', role: 'switch', class: 'switch', checked: !!o.value });
  const state = h('span', { class: 'switch-state', 'aria-hidden': 'true' }, input.checked ? 'On' : 'Off');
  input.addEventListener('change', () => {
    state.textContent = input.checked ? 'On' : 'Off';
    o.onChange(input.checked, null);
  });
  return { el: h('div', { class: 'switch-wrap' }, input, state), focus: () => input.focus() };
}

function enumEditor(f, o) {
  const opts = [...(f.options || [])];
  const v = o.value ?? '';
  if (v && !opts.includes(v)) opts.unshift(v);
  const sel = h('select', { ...baseAttrs(o), class: 'input select' },
    !v ? h('option', { value: '', selected: true, disabled: true }, 'Choose…') : null,
    opts.map((x) => h('option', { value: x, selected: x === v }, x)));
  sel.addEventListener('change', () => o.onChange(sel.value, null));
  return { el: sel, focus: () => sel.focus() };
}

function secretEditor(f, o) {
  const input = h('input', {
    ...baseAttrs(o),
    class: 'input',
    type: 'password',
    autocomplete: 'new-password',
    spellcheck: 'false',
    value: typeof o.value === 'string' && o.value !== '' ? o.value : '',
    placeholder: o.hasValue ? 'Saved. Type a new value to replace it' : (f.placeholder || 'Not set'),
  });
  const reveal = h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Show what you typed', 'aria-pressed': 'false', disabled: o.readOnly }, icon('eye'));
  reveal.addEventListener('click', () => {
    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    reveal.setAttribute('aria-pressed', String(show));
  });
  const clearing = h('p', { class: 'secret-clearing', hidden: true }, 'The saved value will be removed when you save.');
  const clearBtn = h('button', { class: 'btn btn-sm btn-quiet', type: 'button', hidden: !o.hasValue || o.readOnly }, 'Clear saved value');

  const setClearing = (on) => {
    input.disabled = on || o.readOnly;
    reveal.disabled = on || o.readOnly;
    clearing.hidden = !on;
    clearBtn.textContent = on ? 'Keep saved value' : 'Clear saved value';
    if (on) input.value = '';
  };
  clearBtn.addEventListener('click', () => {
    const on = clearing.hidden;
    setClearing(on);
    o.onChange(on ? '' : undefined, null);
    if (!on) input.focus();
  });
  input.addEventListener('input', () => {
    const v = input.value;
    if (v === '') { o.onChange(undefined, null); return; }
    o.onChange(v, checkFormat(f.format, v.trim()));
  });
  if (o.value === '') setClearing(true);

  const el = h('div', { class: 'secret' },
    h('div', { class: 'input-group' }, input, reveal),
    h('div', { class: 'secret-foot' }, clearBtn, clearing));
  return { el, focus: () => input.focus() };
}

function timeZoneEditor(f, o) {
  let value = o.value || '';
  const label = h('span', { class: 'tz-label' }, value || 'Not set');
  const sub = h('span', { class: 'muted small tz-id' });
  const btn = h('button', {
    ...baseAttrs(o), class: 'btn tz-btn', type: 'button', 'aria-haspopup': 'dialog',
  }, h('span', { class: 'tz-text' }, label, sub), h('span', { class: 'tz-change' }, 'Change…'));

  const paint = (zones) => {
    const z = zones?.find((x) => x.id === value);
    label.textContent = z ? z.displayName : (value || 'Not set');
    sub.textContent = z ? z.id : '';
  };
  if (value) loadTimeZones().then(paint).catch(() => {});

  btn.addEventListener('click', async () => {
    const picked = await openPicker({
      title: 'Choose a time zone',
      searchLabel: 'Search by city, region or UTC offset',
      selected: value ? [value] : [],
      load: async () => {
        const zones = await loadTimeZones();
        return { items: zones.map((z) => ({ value: z.id, label: z.displayName, sub: z.id !== z.displayName ? z.id : '' })) };
      },
    });
    btn.focus();
    if (!picked || !picked.length) return;
    value = picked[0];
    loadTimeZones().then(paint).catch(() => paint(null));
    o.onChange(value, null);
  });
  return { el: btn, focus: () => btn.focus() };
}

function listEditor(f, o) {
  let items = Array.isArray(o.value) ? [...o.value] : [];
  const isServices = f.type === 'Services';
  const isPools = f.type === 'AppPools';
  const noun = isServices ? 'service' : isPools ? 'application pool' : f.format === 'email' ? 'address' : f.format === 'url' ? 'URL' : 'entry';

  const chips = h('ul', { class: 'chips', 'aria-label': `${f.label}: ${items.length} entries` });
  const addId = nextId('add');
  const add = h('input', {
    id: o.id, class: 'input', type: f.format === 'email' ? 'email' : 'text', disabled: o.readOnly,
    'aria-describedby': [o.describedBy, `${addId}-msg`].filter(Boolean).join(' '),
    placeholder: isServices ? 'Service name, e.g. W3SVC' : isPools ? 'Pool name, e.g. DefaultAppPool' : f.format === 'email' ? 'name@example.com' : f.format === 'url' ? 'https://' : `Add ${noun}`,
    autocomplete: 'off', spellcheck: 'false',
  });
  const addBtn = h('button', { class: 'btn btn-sm', type: 'button', disabled: o.readOnly }, icon('plus'), 'Add');
  const msg = h('p', { id: `${addId}-msg`, class: 'field-error-inline', hidden: true });

  const emit = () => o.onChange([...items], null);

  const paint = () => {
    chips.setAttribute('aria-label', `${f.label}: ${items.length} ${items.length === 1 ? 'entry' : 'entries'}`);
    if (!items.length) {
      chips.replaceChildren(h('li', { class: 'chips-empty muted' }, `None yet.`));
      return;
    }
    chips.replaceChildren(...items.map((v, i) => h('li', { class: 'chip' },
      h('span', { class: 'chip-text' }, v),
      o.readOnly ? null : h('button', {
        class: 'chip-x', type: 'button', 'aria-label': `Remove ${v}`,
        onclick: () => {
          items.splice(i, 1);
          paint(); emit();
          (chips.querySelectorAll('.chip-x')[Math.min(i, items.length - 1)] || add).focus();
        },
      }, icon('close')))));
  };

  const tryAdd = () => {
    const parts = add.value.split(/[,;\n]+/).map((s) => s.trim()).filter(Boolean);
    if (!parts.length) return;
    for (const p of parts) {
      const err = checkFormat(f.format, p);
      if (err) { showMsg(`“${p}”: ${err}`); return; }
    }
    let dup = null;
    for (const p of parts) {
      if (items.some((x) => x.toLowerCase() === p.toLowerCase())) dup = p;
      else items.push(p);
    }
    add.value = '';
    showMsg(dup ? `“${dup}” is already in the list.` : null);
    paint(); emit();
  };
  const showMsg = (m) => { msg.hidden = !m; msg.textContent = m || ''; };

  add.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); tryAdd(); }
    else if (e.key === 'Backspace' && !add.value && items.length) {
      chips.querySelector('.chip:last-child .chip-x')?.focus();
    }
  });
  add.addEventListener('input', () => showMsg(null));
  addBtn.addEventListener('click', () => { tryAdd(); add.focus(); });

  let browse = null;
  if (isServices || isPools) {
    browse = h('button', { class: 'btn btn-sm', type: 'button', disabled: o.readOnly, 'aria-haspopup': 'dialog' }, icon('list'), 'Browse…');
    browse.addEventListener('click', async () => {
      const picked = await openPicker({
        title: isServices ? 'Choose services to watch' : 'Choose application pools to watch',
        searchLabel: isServices ? 'Search by service or display name' : 'Search by pool name',
        multi: true,
        allowFreeText: true,
        freeTextNoun: isServices ? 'service name' : 'pool name',
        selected: items,
        load: isServices ? loadServices : loadPools,
      });
      browse.focus();
      if (!picked) return;
      items = picked;
      paint(); emit();
    });
  }

  paint();
  const el = h('div', { class: 'list-editor' },
    chips,
    h('div', { class: 'list-add' }, add, addBtn, browse),
    msg);
  return { el, focus: () => add.focus() };
}

// ------------------------------------------------------------ list of objects (endpoint checks)
// Mirrors EndpointCheck.TryCreate client-side; the server re-validates the whole list on save.

const TYPE_LABEL = { http: 'HTTP', tcp: 'TCP', certificate: 'TLS cert' };
const TARGET_HINT = {
  http: { placeholder: 'https://intranet.example.com/health', help: 'Full http:// or https:// URL. It is requested with GET.' },
  tcp: { placeholder: 'sql01.corp.local:1433', help: 'Host name or IP address and port.' },
  certificate: { placeholder: 'portal.example.com', help: 'Host name, host:port (port 443 when left out) or an https:// URL.' },
};

function checkHostPort(v, portRequired) {
  if (/[\s/?#@]/.test(v)) return false;
  let host = v;
  let port = '';
  if (v.startsWith('[')) {
    const close = v.indexOf(']');
    if (close < 2) return false;
    host = v.slice(1, close);
    const rest = v.slice(close + 1);
    if (rest && !rest.startsWith(':')) return false;
    port = rest.slice(1);
    if (!host.includes(':')) return false;
  } else {
    const colons = (v.match(/:/g) || []).length;
    if (colons > 1) return false; // bare IPv6 is ambiguous: use [fd00::1]:443
    if (colons === 1) [host, port] = v.split(':');
  }
  if (!host || /[[\]]/.test(host)) return false;
  if (!port) return !portRequired && !v.endsWith(':');
  return /^\d{1,5}$/.test(port) && Number(port) >= 1 && Number(port) <= 65535;
}

export function checkEndpointTarget(type, v) {
  if (!v) return null;
  const t = String(type || '').toLowerCase();
  if (t === 'http') return checkUrl(v) ? 'Enter a full http:// or https:// URL, e.g. https://intranet.example.com/health.' : null;
  if (t === 'tcp') {
    if (/^[a-z][a-z0-9+.-]*:\/\//i.test(v)) return 'Enter host:port without a scheme, e.g. sql01.corp.local:1433.';
    return checkHostPort(v, true) ? null : 'Enter host:port with a port from 1 to 65535, e.g. sql01.corp.local:1433. Put IPv6 addresses in brackets.';
  }
  if (t === 'certificate') {
    if (/^https:\/\//i.test(v)) { try { new URL(v); return null; } catch { /* fall through */ } }
    return checkHostPort(v, false) ? null : 'Enter a host, host:port or an https:// URL, e.g. portal.example.com:443.';
  }
  return null;
}

export function checkStatusRanges(v) {
  if (!v || !v.trim()) return null; // empty = 200-399
  const msg = 'Use status codes or ranges from 100 to 599, e.g. 200-399 or 200,204,301-302.';
  const parts = v.split(',').map((s) => s.trim()).filter(Boolean);
  if (!parts.length) return msg;
  for (const p of parts) {
    const b = p.split('-').map((s) => s.trim());
    if (b.length > 2 || !b.every((x) => /^\d+$/.test(x))) return msg;
    const from = Number(b[0]);
    const to = Number(b[b.length - 1]);
    if (from < 100 || to > 599 || from > to) return msg;
  }
  return null;
}

/**
 * Editable list of objects described by f.fields (e.g. Monitoring:Endpoints). Items are
 * collapsible rows; their properties reuse the scalar editors above.
 * Extra options: o.itemErrors { "<index>" | "<index>:<Prop>": message } (server errors);
 * onChange(items, error, display, touched) where touched is "<index>:<Prop>" or "*".
 */
function objectListEditor(f, o) {
  const subs = f.fields || [];
  const noun = f.itemNoun || 'entry';
  const uniqueKey = f.uniqueBy || null;
  const typeField = subs.find((s) => s.type === 'Enum') || null;
  const targetField = subs.find((s) => s.required && s.key !== uniqueKey) || null;
  const max = typeof f.max === 'number' ? f.max : Infinity;
  const disp = o.display || {};
  const fresh = (it) => Object.fromEntries(subs.map((s) => [s.key, it?.[s.key] ?? s.default ?? null]));
  const nameKey = (v) => String(v ?? '').trim().toLowerCase();

  let items = (Array.isArray(o.value) ? o.value : []).map(fresh);
  const originalNames = new Set((Array.isArray(o.original) ? o.original : []).map((it) => nameKey(it?.[uniqueKey])));
  const open = new Set(disp.open || []);
  const raw = new Map(Object.entries(disp.raw || {}));          // "i:Prop" → text typed into a box that did not parse
  const parseErr = new Map(Object.entries(disp.parseErr || {})); // "i:Prop" → that editor's own error
  const touched = new Set(disp.touched || []);                   // "i:Prop" → user has left the box (errors shown)
  const server = new Map(Object.entries(o.itemErrors || {}));
  let revealAll = !!disp.revealAll;
  for (const k of server.keys()) open.add(Number(k.split(':')[0]));

  const visible = (s, it) => !s.visibleWhen
    || s.visibleWhen.values.some((v) => String(v).toLowerCase() === String(it[s.visibleWhen.key] ?? '').toLowerCase());

  function clientError(i, s) {
    const ck = `${i}:${s.key}`;
    if (parseErr.has(ck)) return parseErr.get(ck);
    const it = items[i];
    if (!visible(s, it)) return null;
    const v = it[s.key];
    const empty = v == null || (typeof v === 'string' && !v.trim());
    if (empty) return s.required ? `${s.label} is required.` : null;
    if (s.type === 'String' && typeof s.max === 'number' && String(v).length > s.max) return `Use at most ${s.max} characters.`;
    if (s.key === uniqueKey) {
      const first = items.findIndex((x) => nameKey(x[uniqueKey]) === nameKey(v));
      if (first >= 0 && first < i) return `“${String(v).trim()}” is already used by ${noun} ${first + 1}. ${s.label}s must be unique.`;
    }
    if (s.format === 'endpoint-target') return checkEndpointTarget(typeField ? it[typeField.key] : null, String(v).trim());
    if (s.format === 'status-ranges') return checkStatusRanges(String(v));
    return checkFormat(s.format, String(v).trim());
  }
  const shownError = (i, s) => {
    const ck = `${i}:${s.key}`;
    const err = clientError(i, s);
    if (err && (revealAll || touched.has(ck) || parseErr.has(ck))) return err;
    return server.get(ck) || null;
  };
  const itemHasError = (i) => subs.some((s) => clientError(i, s)) || [...server.keys()].some((k) => k === String(i) || k.startsWith(`${i}:`));
  const itemShowsError = (i) => subs.some((s) => shownError(i, s)) || server.has(String(i));

  function summary() {
    const bad = items.filter((_, i) => subs.some((s) => clientError(i, s))).length;
    if (!bad) return null;
    return `${plural(bad, noun)} ${bad === 1 ? 'is' : 'are'} incomplete or invalid.`;
  }

  const emit = (what) => o.onChange(items.map((it) => ({ ...it })), summary(), {
    open: [...open], raw: Object.fromEntries(raw), parseErr: Object.fromEntries(parseErr), touched: [...touched], revealAll,
  }, what);

  // ---------------------------------------------------------------- DOM
  const groupLabel = o.labelledBy || null;
  const list = h('ul', { class: 'obj-items', 'aria-labelledby': groupLabel });
  const live = h('p', { class: 'sr-only', 'aria-live': 'polite' });
  const count = h('p', { class: 'obj-count' });
  const addBtn = h('button', { class: 'btn btn-sm', type: 'button', hidden: o.readOnly }, icon('plus'), `Add ${noun}`);
  const views = []; // per item: { li, toggle, remove, body, cells: Map(key → cell) }

  function announce(text) { live.textContent = ''; setTimeout(() => { live.textContent = text; }, 30); }

  function paintCount() {
    count.textContent = items.length ? plural(items.length, noun) : '';
    addBtn.disabled = o.readOnly || items.length >= max;
    addBtn.title = items.length >= max ? `At most ${max} ${noun}s.` : '';
  }

  function paintHead(i) {
    const v = views[i];
    const it = items[i];
    const name = String(it[uniqueKey] ?? '').trim();
    const type = typeField ? String(it[typeField.key] ?? '') : '';
    const target = targetField ? String(it[targetField.key] ?? '').trim() : '';
    const isNew = !originalNames.has(nameKey(name)) || !name;
    v.name.textContent = name || `New ${noun}`;
    v.name.classList.toggle('is-placeholder', !name);
    v.badge.textContent = TYPE_LABEL[type.toLowerCase()] || type;
    v.badge.hidden = !type;
    v.newBadge.hidden = !isNew || o.readOnly;
    v.sub.textContent = target || 'No target yet';
    const bad = itemShowsError(i);
    v.li.classList.toggle('is-invalid', bad);
    v.mark.replaceChildren(...(bad ? [icon('critical'), h('span', { class: 'sr-only' }, ' (needs fixing)')] : []));
    v.toggle.setAttribute('aria-expanded', String(open.has(i)));
    v.body.hidden = !open.has(i);
    v.remove?.setAttribute('aria-label', `Remove ${noun} ${name || i + 1}`);
    const itemMsg = server.get(String(i));
    v.itemErr.hidden = !itemMsg;
    v.itemErr.replaceChildren(...(itemMsg ? [icon('critical'), h('span', null, itemMsg)] : []));
  }

  function paintCells(i) {
    for (const [key, cell] of views[i].cells) {
      const s = subs.find((x) => x.key === key);
      const msg = shownError(i, s);
      cell.el.classList.toggle('is-invalid', !!msg);
      cell.err.hidden = !msg;
      cell.err.replaceChildren(...(msg ? [icon('critical'), h('span', null, msg)] : []));
      cell.el.querySelectorAll('input, select').forEach((c) => {
        if (msg) c.setAttribute('aria-invalid', 'true'); else c.removeAttribute('aria-invalid');
      });
    }
  }

  const paintAll = () => { items.forEach((_, i) => { paintHead(i); paintCells(i); }); paintCount(); };

  function buildCells(i) {
    const v = views[i];
    const it = items[i];
    v.cells = new Map();
    const type = typeField ? String(it[typeField.key] ?? '').toLowerCase() : '';
    const grid = subs.filter((s) => visible(s, it)).map((s) => {
      const ck = `${i}:${s.key}`;
      const cid = nextId('obj-f');
      const hint = s.format === 'endpoint-target' ? TARGET_HINT[type] : null;
      const help = hint?.help || s.help;
      const err = h('p', { id: `${cid}-err`, class: 'field-error', hidden: true });
      const ed = createEditor({ ...s, placeholder: hint?.placeholder || s.placeholder }, {
        id: cid,
        describedBy: [help ? `${cid}-help` : null, `${cid}-err`].filter(Boolean).join(' '),
        value: it[s.key],
        display: raw.get(ck),
        original: s.default ?? null, // non-null: an emptied number box is an error, not "unset"
        hasValue: true,
        readOnly: o.readOnly,
        onChange: (value, error, display) => onCell(i, s, value, error, display),
      });
      const span = s.format === 'endpoint-target' || s.format === 'url' ? 'span-all'
        : s.type === 'String' && !s.format ? 'span-2' : '';
      const el = h('div', { class: `obj-field ${span}`.trim(), dataset: { prop: s.key } },
        h('label', { class: 'obj-label', for: cid }, s.label),
        ed.el,
        help ? h('p', { id: `${cid}-help`, class: 'obj-help' }, help) : null,
        err);
      el.addEventListener('focusout', (e) => {
        if (el.contains(e.relatedTarget) || touched.has(ck)) return;
        touched.add(ck);
        paintCells(i); paintHead(i);
        emit(null);
      });
      v.cells.set(s.key, { el, err, focus: ed.focus });
      return el;
    });
    v.body.replaceChildren(...grid, v.itemErr);
    paintCells(i);
  }

  function onCell(i, s, value, error, display) {
    const ck = `${i}:${s.key}`;
    items[i][s.key] = value === undefined ? null : value;
    if (error) { parseErr.set(ck, error); if (display !== undefined) raw.set(ck, display); }
    else { parseErr.delete(ck); raw.delete(ck); }
    server.delete(ck);
    if (typeField && s.key === typeField.key) {
      // Different type → different properties and target format.
      buildCells(i);
      views[i].cells.get(s.key)?.focus();
    }
    // Uniqueness and target rules depend on other items/properties: repaint everything.
    paintAll();
    emit(ck);
  }

  function itemView(i) {
    const bodyId = nextId('obj');
    const name = h('span', { class: 'obj-name' });
    const badge = h('span', { class: 'badge obj-badge' });
    const newBadge = h('span', { class: 'badge badge-dirty' }, 'New');
    const sub = h('span', { class: 'obj-sub' });
    const mark = h('span', { class: 'obj-mark' });
    const toggle = h('button', { class: 'obj-toggle', type: 'button', 'aria-controls': bodyId },
      h('span', { class: 'obj-chevron', 'aria-hidden': 'true' }),
      h('span', { class: 'obj-title' }, name, badge, newBadge),
      sub, mark);
    const remove = o.readOnly ? null : h('button', { class: 'btn btn-sm btn-quiet obj-remove', type: 'button' }, icon('close'), h('span', { class: 'obj-remove-text' }, 'Remove'));
    const itemErr = h('p', { class: 'field-error obj-item-error span-all', role: 'status', hidden: true });
    const body = h('div', { class: 'obj-body', id: bodyId, role: 'group', 'aria-label': `${noun} ${i + 1}` });
    const li = h('li', { class: 'obj-item' }, h('div', { class: 'obj-head' }, toggle, remove), body);
    views[i] = { li, toggle, remove, body, name, badge, newBadge, sub, mark, itemErr, cells: new Map() };

    toggle.addEventListener('click', () => {
      if (open.has(i)) open.delete(i); else open.add(i);
      paintHead(i);
      emit(null);
    });
    remove?.addEventListener('click', () => removeItem(i));
    buildCells(i);
    paintHead(i);
    return li;
  }

  function renderList() {
    views.length = 0;
    list.replaceChildren(...(items.length
      ? items.map((_, i) => itemView(i))
      : [h('li', { class: 'obj-empty' }, o.readOnly ? `No ${noun}s.` : `No ${noun}s yet. Add one to watch a website, a port or a TLS certificate.`)]));
    paintCount();
  }

  /** Drops index i from a Map/Set keyed "i" or "i:Prop" and shifts later indices down. */
  function shiftKeys(coll, i) {
    const entries = coll instanceof Map ? [...coll.entries()] : [...coll].map((k) => [k, true]);
    coll.clear();
    for (const [k, val] of entries) {
      const [idx, ...rest] = String(k).split(':');
      const n = Number(idx);
      if (n === i) continue;
      const nk = n > i ? [n - 1, ...rest].join(':') : String(k);
      if (coll instanceof Map) coll.set(nk, val); else coll.add(typeof k === 'number' ? Number(nk) : nk);
    }
  }

  function removeItem(i) {
    const label = String(items[i][uniqueKey] ?? '').trim();
    items.splice(i, 1);
    for (const coll of [open, raw, parseErr, touched]) shiftKeys(coll, i);
    server.clear(); // indices changed; the next save re-validates
    renderList();
    emit('*');
    announce(`Removed ${noun} ${label || i + 1}. Undo change or Discard restores it until you save.`);
    (views[Math.min(i, items.length - 1)]?.toggle || addBtn).focus();
  }

  addBtn.addEventListener('click', () => {
    if (items.length >= max) return;
    items.push(fresh(null));
    const i = items.length - 1;
    open.add(i);
    if (items.length === 1) renderList();
    else { list.append(itemView(i)); paintCount(); }
    emit('*');
    announce(`Added ${noun} ${i + 1}.`);
    views[i].cells.get(uniqueKey || subs[0]?.key)?.focus();
  });

  renderList();
  const el = h('div', { class: 'obj-list', id: o.id, role: 'group', 'aria-labelledby': groupLabel, 'aria-describedby': o.describedBy || null },
    list, h('div', { class: 'obj-foot' }, addBtn, count), live);

  /** Focus a given entry property (from a server error key), else the first problem, else the list. */
  function focus(key) {
    const m = /^(\d+)(?::(\w+))?$/.exec(key || '');
    let target = m ? { i: Number(m[1]), prop: m[2] } : null;
    if (!target) {
      revealAll = true;
      const i = items.findIndex((_, x) => itemHasError(x));
      if (i >= 0) target = { i, prop: subs.find((s) => views[i].cells.has(s.key) && (clientError(i, s) || server.has(`${i}:${s.key}`)))?.key };
    }
    if (target && views[target.i]) {
      open.add(target.i);
      paintAll();
      const v = views[target.i];
      (v.cells.get(target.prop)?.focus || (() => v.toggle.focus()))();
      return;
    }
    (views[0]?.toggle || addBtn).focus();
  }

  return { el, focus };
}

async function loadServices() {
  const list = await api.services();
  return {
    items: (list || [])
      .slice()
      .sort((a, b) => (a.displayName || a.name).localeCompare(b.displayName || b.name))
      .map((s) => ({
        value: s.name,
        label: s.displayName || s.name,
        sub: s.displayName && s.displayName !== s.name ? s.name : '',
        meta: [s.status, s.startType].filter(Boolean).join(', '),
        metaKind: s.status === 'Running' ? 'ok' : 'none',
      })),
  };
}

async function loadPools() {
  const r = await api.appPools();
  const pools = r?.pools || [];
  return {
    note: r && r.iisInstalled === false
      ? 'IIS is not installed on this server, so there are no pools to list. You can still type pool names to watch.'
      : null,
    items: pools.map((p) => ({ value: p.name, meta: p.state, metaKind: p.state === 'Started' ? 'ok' : 'none' })),
  };
}
