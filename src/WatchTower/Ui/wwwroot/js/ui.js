// Shared UI pieces: toasts, confirm dialog, notices, and the "restart WatchTower" flow.
import { h, icon, sleep } from './dom.js';
import { api } from './api.js';

// ---------- toasts ----------
export function toast(message, { kind = 'ok', timeout } = {}) {
  const host = document.getElementById('toasts');
  if (!host) return;
  const isError = kind === 'error';
  const el = h('div', { class: `toast toast-${kind}`, role: isError ? 'alert' : 'status' },
    h('span', { class: 'toast-glyph', 'aria-hidden': 'true' }, '■'),
    h('p', null, message),
    h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Dismiss notification', onclick: () => dismiss() }, icon('close')));
  let timer = 0;
  const dismiss = () => { clearTimeout(timer); el.classList.add('leaving'); setTimeout(() => el.remove(), 180); };
  host.append(el);
  const ms = timeout ?? (isError ? 9000 : 4500);
  if (ms > 0) timer = setTimeout(dismiss, ms);
  el.addEventListener('mouseenter', () => clearTimeout(timer));
  el.addEventListener('mouseleave', () => { if (ms > 0) timer = setTimeout(dismiss, ms); });
  return dismiss;
}

// ---------- notices (inline banners) ----------
/** kind: critical | warning | info | ok. items: string or string[]; actions: nodes. */
export function notice(kind, title, items, actions, { live = false } = {}) {
  const list = Array.isArray(items) ? items : items ? [items] : [];
  return h('section', { class: `notice notice-${kind}`, role: live ? (kind === 'critical' ? 'alert' : 'status') : null },
    h('span', { class: 'notice-icon', 'aria-hidden': 'true' }, '■'),
    h('div', { class: 'notice-body' },
      title ? h('p', { class: 'notice-title' }, title) : null,
      list.length === 1 ? h('p', null, list[0]) : list.length ? h('ul', null, list.map((t) => h('li', null, t))) : null),
    actions ? h('div', { class: 'notice-actions' }, actions) : null);
}

// ---------- modal dialogs ----------
/**
 * A native <dialog> that is removed once settled. Esc (the "cancel" event) and an
 * unexpected close both call onDismiss. dlg.settleWith(resolve) returns a once-only
 * settle(value) that closes + removes the dialog and resolves the promise.
 */
export function modal({ titleId, describedBy, className = 'dialog dialog-sm', onDismiss }) {
  const dlg = h('dialog', { class: className, 'aria-labelledby': titleId, 'aria-describedby': describedBy || null });
  let settled = false;
  const opener = document.activeElement;
  dlg.settleWith = (resolve) => (value) => {
    if (settled) return;
    settled = true;
    if (dlg.open) dlg.close();
    dlg.remove();
    if (opener && opener.isConnected && typeof opener.focus === 'function') opener.focus();
    resolve(value);
  };
  dlg.addEventListener('cancel', (e) => { e.preventDefault(); onDismiss?.(); });
  dlg.addEventListener('close', () => { if (!settled) onDismiss?.(); });
  return dlg;
}

// ---------- confirm dialog ----------
export function confirmDialog({ title, message, confirmLabel = 'Continue', cancelLabel = 'Cancel', danger = false }) {
  return new Promise((resolve) => {
    const titleId = 'confirm-title';
    const textId = 'confirm-text';
    const dlg = modal({ titleId, describedBy: textId, onDismiss: () => settle(false) });
    // Settle from the button handlers directly rather than relying only on the dialog's
    // "close" event, which some embedded/background Chromium tabs deliver late or never.
    const settle = dlg.settleWith(resolve);
    const cancel = h('button', { class: 'btn', type: 'button', onclick: () => settle(false) }, cancelLabel);
    dlg.append(h('div', { class: 'dialog-form' },
      h('h2', { id: titleId, class: 'dialog-title' }, title),
      h('p', { id: textId, class: 'dialog-text' }, message),
      h('div', { class: 'dialog-actions' },
        cancel,
        h('button', { class: danger ? 'btn btn-danger' : 'btn btn-primary', type: 'button', onclick: () => settle(true) }, confirmLabel))));
    document.body.append(dlg);
    dlg.showModal();
    cancel.focus();
  });
}

// ---------- restart flow ----------
let restarting = false;

/** POST /api/actions/restart, then wait for the service to go away and come back, then reload. */
export async function restartWatchTower() {
  if (restarting) return;
  const ok = await confirmDialog({
    title: 'Restart WatchTower?',
    message: 'Monitoring pauses for a few seconds while the service restarts. This console reconnects on its own.',
    confirmLabel: 'Restart now',
  });
  if (!ok) return;

  restarting = true;
  try {
    await api.restart();
  } catch (e) {
    restarting = false;
    toast(e.message, { kind: 'error' });
    return;
  }

  const status = h('p', { class: 'dialog-text', role: 'status' }, 'Waiting for the service to stop…');
  const dlg = h('dialog', { class: 'dialog dialog-sm restart-dialog', 'aria-labelledby': 'restart-title' },
    h('div', { class: 'dialog-form' },
      h('div', { class: 'spinner', 'aria-hidden': 'true' }),
      h('h2', { id: 'restart-title', class: 'dialog-title' }, 'Restarting WatchTower'),
      status));
  dlg.addEventListener('cancel', (e) => e.preventDefault()); // Esc must not hide the progress
  document.body.append(dlg);
  dlg.showModal();

  const alive = async () => {
    try {
      const r = await fetch('/api/auth/state', { cache: 'no-store', credentials: 'same-origin' });
      return r.ok;
    } catch { return false; }
  };

  // Phase 1: wait (up to 15 s) for the old process to stop answering.
  const downBy = Date.now() + 15000;
  while (Date.now() < downBy && (await alive())) await sleep(500);

  // Phase 2: wait for the new process to answer.
  status.textContent = 'Waiting for the service to come back…';
  const upBy = Date.now() + 120000;
  while (Date.now() < upBy) {
    if (await alive()) {
      status.textContent = 'Back online. Reloading…';
      location.reload();
      return;
    }
    await sleep(1500);
  }

  restarting = false;
  dlg.querySelector('.spinner')?.remove();
  status.textContent = 'WatchTower has not come back after two minutes. Check the WatchTower service in Services (services.msc) and the log files in the data directory.';
  dlg.querySelector('.dialog-form').append(h('div', { class: 'dialog-actions' },
    h('button', { class: 'btn', type: 'button', onclick: () => { dlg.close(); dlg.remove(); } }, 'Close'),
    h('button', { class: 'btn btn-primary', type: 'button', onclick: () => location.reload() }, 'Reload page')));
}

export function restartButton(label = 'Restart now') {
  return h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => restartWatchTower() }, icon('restart'), label);
}

/** Wrap a button's async action: disables it and shows a busy label while running. */
export async function busy(button, busyLabel, fn) {
  const prev = [...button.childNodes];
  button.disabled = true;
  button.setAttribute('aria-busy', 'true');
  if (busyLabel) button.replaceChildren(h('span', { class: 'spinner spinner-sm', 'aria-hidden': 'true' }), busyLabel);
  try {
    return await fn();
  } finally {
    button.disabled = false;
    button.removeAttribute('aria-busy');
    button.replaceChildren(...prev);
  }
}

// ---------- clipboard ----------
/**
 * Copy text. navigator.clipboard needs a secure context, and the console is often plain
 * http on a LAN address, so fall back to a hidden textarea + execCommand('copy').
 */
export async function copyText(text) {
  if (navigator.clipboard && window.isSecureContext) {
    try { await navigator.clipboard.writeText(text); return true; } catch { /* fall through */ }
  }
  const ta = h('textarea', { class: 'clipboard-proxy', readonly: true, 'aria-hidden': 'true', tabindex: '-1' });
  ta.value = text;
  const prev = document.activeElement;
  document.body.append(ta);
  ta.select();
  ta.setSelectionRange(0, text.length);
  let ok = false;
  try { ok = document.execCommand('copy'); } catch { ok = false; }
  ta.remove();
  if (prev && typeof prev.focus === 'function') prev.focus();
  return ok;
}

/** Small outline button that copies text and confirms in place. */
export function copyButton(getText, { label = 'Copy', what = 'Text' } = {}) {
  const text = h('span', null, label);
  const btn = h('button', { class: 'btn btn-xs', type: 'button', 'aria-label': `Copy ${what.toLowerCase()}` }, icon('copy'), text);
  let t = 0;
  btn.addEventListener('click', async () => {
    const ok = await copyText(getText());
    clearTimeout(t);
    text.textContent = ok ? 'Copied' : label;
    if (ok) t = setTimeout(() => { text.textContent = label; }, 1800);
    else toast(`Could not copy. Select the address and press Ctrl+C.`, { kind: 'error' });
    if (ok) toast(`${what} copied.`, { kind: 'info', timeout: 2500 });
  });
  return btn;
}
