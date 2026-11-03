// One editor per schema field type (see API.md table). Editors are dumb: they render a
// control, parse what the user typed, validate it, and report (value, error, display)
// upwards. The config view owns drafts and decides what counts as a change.
import { h, icon, nextId } from '../dom.js';
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
