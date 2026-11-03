// Configuration: rendered entirely from GET /api/config. Unsaved edits ("drafts") are
// kept across section switches and sent as one PUT /api/config with only changed keys.
import { h, icon, nextId, plural, statusTag } from '../dom.js';
import { api, serverOffset } from '../api.js';
import { notice, toast, busy } from '../ui.js';
import { createEditor } from './fields.js';
import { enrollPanel, ENROLL_KEYS } from './enroll.js';
import { startMaintenance, endMaintenance, parseUntil, fmtUntil } from './maintenance.js';

/** Fixed channels of the Notification channels section, and the saved setting that enables each. */
const CHANNELS = [
  { name: 'email', label: 'Email', enabled: (v) => v('Notifications:Email:Enabled').value === true },
  { name: 'teams', label: 'Teams', enabled: (v) => !!v('Notifications:Teams:Url').hasValue },
  { name: 'slack', label: 'Slack', enabled: (v) => !!v('Notifications:Slack:Url').hasValue },
  { name: 'webhook', label: 'Webhook', enabled: (v) => !!v('Notifications:Webhook:Url').hasValue },
];

const SOURCE_BADGE = {
  portal: 'Managed by portal',
  environment: 'Set by environment',
};

export function mount(root, params, ctx) {
  const state = {
    data: null,            // { sections, values, warnings }
    sectionId: params[0] || null,
    drafts: new Map(),     // key → { value, error, display }
    resets: new Set(),     // keys to revert to default
    serverErrors: new Map(),
    generalError: null,
    saving: false,
    lastWarnings: [],
    rows: new Map(),       // key → row controller (current section only)
    enroll: {},            // enrollment box state, kept across re-renders and section switches
    notifyTest: null,      // last test-notification outcome, kept across re-renders
    destroyed: false,
  };

  const nav = h('nav', { class: 'subnav', 'aria-label': 'Settings sections' });
  const warnSlot = h('div', { class: 'config-warnings' });
  const body = h('div', { class: 'config-body' });
  const saveCount = h('p', { class: 'savebar-count' });
  const saveMsg = h('p', { class: 'savebar-msg', role: 'alert' });
  const discardBtn = h('button', { class: 'btn', type: 'button' }, 'Discard');
  const saveBtn = h('button', { class: 'btn btn-primary', type: 'button' }, 'Save changes');
  const saveBar = h('div', { class: 'savebar', role: 'region', 'aria-label': 'Unsaved changes', hidden: true },
    h('div', { class: 'savebar-text' }, saveCount, saveMsg),
    h('div', { class: 'btn-row' }, discardBtn, saveBtn));

  const layout = h('div', { class: 'config' }, nav, h('div', { class: 'config-main' }, warnSlot, body, saveBar));
  root.append(h('p', { class: 'loading' }, h('span', { class: 'spinner spinner-sm', 'aria-hidden': 'true' }), 'Loading settings…'));

  discardBtn.addEventListener('click', discard);
  saveBtn.addEventListener('click', save);

  // ------------------------------------------------------------ data
  async function load({ keepSection = true } = {}) {
    try {
      const data = await api.config();
      if (state.destroyed) return;
      state.data = data;
      if (!keepSection || !findSection(state.sectionId)) {
        state.sectionId = findSection(state.sectionId)?.id || data.sections?.[0]?.id || null;
        if (state.sectionId) history.replaceState(null, '', `#/config/${state.sectionId}`);
      }
      root.replaceChildren(layout);
      renderAll();
    } catch (e) {
      if (state.destroyed || e.status === 401) return;
      root.replaceChildren(notice('critical', 'Settings could not be loaded', e.message, h('button', {
        class: 'btn btn-sm', type: 'button', onclick: () => load(),
      }, icon('refresh'), 'Try again'), { live: true }));
    }
  }

  const findSection = (id) => state.data?.sections?.find((s) => s.id === id);
  const sectionOfKey = (key) => state.data?.sections?.find((s) => s.fields.some((f) => f.key === key));
  const valueOf = (key) => state.data?.values?.[key] || { value: null, source: 'default', overridden: false, hasValue: false };
  const changeCount = () => state.drafts.size + state.resets.size;
  const sectionDirty = (s) => s.fields.some((f) => state.drafts.has(f.key) || state.resets.has(f.key));
  const sectionHasError = (s) => s.fields.some((f) => state.drafts.get(f.key)?.error || state.serverErrors.has(f.key));

  // ------------------------------------------------------------ render
  function renderAll() {
    renderNav();
    renderWarnings();
    renderSection();
    renderSaveBar();
  }

  function renderNav() {
    const sections = state.data?.sections || [];
    nav.replaceChildren(h('ul', { class: 'subnav-list' }, sections.map((s) => {
      const dirty = sectionDirty(s);
      const err = sectionHasError(s);
      return h('li', null, h('a', {
        class: 'subnav-link', href: `#/config/${s.id}`,
        'aria-current': s.id === state.sectionId ? 'page' : null,
      },
      h('span', null, s.title),
      err ? h('span', { class: 'subnav-mark mark-error', title: 'Has errors' }, icon('critical'), h('span', { class: 'sr-only' }, ' (has errors)'))
        : dirty ? h('span', { class: 'subnav-mark mark-dirty', title: 'Unsaved changes' }, h('span', { class: 'sr-only' }, ' (unsaved changes)')) : null));
    })));
  }

  function renderWarnings() {
    const w = [...(state.data?.warnings || []), ...state.lastWarnings];
    warnSlot.replaceChildren(w.length ? notice('warning', null, [...new Set(w)]) : '');
  }

  function renderSection() {
    state.rows.clear();
    const s = findSection(state.sectionId);
    if (!s) {
      body.replaceChildren(h('p', { class: 'empty' }, 'Choose a section on the left.'));
      return;
    }
    ctx.setTitle(`Configuration: ${s.title}`);
    const basic = s.fields.filter((f) => !f.advanced);
    const adv = s.fields.filter((f) => f.advanced);
    const advDirty = adv.some((f) => state.drafts.has(f.key) || state.resets.has(f.key) || state.serverErrors.has(f.key));

    body.replaceChildren(
      h('header', { class: 'section-head' },
        h('p', { class: 'section-tag' }, `/ Section ${String(state.data.sections.indexOf(s) + 1).padStart(2, '0')} of ${String(state.data.sections.length).padStart(2, '0')}`),
        h('h2', { class: 'section-title', tabindex: '-1' }, s.title),
        s.description ? h('p', { class: 'section-desc' }, s.description)
          : null),
      s.id === 'portal' ? enrollBlock(s) : '',
      s.id === 'maintenance' ? maintenanceBlock(s) : '',
      h('div', { class: 'fields' }, basic.map(fieldRow)),
      adv.length ? h('details', { class: 'advanced', open: advDirty },
        h('summary', null, `Advanced (${adv.length})`),
        h('div', { class: 'fields' }, adv.map(fieldRow))) : '',
      s.id === 'email' ? testEmailBlock(s) : '',
      s.id === 'notifications' ? testNotifyBlock(s) : '');
  }

  function fieldRow(f) {
    const v = valueOf(f.key);
    const id = nextId('f');
    const helpId = f.help ? `${id}-help` : null;
    const errId = `${id}-err`;
    const readOnly = !!v.overridden;
    const draft = state.drafts.get(f.key);
    const resetPending = state.resets.has(f.key);

    const badges = h('span', { class: 'badges' });
    const err = h('p', { id: errId, class: 'field-error', hidden: true });
    const actions = h('div', { class: 'field-actions' });
    const resetNote = h('p', { class: 'reset-note', hidden: true }, icon('undo'), 'Reverts to the default value when you save.');

    const current = draft ? draft.value : (f.type === 'Secret' ? undefined : v.value);
    const editor = createEditor(f, {
      id,
      describedBy: [helpId, errId].filter(Boolean).join(' '),
      value: current,
      display: draft?.display,
      original: v.value,
      hasValue: v.hasValue,
      readOnly: readOnly || resetPending,
      onChange: (value, error, display) => onFieldChange(f, value, error, display),
    });

    const labelEl = f.type === 'TimeZone'
      ? h('span', { class: 'field-label', id: `${id}-label` }, f.label)
      : h('label', { class: 'field-label', for: id }, f.label);
    if (f.type === 'TimeZone') editor.el.setAttribute('aria-labelledby', `${id}-label ${id}`);

    const row = h('div', { class: 'field', dataset: { key: f.key } },
      h('div', { class: 'field-meta' },
        h('div', { class: 'field-label-row' }, labelEl, badges),
        f.help ? h('p', { id: helpId, class: 'field-help' }, f.help) : null),
      h('div', { class: 'field-control' }, editor.el, resetNote, err, actions));

    const ctrl = {
      focus: editor.focus,
      sync() {
        const d = state.drafts.get(f.key);
        const reset = state.resets.has(f.key);
        const msg = d?.error || state.serverErrors.get(f.key) || null;
        row.classList.toggle('is-dirty', !!d || reset);
        row.classList.toggle('is-invalid', !!msg);
        row.classList.toggle('is-locked', readOnly);
        err.hidden = !msg;
        err.replaceChildren(...(msg ? [icon('critical'), h('span', null, msg)] : []));
        resetNote.hidden = !reset;
        // aria-invalid on the actual control(s)
        row.querySelectorAll('.field-control input, .field-control select').forEach((el) => {
          if (msg) el.setAttribute('aria-invalid', 'true'); else el.removeAttribute('aria-invalid');
        });

        const b = [];
        if (readOnly) b.push(h('span', { class: 'badge badge-lock' }, icon('lock'), SOURCE_BADGE[v.source] || 'Managed elsewhere'));
        if (f.type === 'Secret' && v.hasValue && !readOnly) b.push(h('span', { class: 'badge badge-set' }, '•••• set'));
        if (f.type === 'Secret' && !v.hasValue) b.push(h('span', { class: 'badge' }, 'Not set'));
        if (!readOnly && v.source === 'operator' && !reset && f.type !== 'Secret') b.push(h('span', { class: 'badge badge-custom', title: 'Changed from the shipped default' }, 'Customised'));
        if (f.restartRequired) b.push(h('span', { class: 'badge badge-restart' }, icon('restart'), 'Restart required'));
        if (d || reset) b.push(h('span', { class: 'badge badge-dirty' }, 'Unsaved'));
        badges.replaceChildren(...b);

        const a = [];
        if (d) a.push(h('button', { class: 'link-btn', type: 'button', onclick: () => undoField(f) }, 'Undo change'));
        if (reset) a.push(h('button', { class: 'link-btn', type: 'button', onclick: () => undoField(f) }, 'Keep current value'));
        else if (!readOnly && v.source === 'operator' && f.type !== 'Secret') { // secrets use "Clear saved value"
          a.push(h('button', { class: 'link-btn', type: 'button', onclick: () => resetField(f) }, 'Reset to default'));
        }
        if (readOnly) {
          a.push(h('span', { class: 'muted small' }, v.source === 'portal'
            ? 'Change it in the Visibility Portal, or turn off portal-managed settings.'
            : 'Remove the environment variable to edit it here.'));
        }
        actions.replaceChildren(...a);
        actions.hidden = !a.length;
      },
    };
    state.rows.set(f.key, ctrl);
    ctrl.sync();
    return row;
  }

  function testEmailBlock(s) {
    const dirty = sectionDirty(s);
    const result = h('div', { class: 'test-result', 'aria-live': 'polite' });
    const btn = h('button', { class: 'btn', type: 'button', disabled: dirty }, icon('mail'), 'Send test email');
    btn.addEventListener('click', () => busy(btn, 'Sending…', async () => {
      result.replaceChildren();
      try {
        const r = await api.testEmail();
        result.replaceChildren(r?.ok
          ? h('p', { class: 'result-ok' }, icon('ok'), r.message || 'Test email sent.')
          : h('p', { class: 'result-bad' }, icon('critical'), r?.message || 'The test email could not be sent.'));
      } catch (e) {
        result.replaceChildren(h('p', { class: 'result-bad' }, icon('critical'), e.message));
      }
    }));
    return h('section', { class: 'test-email', 'aria-labelledby': 'test-email-h' },
      h('h3', { id: 'test-email-h', class: 'subhead' }, 'Check the email settings'),
      h('p', { class: 'muted' }, 'Sends a message to the saved recipients using the saved settings.'),
      dirty ? h('p', { class: 'hint-warn' }, icon('warning'), 'Save your changes first. The test uses the saved settings, not the ones on screen.') : null,
      h('div', { class: 'btn-row' }, btn),
      result);
  }

  // ------------------------------------------------------------ notification test
  function testNotifyBlock(s) {
    const dirty = sectionDirty(s);
    const enabled = CHANNELS.filter((c) => c.enabled(valueOf));
    const result = h('div', { class: 'test-result notify-results', 'aria-live': 'polite' });
    const allBtn = h('button', { class: 'btn', type: 'button', disabled: dirty }, icon('bell'), 'Send test notification');
    const buttons = [allBtn];

    const run = (btn, channel) => busy(btn, 'Sending…', async () => {
      for (const b of buttons) b.disabled = true;
      result.replaceChildren(h('p', { class: 'muted small' }, channel ? `Sending a test to ${channel}…` : 'Sending a test to every enabled channel…'));
      try {
        const r = await api.testNotification(channel);
        state.notifyTest = { channel, ok: !!r?.ok, message: r?.message || '', results: Array.isArray(r?.results) ? r.results : [] };
      } catch (e) {
        if (e.status === 401) return;
        state.notifyTest = { channel, ok: false, message: e.message, results: [], error: true };
      } finally {
        for (const b of buttons) b.disabled = sectionDirty(s);
      }
      renderNotifyResult(result, rerun);
    });
    const rerun = (channel, btn) => run(btn, channel);
    allBtn.addEventListener('click', () => run(allBtn, null));

    const perChannel = enabled.map((c) => {
      const b = h('button', { class: 'btn btn-sm', type: 'button', disabled: dirty, 'aria-label': `Send a test to ${c.label} only` }, c.label);
      b.addEventListener('click', () => run(b, c.name));
      buttons.push(b);
      return b;
    });

    renderNotifyResult(result, rerun);
    return h('section', { class: 'test-email test-notify', 'aria-labelledby': 'test-notify-h' },
      h('h3', { id: 'test-notify-h', class: 'subhead' }, 'Check the notification channels'),
      h('p', { class: 'muted' }, 'Sends a test message using the saved settings. Minimum-severity filters do not apply to tests.'),
      dirty ? h('p', { class: 'hint-warn' }, icon('warning'), 'Save your changes first. The test uses the saved settings, not the ones on screen.') : null,
      h('div', { class: 'btn-row' }, allBtn,
        perChannel.length ? [h('span', { class: 'btn-row-label' }, 'or only'), perChannel] : null),
      result);
  }

  function renderNotifyResult(el, rerun) {
    const t = state.notifyTest;
    if (!t) { el.replaceChildren(); return; }
    const out = [];
    if (t.error) {
      out.push(h('p', { class: 'result-bad' }, icon('critical'), t.message));
    } else if (!t.results.length) {
      out.push(h('p', { class: 'result-bad' }, icon('critical'), t.message || 'No notification channel is enabled.'),
        h('p', { class: 'muted small' }, 'Turn on email alerts or add a webhook URL above, save, then send the test again.'));
    } else {
      out.push(h('p', { class: t.ok ? 'result-ok' : 'result-bad' }, icon(t.ok ? 'ok' : 'critical'),
        t.message || (t.ok ? 'Test notification sent.' : 'Some channels failed.')));
      out.push(h('ul', { class: 'channel-results', 'aria-label': 'Results per channel' }, t.results.map((r) => {
        const again = h('button', { class: 'link-btn', type: 'button', 'aria-label': `Send the test to ${r.channel} again` }, 'Send again');
        again.addEventListener('click', () => rerun(r.channel, again));
        return h('li', { class: r.ok ? 'is-ok' : 'is-bad' },
          statusTag(r.ok ? 'ok' : 'critical', r.ok ? 'Delivered' : 'Failed'),
          h('p', { class: 'channel-name' }, h('span', { class: 'strong' }, r.channel || '–'),
            r.type && String(r.type).toLowerCase() !== String(r.channel).toLowerCase() ? h('span', { class: 'muted' }, ` ${r.type}`) : null),
          h('p', { class: 'channel-msg' }, r.message || ''),
          r.ok ? null : again);
      })));
    }
    el.replaceChildren(...out);
  }

  // ------------------------------------------------------------ maintenance window
  function maintenanceBlock(s) {
    const dirty = sectionDirty(s);
    const untilV = valueOf('Maintenance:Until');
    const reason = valueOf('Maintenance:Reason').value;
    const until = parseUntil(untilV.value);
    const active = Number.isFinite(until) && until > Date.now() + serverOffset();
    const managed = !!untilV.overridden;

    const done = async () => { await load(); body.querySelector('.maint-block button')?.focus(); };
    const startBtn = h('button', { class: active ? 'btn btn-sm' : 'btn btn-primary btn-sm', type: 'button', disabled: dirty || managed },
      icon('pause'), active ? 'Change…' : 'Start maintenance');
    startBtn.addEventListener('click', async () => {
      if (await startMaintenance({ current: active ? { active, until: untilV.value, reason } : null })) await done();
    });
    const endBtn = active ? h('button', { class: 'btn btn-sm btn-maint', type: 'button', disabled: dirty || managed }, 'End maintenance') : null;
    endBtn?.addEventListener('click', async () => { if (await endMaintenance(endBtn)) await done(); });

    return h('section', { class: `maint-block${active ? ' is-active' : ''}`, 'aria-labelledby': 'maint-block-h' },
      h('p', { class: 'section-tag' }, '/ Now'),
      h('h3', { id: 'maint-block-h', class: 'maint-block-title' },
        active ? [statusTag('warning', 'Active'), ` Maintenance until ${fmtUntil(until)}`] : [statusTag('stopped', 'Off'), ' No maintenance window is active']),
      h('p', { class: 'enroll-help' }, active
        ? (reason ? ['Notifications and automatic restarts are paused: ', h('span', { class: 'strong' }, reason)] : 'Notifications and automatic restarts are paused.')
        : 'Start a window before planned work so restarts and alerts do not page anyone. It ends on its own.'),
      managed ? h('p', { class: 'hint-warn' }, icon('lock'), 'The portal controls the maintenance window for this server. Change it in the Visibility Portal.')
        : dirty ? h('p', { class: 'hint-warn' }, icon('warning'), 'Save or discard your changes to this section first.') : null,
      h('div', { class: 'btn-row' }, startBtn, endBtn));
  }

  function enrollBlock(s) {
    return enrollPanel(state.enroll, {
      isDirty: () => sectionDirty(s),
      labelOf: (key) => sectionOfKey(key)?.fields.find((f) => f.key === key)?.label || key,
      async onEnrolled(r) {
        // The server saved these settings, so on-screen edits to them are superseded.
        for (const k of ENROLL_KEYS) {
          state.drafts.delete(k);
          state.resets.delete(k);
          state.serverErrors.delete(k);
        }
        state.generalError = null;
        state.lastWarnings = r.warnings || [];
        if (r.restartRequired) ctx.setRestartPending(true);
        toast(r.restartRequired ? 'Portal connected. Restart WatchTower to start reporting.' : 'Portal connected.');
        await load(); // re-renders the section, so the portal fields show the new values
      },
    });
  }

  function renderSaveBar() {
    const n = changeCount();
    const invalid = [...state.drafts.values()].filter((d) => d.error).length;
    const show = n > 0 || !!state.generalError || state.serverErrors.size > 0;
    saveBar.hidden = !show;
    layout.classList.toggle('has-savebar', show);
    saveCount.textContent = n ? `${plural(n, 'unsaved change')}` : 'No unsaved changes';
    const msg = state.generalError
      || (invalid ? `${plural(invalid, 'field needs', 'fields need')} fixing before you can save.` : '')
      || (state.serverErrors.size ? 'Some settings were not accepted. Check the highlighted sections.' : '');
    saveMsg.textContent = msg;
    saveMsg.hidden = !msg;
    saveBtn.disabled = state.saving || n === 0;
    discardBtn.disabled = state.saving || n === 0;
  }

  // ------------------------------------------------------------ editing
  const norm = (v) => (v == null || (Array.isArray(v) && !v.length) ? '' : v);
  const same = (a, b) => JSON.stringify(norm(a)) === JSON.stringify(norm(b));

  function onFieldChange(f, value, error, display) {
    const orig = valueOf(f.key).value;
    state.serverErrors.delete(f.key);
    state.generalError = null;
    const unchanged = f.type === 'Secret' ? value === undefined : (!error && same(value, orig));
    if (unchanged && !error) state.drafts.delete(f.key);
    else state.drafts.set(f.key, { value, error: error || null, display });
    state.rows.get(f.key)?.sync();
    renderNav();
    renderSaveBar();
    refreshActionBlocks();
  }

  /** Action boxes depend on whether their section has unsaved edits. */
  function refreshActionBlocks() {
    const s = findSection(state.sectionId);
    if (!s) return;
    const swap = (sel, build) => { const old = body.querySelector(sel); if (old) old.replaceWith(build(s)); };
    if (s.id === 'email') swap('.test-email', testEmailBlock);
    if (s.id === 'notifications') swap('.test-notify', testNotifyBlock);
    if (s.id === 'maintenance') swap('.maint-block', maintenanceBlock);
  }

  function undoField(f) {
    state.drafts.delete(f.key);
    state.resets.delete(f.key);
    state.serverErrors.delete(f.key);
    rerenderKeepingFocus(f.key);
  }

  function resetField(f) {
    state.drafts.delete(f.key);
    state.resets.add(f.key);
    rerenderKeepingFocus(f.key);
  }

  function rerenderKeepingFocus(key) {
    const y = window.scrollY;
    renderSection();
    renderNav();
    renderSaveBar();
    window.scrollTo(0, y);
    const row = body.querySelector(`.field[data-key="${CSS.escape(key)}"]`);
    (row?.querySelector('.field-actions button') || row?.querySelector('input,select,button'))?.focus();
  }

  function discard() {
    state.drafts.clear();
    state.resets.clear();
    state.serverErrors.clear();
    state.generalError = null;
    renderAll();
    toast('Changes discarded.', { kind: 'info' });
  }

  async function save() {
    if (state.saving || !changeCount()) return;
    const invalid = [...state.drafts.entries()].find(([, d]) => d.error);
    if (invalid) {
      goToKey(invalid[0]);
      renderSaveBar();
      return;
    }
    const changes = {};
    for (const [k, d] of state.drafts) changes[k] = d.value;
    const reset = [...state.resets];

    state.saving = true;
    state.generalError = null;
    renderSaveBar();
    try {
      await busy(saveBtn, 'Saving…', async () => {
        const r = await api.saveConfig(changes, reset);
        state.drafts.clear();
        state.resets.clear();
        state.serverErrors.clear();
        state.lastWarnings = r?.warnings || [];
        if (r?.restartRequired) ctx.setRestartPending(true);
        toast(r?.restartRequired ? 'Settings saved. Restart WatchTower to apply some of them.' : 'Settings saved.');
        await load(); // the shell shows the "Restart needed" banner via setRestartPending
      });
    } catch (e) {
      if (e.status === 401) return;
      if (e.errors) {
        state.serverErrors = new Map(Object.entries(e.errors).filter(([k]) => k !== '_'));
        state.generalError = e.errors._ || null;
        const firstKey = [...state.serverErrors.keys()][0];
        renderAll();
        if (firstKey) goToKey(firstKey);
      } else {
        state.generalError = e.message;
      }
    } finally {
      state.saving = false;
      renderSaveBar();
    }
  }

  function goToKey(key) {
    const s = sectionOfKey(key);
    if (s && s.id !== state.sectionId) {
      state.sectionId = s.id;
      history.replaceState(null, '', `#/config/${s.id}`);
      renderSection();
      renderNav();
    } else {
      // make sure errors are painted in the current section
      for (const r of state.rows.values()) r.sync();
    }
    const row = body.querySelector(`.field[data-key="${CSS.escape(key)}"]`);
    const det = row?.closest('details');
    if (det) det.open = true;
    row?.scrollIntoView({ block: 'center' });
    state.rows.get(key)?.focus();
  }

  load({ keepSection: false });

  return {
    update(p) {
      const id = p[0] || state.data?.sections?.[0]?.id;
      if (!state.data) { state.sectionId = id; return; }
      if (!findSection(id)) {
        history.replaceState(null, '', `#/config/${state.sectionId}`);
        return;
      }
      if (id === state.sectionId && p[0]) return;
      state.sectionId = id;
      if (!p[0]) history.replaceState(null, '', `#/config/${id}`);
      renderSection();
      renderNav();
      body.querySelector('.section-title')?.focus?.();
      window.scrollTo(0, 0);
    },
    isDirty: () => changeCount() > 0,
    discard() { state.drafts.clear(); state.resets.clear(); },
    unmount() { state.destroyed = true; },
  };
}
