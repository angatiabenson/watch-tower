// Logs: tail of the current log file from GET /api/logs?lines=N.
import { h, icon, nextId, fmtTime } from '../dom.js';
import { api } from '../api.js';
import { notice } from '../ui.js';

const LEVEL_RE = /\[(VRB|DBG|INF|WRN|ERR|FTL)\]/;
const LEVEL_CLASS = { VRB: 'lv-dbg', DBG: 'lv-dbg', INF: 'lv-inf', WRN: 'lv-wrn', ERR: 'lv-err', FTL: 'lv-err' };
const PREF_KEY = 'wt.logs.lines';

export function mount(root) {
  let destroyed = false;
  let lines = [];
  const linesId = nextId('ln');
  const filterId = nextId('lf');
  let saved = 300;
  try { saved = Number(localStorage.getItem(PREF_KEY)) || 300; } catch { /* storage disabled */ }

  const sel = h('select', { id: linesId, class: 'input select select-sm' },
    [100, 300, 1000].map((n) => h('option', { value: String(n), selected: n === saved }, `Last ${n} lines`)));
  const filter = h('input', { id: filterId, type: 'search', class: 'input input-sm', placeholder: 'Filter lines', autocomplete: 'off', spellcheck: 'false' });
  const problemsOnly = h('input', { type: 'checkbox', class: 'check' });
  const refreshBtn = h('button', { class: 'btn btn-sm', type: 'button' }, icon('refresh'), 'Refresh');
  const meta = h('p', { class: 'muted small log-meta', 'aria-live': 'polite' });
  const errSlot = h('div');
  const pre = h('pre', { class: 'log', tabindex: '0', role: 'log', 'aria-label': 'Log lines' });

  root.append(
    h('div', { class: 'log-toolbar' },
      h('div', { class: 'tool' }, h('label', { for: linesId, class: 'sr-only' }, 'Number of lines'), sel),
      h('div', { class: 'tool tool-grow' }, h('label', { for: filterId, class: 'sr-only' }, 'Filter lines'),
        h('div', { class: 'input-icon' }, icon('search'), filter)),
      h('label', { class: 'tool check-label' }, problemsOnly, 'Warnings and errors only'),
      refreshBtn),
    meta, errSlot, pre);

  const paint = () => {
    const q = filter.value.trim().toLowerCase();
    const onlyProblems = problemsOnly.checked;
    const frag = document.createDocumentFragment();
    let shown = 0;
    let level = 'lv-inf';
    for (const line of lines) {
      const m = LEVEL_RE.exec(line);
      // Continuation lines (stack traces) inherit the level of the entry above.
      if (m) level = LEVEL_CLASS[m[1]] || 'lv-inf';
      if (onlyProblems && level !== 'lv-wrn' && level !== 'lv-err') continue;
      if (q && !line.toLowerCase().includes(q)) continue;
      frag.append(h('span', { class: `log-line ${level}` }, line || ' '), '\n');
      shown++;
    }
    if (!shown) frag.append(h('span', { class: 'log-empty' }, lines.length ? 'No lines match the filter.' : 'The log file is empty.'));
    pre.replaceChildren(frag);
    pre.scrollTop = pre.scrollHeight;
    return shown;
  };

  const describe = (file, shown) => {
    meta.textContent = `${file || 'Current log'}: showing ${shown} of ${lines.length} lines.`;
  };
  let file = '';

  async function load() {
    refreshBtn.disabled = true;
    try {
      const r = await api.logs(Number(sel.value));
      if (destroyed) return;
      lines = Array.isArray(r?.lines) ? r.lines : [];
      file = r?.file || '';
      errSlot.replaceChildren();
      describe(file, paint());
      meta.append(` Loaded at ${fmtTime(new Date().toISOString(), true)}.`);
    } catch (e) {
      if (destroyed || e.status === 401) return;
      errSlot.replaceChildren(notice('critical', 'Logs could not be loaded', e.message, null, { live: true }));
    } finally {
      refreshBtn.disabled = false;
    }
  }

  const repaint = () => describe(file, paint());
  sel.addEventListener('change', () => {
    try { localStorage.setItem(PREF_KEY, sel.value); } catch { /* storage disabled */ }
    load();
  });
  refreshBtn.addEventListener('click', load);
  let t = 0;
  filter.addEventListener('input', () => { clearTimeout(t); t = setTimeout(repaint, 120); });
  problemsOnly.addEventListener('change', repaint);

  load();
  return { unmount() { destroyed = true; clearTimeout(t); } };
}
