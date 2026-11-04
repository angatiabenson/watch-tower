// First-run setup, sign-in and "cannot reach the service" screens.
import { h, icon, nextId } from '../dom.js';
import { api } from '../api.js';

const MIN_PASSWORD = 10;

/** "Console address: <origin> — bookmark it." shown under the sign-in and setup panels. */
function addressHint() {
  return h('p', { class: 'gate-hint' },
    'Console address: ', h('code', { class: 'gate-origin' }, location.origin),
    ' — bookmark it. Run ', h('code', null, 'WatchTower status'), ' on the server to see it at any time.');
}

function frame({ tag, title, lead, hint = false }, ...body) {
  return h('div', { class: 'gate' },
    h('div', { class: 'gate-column' },
      h('div', { class: 'gate-brand' },
        h('span', { class: 'logo-mark', role: 'img', 'aria-label': 'WatchTower' }),
        h('p', { class: 'section-tag' }, `/ ${tag}`)),
      h('div', { class: 'gate-panel' },
        h('h1', { class: 'gate-title' }, title),
        lead ? h('p', { class: 'gate-lead' }, lead) : null,
        ...body),
      hint ? addressHint() : null,
      h('p', { class: 'gate-sign', 'aria-hidden': 'true' }, 'WatchTower agent console')));
}

export function passwordField({ label, autocomplete, hint, autofocus = false, name }) {
  const id = nextId('pw');
  const hintId = hint ? `${id}-hint` : null;
  const input = h('input', {
    id, name, type: 'password', class: 'input', autocomplete, required: true,
    'aria-describedby': hintId, autofocus, spellcheck: 'false',
  });
  const row = h('div', { class: 'form-row' },
    h('label', { for: id, class: 'form-label' }, label),
    input,
    hint ? h('p', { id: hintId, class: 'form-hint' }, hint) : null);
  return { row, input };
}

export function formError() {
  const el = h('p', { class: 'form-error', role: 'alert', hidden: true });
  return {
    el,
    show(msg) { el.replaceChildren(icon('critical'), h('span', null, msg)); el.hidden = false; },
    clear() { el.hidden = true; el.replaceChildren(); },
  };
}

function submitButton(label) {
  return h('button', { class: 'btn btn-primary btn-lg btn-block', type: 'submit' }, label);
}

async function runSubmit(form, button, err, busyLabel, fn) {
  err.clear();
  const prev = button.textContent;
  button.disabled = true;
  button.textContent = busyLabel;
  try {
    await fn();
  } catch (e) {
    err.show(e.message);
  } finally {
    if (form.isConnected) {
      button.disabled = false;
      button.textContent = prev;
    }
  }
}

export function renderSetup(state, { onDone, onRecheck }) {
  if (!state.setupAllowedFromHere) {
    return frame({ tag: 'First-run setup', title: 'Finish setup on the server', hint: true,
      lead: 'The administrator password has not been created yet. For security, the first password can only be set on the server itself.' },
      h('ol', { class: 'gate-steps' },
        h('li', null, 'On the server, open ', h('a', { href: 'http://127.0.0.1:8750' }, 'http://127.0.0.1:8750'), ' in a browser and create the password there.'),
        h('li', null, 'Or, in an elevated command prompt in the WatchTower install folder, run ',
          h('code', null, 'WatchTower.exe set-password'), '.')),
      h('button', { class: 'btn btn-lg btn-block', type: 'button', autofocus: true, onclick: onRecheck }, icon('refresh'), 'Check again'));
  }

  const pw = passwordField({ label: 'New password', autocomplete: 'new-password', hint: `At least ${MIN_PASSWORD} characters. A short phrase is easier to remember than symbols.`, autofocus: true, name: 'password' });
  const confirm = passwordField({ label: 'Confirm password', autocomplete: 'new-password', name: 'confirm' });
  const err = formError();
  const btn = submitButton('Create password and sign in');
  const form = h('form', { class: 'form', novalidate: true },
    pw.row, confirm.row, err.el, btn);

  form.addEventListener('submit', (e) => {
    e.preventDefault();
    if (pw.input.value.length < MIN_PASSWORD) { err.show(`Use at least ${MIN_PASSWORD} characters.`); pw.input.focus(); return; }
    if (pw.input.value !== confirm.input.value) { err.show('The two passwords do not match.'); confirm.input.focus(); return; }
    runSubmit(form, btn, err, 'Creating…', async () => {
      await api.setup(pw.input.value);
      onDone();
    });
  });

  return frame({ tag: 'First-run setup', title: 'Set up WatchTower', hint: true,
    lead: 'Create the administrator password for this console. You will use it to sign in from now on.' },
  form);
}

export function renderLogin({ onDone, expired = false }) {
  const pw = passwordField({ label: 'Password', autocomplete: 'current-password', autofocus: true, name: 'password' });
  const err = formError();
  const btn = submitButton('Sign in');
  const form = h('form', { class: 'form', novalidate: true }, pw.row, err.el, btn);

  form.addEventListener('submit', (e) => {
    e.preventDefault();
    if (!pw.input.value) { err.show('Enter the administrator password.'); pw.input.focus(); return; }
    runSubmit(form, btn, err, 'Signing in…', async () => {
      try {
        await api.login(pw.input.value);
      } catch (ex) {
        if (ex.status === 401 && !ex.body?.error) ex.message = 'That password is not correct.';
        pw.input.select();
        throw ex;
      }
      onDone();
    });
  });

  return frame({ tag: 'Access', title: 'Sign in to WatchTower', hint: true,
    lead: expired ? 'Your session ended. Sign in again to continue where you left off.' : 'Enter the administrator password for this server.' },
  form,
    h('p', { class: 'gate-foot' }, 'Forgot the password? On the server, run ', h('code', null, 'WatchTower.exe set-password'), ' to set a new one.'));
}

export function renderUnreachable(message, onRetry) {
  return frame({ tag: 'Connection lost', title: 'Cannot reach WatchTower', lead: message },
    h('button', { class: 'btn btn-primary btn-lg btn-block', type: 'button', autofocus: true, onclick: onRetry }, icon('refresh'), 'Try again'));
}
