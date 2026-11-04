// Maintenance window: the "Start maintenance" dialog, ending a window, and the dashboard banner.
// POST /api/actions/maintenance { minutes, reason } saves Maintenance:Until/Reason (operator
// settings); POST /api/actions/maintenance/stop resets them. A window set by the portal stays
// active after "stop" and the response message says so.
import { h, icon, nextId } from '../dom.js';
import { api } from '../api.js';
import { modal, busy, toast } from '../ui.js';

export const MAX_MINUTES = 7 * 24 * 60;
export const MAX_REASON = 500;
/** The settings the maintenance actions write (unsaved edits to these are superseded). */
export const MAINTENANCE_KEYS = ['Maintenance:Until', 'Maintenance:Reason'];

const PRESETS = [
  { id: '30', label: '30 min', minutes: 30 },
  { id: '60', label: '1 h', minutes: 60 },
  { id: '120', label: '2 h', minutes: 120 },
  { id: '240', label: '4 h', minutes: 240 },
  { id: 'custom', label: 'Custom', minutes: null },
];
const UNITS = [
  { id: 'min', label: 'minutes', factor: 1 },
  { id: 'h', label: 'hours', factor: 60 },
  { id: 'd', label: 'days', factor: 1440 },
];

/** ISO timestamp → epoch ms. Values without an offset are UTC, as on the server. */
export function parseUntil(value) {
  const s = typeof value === 'string' ? value.trim() : '';
  if (!s) return NaN;
  const zoned = /(?:Z|[+-]\d\d:?\d\d)$/i.test(s) || !/T\d/.test(s) ? s : `${s}Z`;
  return Date.parse(zoned);
}

const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
const tf = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' });
const dtf = new Intl.DateTimeFormat(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

/** "18:00" today, "Tue 4 Nov, 02:00" on another day (browser local time). */
export function fmtUntil(ms) {
  if (!Number.isFinite(ms)) return '–';
  const d = new Date(ms);
  return sameDay(d, new Date()) ? tf.format(d) : dtf.format(d);
}

/** "1 h 30 min", "2 d 4 h" for a duration in minutes. */
export function fmtMinutes(total) {
  const d = Math.floor(total / 1440);
  const hrs = Math.floor((total % 1440) / 60);
  const m = total % 60;
  return [d ? `${d} d` : '', hrs ? `${hrs} h` : '', m ? `${m} min` : ''].filter(Boolean).join(' ') || '0 min';
}

const isActive = (m) => !!(m && m.active);

/**
 * Opens the "Start maintenance" dialog. Resolves with the server's { active, until, reason }
 * once a window was saved, or null when cancelled. `current` (optional) pre-fills the reason
 * and switches the wording to "change" for a window that is already running.
 */
export function startMaintenanceDialog({ current = null } = {}) {
  return new Promise((resolve) => {
    const changing = isActive(current);
    const titleId = nextId('maint-title');
    const textId = nextId('maint-text');
    const dlg = modal({ titleId, describedBy: textId, className: 'dialog maint-dialog', onDismiss: () => settle(null) });
    const settle = dlg.settleWith(resolve);
    const group = nextId('maint-dur');

    let selected = '60';
    const radios = PRESETS.map((p) => {
      const id = `${group}-${p.id}`;
      const input = h('input', { type: 'radio', class: 'seg-input', name: group, id, value: p.id, checked: p.id === selected });
      input.addEventListener('change', () => { if (input.checked) { selected = p.id; sync(); } });
      return h('span', { class: 'seg-item' }, input, h('label', { class: 'seg-label', for: id }, p.label));
    });

    const customId = nextId('maint-custom');
    const customHelpId = `${customId}-help`;
    const customNum = h('input', {
      class: 'input input-num', type: 'number', id: customId, min: '1', step: '1', inputmode: 'numeric',
      value: '90', 'aria-describedby': customHelpId,
    });
    const unitSel = h('select', { class: 'input maint-unit', 'aria-label': 'Unit' },
      UNITS.map((u) => h('option', { value: u.id, selected: u.id === 'min' }, u.label)));
    const customErrId = `${customId}-err`;
    const customErr = h('p', { id: customErrId, class: 'field-error', hidden: true });
    customNum.setAttribute('aria-describedby', `${customHelpId} ${customErrId}`);
    const customRow = h('div', { class: 'maint-custom', hidden: true },
      h('label', { class: 'field-label', for: customId }, 'Custom duration'),
      h('div', { class: 'input-group' }, customNum, unitSel),
      h('p', { id: customHelpId, class: 'field-help' }, 'Up to 7 days.'),
      customErr);
    customNum.addEventListener('input', sync);
    unitSel.addEventListener('change', sync);

    const reasonId = nextId('maint-reason');
    const reasonCount = h('span', { class: 'muted small' });
    const reason = h('input', {
      class: 'input', type: 'text', id: reasonId, maxlength: String(MAX_REASON), autocomplete: 'off',
      placeholder: 'Monthly patching', value: current?.reason || '',
      'aria-describedby': `${reasonId}-help`,
    });
    reason.addEventListener('input', sync);

    const endsAt = h('p', { class: 'maint-ends' });
    const error = h('p', { class: 'field-error', role: 'alert', hidden: true });
    const cancelBtn = h('button', { class: 'btn', type: 'button', onclick: () => settle(null) }, 'Cancel');
    const startBtn = h('button', { class: 'btn btn-primary', type: 'submit' }, changing ? 'Set new end time' : 'Start maintenance');

    function minutes() {
      const p = PRESETS.find((x) => x.id === selected);
      if (p?.minutes) return p.minutes;
      const n = Number(customNum.value);
      const unit = UNITS.find((u) => u.id === unitSel.value) || UNITS[0];
      if (!customNum.value.trim() || !Number.isFinite(n) || n <= 0) return NaN;
      return Math.round(n * unit.factor);
    }

    function validate() {
      const m = minutes();
      if (!Number.isFinite(m) || m < 1) return 'Enter a duration of at least 1 minute.';
      if (m > MAX_MINUTES) return 'Maintenance can last at most 7 days (10,080 minutes).';
      return null;
    }

    function sync() {
      customRow.hidden = selected !== 'custom';
      const msg = selected === 'custom' ? validate() : null;
      if (msg) customNum.setAttribute('aria-invalid', 'true'); else customNum.removeAttribute('aria-invalid');
      customErr.replaceChildren(...(msg ? [icon('critical'), h('span', null, msg)] : []));
      customErr.hidden = !msg;
      const m = minutes();
      endsAt.replaceChildren(...(Number.isFinite(m) && !msg
        ? [h('span', { class: 'maint-ends-label' }, 'Ends'), ` ${fmtUntil(Date.now() + m * 60000)}`, h('span', { class: 'muted' }, ` (${fmtMinutes(m)} from now)`)]
        : [h('span', { class: 'maint-ends-label' }, 'Ends'), h('span', { class: 'muted' }, ' –')]));
      reasonCount.textContent = reason.value.length > MAX_REASON - 100 ? `${reason.value.length}/${MAX_REASON}` : '';
      error.hidden = true;
    }

    const form = h('form', { class: 'dialog-form', novalidate: true },
      h('h2', { id: titleId, class: 'dialog-title' }, changing ? 'Change maintenance window' : 'Start maintenance'),
      h('p', { id: textId, class: 'dialog-text' },
        'Incidents are still recorded, but notifications are held back and services and application pools are not restarted automatically. Alerts still open when the window ends are sent then.'),
      h('fieldset', { class: 'maint-fieldset' },
        h('legend', { class: 'field-label' }, changing ? 'New duration, from now' : 'Duration'),
        h('div', { class: 'segmented' }, radios)),
      customRow,
      endsAt,
      h('div', { class: 'maint-reason' },
        h('div', { class: 'field-label-row' }, h('label', { class: 'field-label', for: reasonId }, 'Reason'), h('span', { class: 'muted small' }, 'Optional'), reasonCount),
        reason,
        h('p', { id: `${reasonId}-help`, class: 'field-help' }, 'Shown on the dashboard and in the portal.')),
      error,
      h('div', { class: 'dialog-actions' }, cancelBtn, startBtn));

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const msg = validate();
      if (msg) {
        customNum.focus(); // only a custom duration can be invalid; its message is next to it
        return;
      }
      await busy(startBtn, changing ? 'Saving…' : 'Starting…', async () => {
        cancelBtn.disabled = true;
        try {
          const r = await api.startMaintenance(minutes(), reason.value.trim());
          settle(r?.maintenance || { active: true, until: null, reason: reason.value.trim() || null });
        } catch (err) {
          if (err.status === 401) { settle(null); return; }
          error.replaceChildren(icon('critical'), h('span', null, err.message));
          error.hidden = false;
        } finally {
          cancelBtn.disabled = false;
        }
      });
    });

    dlg.append(form);
    document.body.append(dlg);
    sync();
    dlg.showModal();
    radios[PRESETS.findIndex((p) => p.id === selected)].querySelector('input').focus();
  });
}

/** Start (or change) a window from a button; toasts the outcome. Resolves true when saved. */
export async function startMaintenance({ current = null } = {}) {
  const m = await startMaintenanceDialog({ current });
  if (!m) return false;
  const until = parseUntil(m.until);
  toast(Number.isFinite(until)
    ? `Maintenance until ${fmtUntil(until)}. Notifications and automatic restarts are paused.`
    : 'Maintenance started.');
  return true;
}

/** POST /api/actions/maintenance/stop from a button. Resolves with the response, or null on error. */
export async function endMaintenance(btn) {
  return busy(btn, 'Ending…', async () => {
    try {
      const r = await api.stopMaintenance();
      if (r?.maintenance?.active) toast(r.message || 'The maintenance window is still active.', { kind: 'info', timeout: 9000 });
      else toast(r?.message || 'Maintenance ended.');
      return r;
    } catch (e) {
      if (e.status !== 401) toast(e.message, { kind: 'error' });
      return null;
    }
  });
}

/**
 * The prominent dashboard banner while a window is active. onChanged runs after the window was
 * changed or ended (the caller refreshes its status).
 */
export function maintenanceBanner(m, { onChanged } = {}) {
  const until = parseUntil(m.until);
  const endBtn = h('button', { class: 'btn btn-sm btn-maint', type: 'button' }, 'End maintenance');
  const changeBtn = h('button', { class: 'btn btn-sm btn-quiet', type: 'button' }, 'Change…');
  endBtn.addEventListener('click', async () => {
    const r = await endMaintenance(endBtn);
    if (r) onChanged?.(r.maintenance);
  });
  changeBtn.addEventListener('click', async () => {
    if (await startMaintenance({ current: m })) onChanged?.();
  });
  return h('section', { class: 'maint-banner', 'aria-labelledby': 'maint-banner-title' },
    h('p', { class: 'maint-banner-tag' }, h('span', { 'aria-hidden': 'true' }, '■'), 'Maintenance'),
    h('div', { class: 'maint-banner-body' },
      h('h2', { id: 'maint-banner-title', class: 'maint-banner-title' },
        Number.isFinite(until) ? `Maintenance until ${fmtUntil(until)}` : 'Maintenance is active'),
      h('p', { class: 'maint-banner-text' },
        m.reason ? 'Notifications and automatic restarts are paused: ' : 'Notifications and automatic restarts are paused.',
        m.reason ? h('span', { class: 'maint-banner-reason' }, m.reason) : null)),
    h('div', { class: 'maint-banner-actions' }, changeBtn, endBtn));
}
