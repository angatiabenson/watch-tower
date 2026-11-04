// Searchable picker in a native <dialog>. Multi-select (services, app pools) or
// single-select (time zone), with optional free-text entries.
import { h, icon, nextId, plural } from '../dom.js';
import { modal } from '../ui.js';

/**
 * @param {object} o
 * @param {string} o.title
 * @param {() => Promise<{items: {value:string,label?:string,sub?:string,meta?:string,metaKind?:string}[], note?:string}>} o.load
 * @param {boolean} [o.multi]
 * @param {string[]} [o.selected]
 * @param {boolean} [o.allowFreeText]
 * @param {(v:string)=>string|null} [o.validateFreeText]
 * @param {string} [o.searchLabel]
 * @param {string} [o.freeTextNoun]  e.g. "service name"
 * @returns {Promise<string[]|null>} null when cancelled
 */
export function openPicker(o) {
  return new Promise((resolve) => {
    const multi = !!o.multi;
    const titleId = nextId('pk');
    const searchId = nextId('pks');
    const chosen = new Map(); // lower → original casing (multi only)
    for (const v of o.selected || []) chosen.set(v.toLowerCase(), v);
    let items = [];
    let loading = true;

    const search = h('input', {
      id: searchId, type: 'search', class: 'input', autocomplete: 'off', spellcheck: 'false',
      placeholder: 'Type to filter', 'aria-controls': `${titleId}-list`,
    });
    const note = h('div', { class: 'picker-note' });
    const list = h('ul', { class: 'picker-list', id: `${titleId}-list`, 'aria-labelledby': titleId });
    const freeRow = h('div', { class: 'picker-free', hidden: true });
    const countEl = h('p', { class: 'picker-count', 'aria-live': 'polite' });
    const doneBtn = h('button', { class: 'btn btn-primary', type: 'button' }, 'Done');

    const dlg = modal({ titleId, className: 'dialog dialog-lg picker', onDismiss: () => finish(null) });
    const finish = dlg.settleWith(resolve);
    dlg.append(
      h('div', { class: 'dialog-form' },
        h('header', { class: 'dialog-head' },
          h('h2', { id: titleId, class: 'dialog-title' }, o.title),
          h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Close', onclick: () => finish(null) }, icon('close'))),
        h('div', { class: 'picker-search' },
          h('label', { for: searchId, class: 'form-label' }, o.searchLabel || 'Search'),
          h('div', { class: 'input-icon' }, icon('search'), search)),
        note,
        freeRow,
        list,
        h('footer', { class: 'dialog-actions spread' },
          countEl,
          h('div', { class: 'btn-row' },
            h('button', { class: 'btn', type: 'button', onclick: () => finish(null) }, 'Cancel'),
            multi ? doneBtn : null))));

    doneBtn.addEventListener('click', () => finish([...chosen.values()]));

    const updateCount = () => {
      if (multi) countEl.textContent = `${plural(chosen.size, 'item')} selected`;
    };

    function row(it) {
      const key = it.value.toLowerCase();
      const text = [
        h('span', { class: 'picker-main' },
          h('span', { class: 'strong' }, it.label || it.value),
          it.sub ? h('span', { class: 'muted small' }, it.sub) : null),
        it.meta ? h('span', { class: `picker-meta meta-${it.metaKind || 'none'}` }, it.meta) : null,
      ];
      if (multi) {
        const cb = h('input', { type: 'checkbox', class: 'check', checked: chosen.has(key) });
        cb.addEventListener('change', () => {
          if (cb.checked) chosen.set(key, it.value); else chosen.delete(key);
          updateCount();
        });
        return h('li', { dataset: { search: `${it.value} ${it.label || ''}`.toLowerCase() } },
          h('label', { class: 'picker-item' }, cb, ...text));
      }
      const current = (o.selected || []).some((v) => v.toLowerCase() === key);
      return h('li', { dataset: { search: `${it.value} ${it.label || ''}`.toLowerCase() } },
        h('button', { type: 'button', class: 'picker-item', 'aria-pressed': current ? 'true' : 'false', onclick: () => finish([it.value]) },
          current ? icon('check', 'picker-tick') : h('span', { class: 'picker-tick' }), ...text));
    }

    function renderList() {
      list.replaceChildren(...items.map(row));
      filter();
    }

    function filter() {
      const q = search.value.trim().toLowerCase();
      let shown = 0;
      let exact = false;
      for (const li of list.children) {
        if (li.dataset.search === undefined) continue; // loading / empty rows
        const hit = !q || li.dataset.search.includes(q);
        li.hidden = !hit;
        if (hit) shown++;
      }
      if (q) exact = items.some((i) => i.value.toLowerCase() === q);
      if (loading) {
        // keep the "Loading…" row
      } else if (!shown && !(o.allowFreeText && q)) {
        if (!list.querySelector('.picker-empty')) list.append(h('li', { class: 'picker-empty' }, items.length ? 'Nothing matches that search.' : 'Nothing to list.'));
      } else {
        list.querySelector('.picker-empty')?.remove();
      }
      const raw = search.value.trim();
      if (o.allowFreeText && raw && !exact) {
        const err = o.validateFreeText?.(raw) || null;
        freeRow.hidden = false;
        freeRow.replaceChildren(
          h('button', { class: 'btn btn-sm', type: 'button', disabled: !!err, onclick: () => addFree(raw) },
            icon('plus'), `Add “${raw}”${o.freeTextNoun ? ` as a ${o.freeTextNoun}` : ''}`),
          ...(err ? [h('span', { class: 'field-error-inline' }, err)] : []));
      } else {
        freeRow.hidden = true;
        freeRow.replaceChildren();
      }
    }

    function addFree(v) {
      if (!multi) { finish([v]); return; }
      if (!items.some((i) => i.value.toLowerCase() === v.toLowerCase())) {
        items.unshift({ value: v, sub: 'Added by name', meta: 'Not listed', metaKind: 'none' });
      }
      chosen.set(v.toLowerCase(), v);
      search.value = '';
      renderList();
      updateCount();
      search.focus();
    }

    search.addEventListener('input', filter);
    search.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        const visible = [...list.children].filter((li) => !li.hidden && !li.classList.contains('picker-empty'));
        if (!freeRow.hidden && !visible.length) freeRow.querySelector('button:not([disabled])')?.click();
        else if (visible.length === 1) visible[0].querySelector('input,button')?.click();
      } else if (e.key === 'ArrowDown') {
        e.preventDefault();
        list.querySelector('li:not([hidden]) input, li:not([hidden]) button')?.focus();
      }
    });
    list.addEventListener('keydown', (e) => {
      if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
      const focusables = [...list.querySelectorAll('li:not([hidden]) input, li:not([hidden]) button')];
      const i = focusables.indexOf(document.activeElement);
      if (i < 0) return;
      e.preventDefault();
      if (e.key === 'ArrowUp' && i === 0) search.focus();
      else focusables[Math.max(0, Math.min(focusables.length - 1, i + (e.key === 'ArrowDown' ? 1 : -1)))]?.focus();
    });

    document.body.append(dlg);
    dlg.showModal();
    search.focus();
    updateCount();

    const load = () => {
      loading = true;
      list.replaceChildren(h('li', { class: 'picker-empty' }, h('span', { class: 'spinner spinner-sm', 'aria-hidden': 'true' }), 'Loading…'));
      note.replaceChildren();
      o.load().then((res) => {
        items = res.items || [];
        // Selected values the system does not list (typed earlier, or not installed here).
        if (multi) {
          for (const v of chosen.values()) {
            if (!items.some((i) => i.value.toLowerCase() === v.toLowerCase())) {
              items.unshift({ value: v, sub: 'Not found on this server', meta: 'Not listed', metaKind: 'none' });
            }
          }
        }
        loading = false;
        if (res.note) note.replaceChildren(h('p', { class: 'notice-inline' }, icon('info'), res.note));
        renderList();
      }).catch((e) => {
        note.replaceChildren(h('p', { class: 'notice-inline notice-inline-error', role: 'alert' }, icon('critical'), e.message, ' ',
          h('button', { class: 'link-btn', type: 'button', onclick: load }, 'Try again')));
        loading = false;
        items = multi ? [...chosen.values()].map((v) => ({ value: v })) : [];
        renderList();
      });
    };
    load();
  });
}
