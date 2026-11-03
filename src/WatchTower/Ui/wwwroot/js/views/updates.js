// Updates: placeholder until the update API (GET /api/updates, ...) ships.
import { h, icon } from '../dom.js';

export function mount(root) {
  root.append(h('section', { class: 'panel placeholder' },
    icon('updates', 'placeholder-icon'),
    h('p', { class: 'section-tag' }, '/ Coming next'),
    h('h2', { class: 'placeholder-title' }, 'Update management arrives in the next release'),
    h('p', { class: 'muted' }, 'Until then, update WatchTower by running the new installer on this server. Your settings and data folder are kept.')));
  return {};
}
