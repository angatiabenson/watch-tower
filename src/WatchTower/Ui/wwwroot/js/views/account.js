// Account: change the administrator password, sign out.
import { h } from '../dom.js';
import { api } from '../api.js';
import { toast } from '../ui.js';
import { passwordField, formError } from './auth.js';

const MIN_PASSWORD = 10;

export function mount(root) {
  const current = passwordField({ label: 'Current password', autocomplete: 'current-password', name: 'current' });
  const next = passwordField({ label: 'New password', autocomplete: 'new-password', hint: `At least ${MIN_PASSWORD} characters.`, name: 'new' });
  const confirm = passwordField({ label: 'Confirm new password', autocomplete: 'new-password', name: 'confirm' });
  const err = formError();
  const btn = h('button', { class: 'btn btn-primary', type: 'submit' }, 'Change password');
  const form = h('form', { class: 'form', novalidate: true }, current.row, next.row, confirm.row, err.el, h('div', { class: 'btn-row' }, btn));

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    err.clear();
    if (!current.input.value) { err.show('Enter your current password.'); current.input.focus(); return; }
    if (next.input.value.length < MIN_PASSWORD) { err.show(`Use at least ${MIN_PASSWORD} characters for the new password.`); next.input.focus(); return; }
    if (next.input.value !== confirm.input.value) { err.show('The new passwords do not match.'); confirm.input.focus(); return; }
    if (next.input.value === current.input.value) { err.show('The new password is the same as the current one.'); next.input.focus(); return; }
    btn.disabled = true;
    btn.textContent = 'Changing…';
    try {
      await api.changePassword(current.input.value, next.input.value);
      form.reset();
      toast('Password changed. Other signed-in sessions were signed out.');
    } catch (ex) {
      if (ex.status !== 401) err.show(ex.message);
    } finally {
      btn.disabled = false;
      btn.textContent = 'Change password';
    }
  });

  root.append(
    h('div', { class: 'account' },
      h('section', { class: 'panel', 'aria-labelledby': 'h-pw' },
        h('header', { class: 'panel-head' }, h('h2', { id: 'h-pw', class: 'panel-title' }, h('span', { class: 'slash', 'aria-hidden': 'true' }, '/ '), 'Change password')),
        h('p', { class: 'muted' }, 'There is one administrator password for this console. Changing it signs out every other session.'),
        form),
      h('section', { class: 'panel', 'aria-labelledby': 'h-fp' },
        h('header', { class: 'panel-head' }, h('h2', { id: 'h-fp', class: 'panel-title' }, h('span', { class: 'slash', 'aria-hidden': 'true' }, '/ '), 'Forgot the password?')),
        h('p', null, 'On the server, run ', h('code', null, 'WatchTower.exe set-password'), ' in an elevated command prompt to set a new one.'),
        h('p', { class: 'muted' }, 'Sessions also end on their own after the idle time set in ',
          h('a', { href: '#/config/ui' }, 'Configuration, Web console'), '.'))));
  return {};
}
