// "Connect with an enrollment code" box at the top of the Visibility Portal section.
// POST /api/actions/enroll saves and enables the portal connection in one step; the host
// view (config.js) reloads the settings and raises the restart banner afterwards.
import { h, icon, nextId } from '../dom.js';
import { api } from '../api.js';
import { notice, busy, confirmDialog } from '../ui.js';

const PREFIX = 'WT1.';

/** Settings the server writes on enrollment (unsaved edits to these are superseded). */
export const ENROLL_KEYS = [
  'VisibilityPortal:Enabled',
  'VisibilityPortal:Protocol',
  'VisibilityPortal:BaseUrl',
  'VisibilityPortal:AgentId',
  'VisibilityPortal:AgentToken',
  'VisibilityPortal:Tls:RequireTls',
];

/** Pasted codes may be wrapped across lines; base64url never contains whitespace. */
const normalise = (text) => String(text || '').replace(/\s+/g, '');

/**
 * Decode the payload for client-side hints only: returns { url } (url may be null), or null
 * when the code is not decodable. The token is never read out, kept or shown.
 */
function decode(code) {
  try {
    let b64 = code.slice(PREFIX.length).replace(/-/g, '+').replace(/_/g, '/');
    b64 += '='.repeat((4 - (b64.length % 4)) % 4);
    const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
    const json = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
    if (!json || typeof json !== 'object') return null;
    return { url: typeof json.url === 'string' ? json.url.trim() : null };
  } catch {
    return null;
  }
}

/** What the input looks like, before anything is sent. The server has the final say. */
function inspect(code) {
  if (!code) return { ok: false };
  if (!code.startsWith(PREFIX)) {
    if (PREFIX.startsWith(code)) return { ok: false }; // still typing "WT1."
    return {
      ok: false,
      hint: /^https?:/i.test(code)
        ? 'That is a web address. Paste the enrollment code instead: it starts with WT1.'
        : 'An enrollment code starts with WT1. Copy the whole code from the portal\'s Add server dialog.',
    };
  }
  if (code.length === PREFIX.length) return { ok: false };
  const payload = decode(code);
  if (!payload) return { ok: true, hint: 'This code looks incomplete. Copy it again from the portal\'s Add server dialog.' };
  if (!payload.url) return { ok: true, hint: 'This code has no portal address. Copy it again from the portal\'s Add server dialog.' };
  return { ok: true, plainHttp: /^http:\/\//i.test(payload.url) };
}

/**
 * memo   kept by the host across re-renders: { code, done, error, errors, focusDone }
 * opts   { isDirty(): bool, labelOf(key): string, onEnrolled(response): Promise }
 */
export function enrollPanel(memo, { isDirty, labelOf, onEnrolled }) {
  const id = nextId('enroll');
  const headId = `${id}-h`;
  const helpId = `${id}-help`;
  const hintId = `${id}-hint`;
  const errId = `${id}-err`;

  const input = h('textarea', {
    id, class: 'input enroll-code', rows: '3',
    placeholder: 'WT1.eyJ2IjoxLCJ1cmwiOiJodHRwczovL…',
    spellcheck: 'false', autocomplete: 'off', autocapitalize: 'off', autocorrect: 'off',
    'aria-labelledby': headId,
    'aria-describedby': `${helpId} ${hintId} ${errId}`,
  });
  input.value = memo.code || '';

  const btn = h('button', { class: 'btn btn-primary', type: 'button' }, 'Connect');
  const hint = h('div', { id: hintId, class: 'enroll-hints', 'aria-live': 'polite' });
  const result = h('div', { id: errId, class: 'enroll-result', 'aria-live': 'polite' });
  const done = h('div', { class: 'enroll-done', tabindex: '-1' });
  let running = false;

  function paint() {
    const info = inspect(normalise(input.value));
    btn.disabled = running || !info.ok;

    const hints = [];
    // Once the server has answered, its error replaces the client-side guess.
    if (info.hint && !memo.error && !memo.errors) hints.push(h('p', { class: 'hint-warn' }, icon('warning'), h('span', null, info.hint)));
    if (info.plainHttp) {
      hints.push(h('p', { class: 'hint-warn' }, icon('warning'),
        h('span', null, 'This portal uses plain HTTP — the agent token will travel unencrypted. Use an https:// portal address unless this network is trusted.')));
    }
    hint.replaceChildren(...hints);

    if (memo.errors) {
      result.replaceChildren(notice('critical', 'The portal settings were not saved',
        Object.entries(memo.errors).map(([k, m]) => (k === '_' ? m : `${labelOf(k)}: ${m}`))));
    } else if (memo.error) {
      result.replaceChildren(h('p', { class: 'field-error' }, icon('critical'), h('span', null, memo.error)));
    } else {
      result.replaceChildren();
    }
    if (memo.error || memo.errors) input.setAttribute('aria-invalid', 'true');
    else input.removeAttribute('aria-invalid');

    if (memo.done) {
      const { portalUrl, agentId, restartRequired } = memo.done;
      done.replaceChildren(notice('ok', 'Portal connected',
        `Connected to ${portalUrl} as ${agentId}.${restartRequired ? ' Restart WatchTower to start reporting.' : ''}`));
    } else {
      done.replaceChildren();
    }
  }

  function clearOutcome() {
    memo.error = null;
    memo.errors = null;
    memo.done = null;
  }

  input.addEventListener('input', () => {
    memo.code = input.value;
    clearOutcome();
    paint();
  });
  input.addEventListener('keydown', (e) => {
    // A code is a single token, so Enter connects instead of adding a line.
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
      e.preventDefault();
      if (!btn.disabled) btn.click();
    }
  });

  btn.addEventListener('click', async () => {
    const code = normalise(input.value);
    if (running || !inspect(code).ok) return;
    if (isDirty()) {
      const ok = await confirmDialog({
        title: 'Save portal settings now?',
        message: 'This section has unsaved changes. Connecting saves the portal URL, agent ID and token from the code right away and replaces your unsaved edits to those settings. Your other unsaved changes stay pending until you save them.',
        confirmLabel: 'Connect',
      });
      if (!ok) return;
    }
    running = true;
    clearOutcome();
    paint();
    try {
      await busy(btn, 'Connecting…', async () => {
        let r;
        try {
          r = await api.enroll(code);
        } catch (e) {
          if (e.status === 401) return; // the shell shows the sign-in screen
          if (e.errors) memo.errors = e.errors;
          else memo.error = e.message;
          return;
        }
        memo.code = '';
        input.value = '';
        memo.done = {
          portalUrl: r?.portalUrl || '',
          agentId: r?.agentId || '',
          restartRequired: !!r?.restartRequired,
        };
        memo.focusDone = true;
        await onEnrolled(r || {}); // reloads the settings, which re-renders this panel from memo
      });
    } finally {
      running = false;
      if (btn.isConnected) {
        paint();
        if (memo.error || memo.errors) input.focus();
      }
    }
  });

  paint();

  // After a successful connect the host re-renders the section; move focus to the result
  // so it is announced and keyboard users are not dropped at the top of the page.
  if (memo.done && memo.focusDone) {
    memo.focusDone = false;
    requestAnimationFrame(() => { if (done.isConnected) done.focus(); });
  }

  return h('section', { class: 'enroll', 'aria-labelledby': headId },
    h('p', { class: 'section-tag' }, '/ Pair with a portal'),
    h('h3', { id: headId, class: 'enroll-title' }, 'Connect with an enrollment code'),
    h('p', { id: helpId, class: 'enroll-help' }, 'Paste the enrollment code from your portal\'s Add server dialog. It fills in the portal URL, agent ID and token for you.'),
    done,
    input,
    hint,
    result,
    h('div', { class: 'btn-row' }, btn));
}
