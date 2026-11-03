// Updates: check → download (verified) → restart countdown → install. The state machine lives
// in the service (GET /api/updates), so a countdown started here keeps running — and can be
// cancelled — from any session or the portal. See API.md "Updates".
import { h, icon, statusTag, fmtDateTime, fmtRelative, isNum } from '../dom.js';
import { api, msUntilServer, serverOffset } from '../api.js';
import { notice, toast, busy, waitForServiceCycle } from '../ui.js';

const FAST_MS = 1000;       // Checking, Downloading, countdown running
const CONFIRM_MS = 2000;    // after an install: waiting for the installer's verdict
const SLOW_MS = 30_000;
const RETRY_MS = 5000;     // the last poll failed
const TICK_MS = 250;        // countdown repaint
const ANNOUNCE_GAP_MS = 5000;
// The countdown length is a setting (Updates:RestartCountdownSeconds, default 30) that the
// status does not carry; the drain bar uses the default or the longest remaining time seen.
const DEFAULT_COUNTDOWN_S = 30;
// sessionStorage: an install this tab watched, until the installer's lastResult arrives.
const PENDING_KEY = 'wt.updateInstall';
const PENDING_MAX_MS = 6 * 60_000;

// ------------------------------------------------------------------ shared helpers

export function fmtBytes(n) {
  if (!isNum(n) || n <= 0) return '–';
  const units = ['bytes', 'KB', 'MB', 'GB'];
  let i = 0;
  let v = n;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${new Intl.NumberFormat(undefined, { maximumFractionDigits: i ? 1 : 0 }).format(v)} ${units[i]}`;
}

/** Whole seconds left in a countdown (never negative), from restartAt in server time. */
export function secondsLeft(restartAt) {
  const ms = msUntilServer(restartAt);
  return Number.isNaN(ms) ? 0 : Math.max(0, Math.ceil(ms / 1000));
}

/** "3 minutes ago" for a server timestamp, corrected for the browser's clock skew. */
export function fmtRelativeServer(iso) {
  const t = Date.parse(iso || '');
  return Number.isNaN(t) ? '' : fmtRelative(new Date(t - serverOffset()).toISOString());
}

/** A countdown is running when the update is staged and restartAt is set. */
export const countdownRunning = (s) => !!(s && s.restartAt && s.phase === 'ReadyToInstall');

function readPending() {
  try {
    const p = JSON.parse(sessionStorage.getItem(PENDING_KEY) || 'null');
    if (p && typeof p.to === 'string' && isNum(p.at) && Date.now() - (p.seenAt ?? Date.now()) < PENDING_MAX_MS) return p;
  } catch { /* ignore */ }
  sessionStorage.removeItem(PENDING_KEY);
  return null;
}

/** lastResult belongs to the install recorded in `pending` (allowing for clock jitter). */
const resultMatches = (r, p) => !!(r && p && r.toVersion === p.to && Date.parse(r.completedAt) >= p.at - 60_000);

// ------------------------------------------------------------------ install overlay

let watching = false;

/**
 * Blocking progress dialog for an install: waits for the service to stop answering, then for
 * it to come back (the installer waits for a health check and may roll back), then reloads.
 * Used by this view and the dashboard.
 */
export async function watchInstall({ version, from }) {
  if (watching) return;
  watching = true;
  const startedAt = Date.now() + serverOffset();

  const STEPS = ['Stopping WatchTower', 'Installing and health-checking the new version', 'Reconnecting this console'];
  const items = STEPS.map((label) => {
    const state = h('span', { class: 'step-state' });
    const li = h('li', null, h('span', { class: 'step-glyph', 'aria-hidden': 'true' }, '■'), h('span', { class: 'step-label' }, label), state);
    return { li, state };
  });
  const setStep = (n) => items.forEach((it, i) => {
    it.li.className = i < n ? 'is-done' : i === n ? 'is-active' : '';
    it.state.textContent = i < n ? 'Done' : i === n ? 'Now' : '';
  });
  const status = h('p', { class: 'dialog-text', role: 'status' }, 'Waiting for the service to stop…');
  const spinner = h('span', { class: 'spinner', 'aria-hidden': 'true' });
  const form = h('div', { class: 'dialog-form' },
    h('div', { class: 'install-head' }, spinner,
      h('h2', { id: 'install-title', class: 'dialog-title', tabindex: '-1' }, `Installing WatchTower ${version || 'update'}`)),
    h('ol', { class: 'install-steps' }, items.map((it) => it.li)),
    status,
    h('p', { class: 'dialog-note' }, 'Monitoring pauses until the new version is running. If it does not start correctly, the previous version is restored automatically. This page reloads on its own.'));
  const dlg = h('dialog', { class: 'dialog install-dialog', 'aria-labelledby': 'install-title', 'aria-describedby': 'install-status' }, form);
  status.id = 'install-status';
  dlg.addEventListener('cancel', (e) => e.preventDefault()); // Esc must not hide the progress
  document.body.append(dlg);
  dlg.showModal();
  form.querySelector('#install-title').focus();
  setStep(0);

  const back = await waitForServiceCycle({
    downMs: 60_000,
    upMs: 180_000,
    onStage: (stage) => {
      if (stage !== 'up') return;
      setStep(1);
      status.textContent = 'WatchTower is installing the update and checking that it starts. This can take up to three minutes.';
    },
  });

  if (back) {
    setStep(2);
    status.textContent = 'WatchTower is back. Reloading…';
    try {
      sessionStorage.setItem(PENDING_KEY, JSON.stringify({ to: version, from, at: startedAt, seenAt: Date.now() }));
    } catch { /* private mode: the result still shows, just without the "finishing" notice */ }
    location.reload();
    return;
  }

  watching = false;
  spinner.remove();
  status.textContent = 'WatchTower has not come back after three minutes. The installer may still be checking the new version or restoring the previous one. Check the WatchTower service in Services (services.msc) and the log files in the data folder, then reload this page.';
  form.append(h('div', { class: 'dialog-actions' },
    h('button', { class: 'btn', type: 'button', onclick: () => { dlg.close(); dlg.remove(); } }, 'Close'),
    h('button', { class: 'btn btn-primary', type: 'button', onclick: () => location.reload() }, 'Reload page')));
}

// ------------------------------------------------------------------ view

export function mount(root, _params, ctx) {
  let st = null;               // last GET /api/updates
  let isConsole = false;       // from /api/status: not running as a Windows service
  let loadError = null;        // the last poll failed
  let pending = readPending(); // install watched by this tab, awaiting its result
  let destroyed = false;
  let timer = 0;
  let ticker = 0;
  let seq = 0;                 // ignore responses older than the newest one applied
  let applied = 0;
  let controller = null;
  const prevKeys = new Map();
  const cd = { restartAt: null, total: DEFAULT_COUNTDOWN_S, lastSec: -1, announcedAt: 0, els: null };
  let progressEls = null;
  let resultArrived = false;   // lastResult for this tab's install just came in: announce it
  let fastUntil = 0;           // poll fast for a moment after starting a download

  // page actions: last-checked time + Check for updates
  const checked = h('span', { class: 'muted' }, 'Loading…');
  const checkBtn = h('button', { class: 'btn btn-sm', type: 'button', onclick: () => check() }, icon('refresh'), 'Check for updates');
  ctx.setActions(checked, checkBtn);

  const slots = {
    notices: h('div', { class: 'update-notices' }),
    countdown: h('div', { class: 'update-countdown-slot' }),
    hero: h('section', { class: 'panel update-hero', 'aria-labelledby': 'h-update' }),
    notes: h('div', { class: 'update-notes-slot' }),
  };
  // One polite live region for the countdown, present before anything is written to it.
  const live = h('p', { class: 'sr-only', 'aria-live': 'polite', 'aria-atomic': 'true' });
  root.append(h('div', { class: 'updates' },
    h('div', { class: 'updates-main' }, slots.countdown, slots.notices, slots.hero, slots.notes),
    howItWorks()), live);
  slots.hero.append(h('p', { class: 'loading' }, h('span', { class: 'spinner', 'aria-hidden': 'true' }), 'Loading update status…'));

  // ---------------------------------------------------------------- data
  async function poll() {
    clearTimeout(timer);
    controller?.abort();
    controller = new AbortController();
    const mine = ++seq;
    try {
      const s = await api.updates(controller.signal);
      if (destroyed) return;
      loadError = null;
      apply(s, mine);
    } catch (e) {
      if (e.name === 'AbortError' || destroyed || e.status === 401) return;
      // The service stopping right at the end of a countdown (or mid-install) is expected.
      if (st && (st.phase === 'Installing' || (countdownRunning(st) && msUntilServer(st.restartAt) < 2000))) {
        startInstallWatch();
        return;
      }
      loadError = e;
      renderNotices();
    } finally {
      schedule();
    }
  }

  function apply(s, mine = ++seq) {
    if (!s || destroyed || mine < applied) return;
    applied = mine;
    st = s;
    if (pending && resultMatches(s.lastResult, pending)) {
      pending = null;
      sessionStorage.removeItem(PENDING_KEY);
      prevKeys.delete('notices'); // re-render so the outcome is announced
      resultArrived = true;
    }
    ctx.setUpdateMarker?.(s.available && s.phase !== 'Installing' ? s.available.version : null);
    render();
    if (s.phase === 'Installing') startInstallWatch();
  }

  function schedule() {
    clearTimeout(timer);
    if (destroyed || watching || document.visibilityState !== 'visible') return;
    const fast = Date.now() < fastUntil
      || (st && (st.phase === 'Checking' || st.phase === 'Downloading' || countdownRunning(st)));
    timer = setTimeout(poll, fast ? FAST_MS : pending ? CONFIRM_MS : loadError ? RETRY_MS : SLOW_MS);
  }

  const onVisibility = () => {
    if (document.visibilityState === 'visible') { poll(); startTicker(); } else {
      clearTimeout(timer);
      controller?.abort();
      stopTicker();
    }
  };
  document.addEventListener('visibilitychange', onVisibility);

  function startInstallWatch() {
    clearTimeout(timer);
    stopTicker();
    watchInstall({ version: st?.available?.version, from: st?.currentVersion });
  }

  // ---------------------------------------------------------------- actions
  async function check() {
    checked.textContent = 'Checking…';
    await busy(checkBtn, 'Checking…', async () => {
      try {
        const s = await api.checkUpdates();
        apply(s);
        if (s.error) return; // shown as a notice
        if (s.available && s.phase !== 'Checking') toast(`WatchTower ${s.available.version} is available.`, { kind: 'info' });
        else if (s.phase === 'UpToDate') toast(`WatchTower ${s.currentVersion} is up to date.`, { kind: 'info' });
      } catch (e) {
        if (e.status !== 401) toast(e.message, { kind: 'error' });
      }
    });
    renderChecked();
    syncCheckButton();
    schedule();
  }

  async function download(btn) {
    await busy(btn, 'Starting…', async () => {
      try {
        const r = await api.downloadUpdate();
        fastUntil = Date.now() + 5000;
        // The download runs on a background task, so the returned status can still say
        // Available: show it as started rather than flashing the button back.
        const s = r?.status;
        if (s) apply(s.phase === 'Available' && !s.error ? { ...s, phase: 'Downloading', progress: 0 } : s);
        focusPrimary();
      } catch (e) {
        if (e.status !== 401) toast(e.message, { kind: 'error' });
        poll();
      }
    });
    schedule();
  }

  async function install(btn) {
    await busy(btn, 'Starting…', async () => {
      try {
        await api.applyUpdate();
        startInstallWatch();
      } catch (e) {
        if (e.status !== 401) toast(e.message, { kind: 'error' });
        poll();
      }
    });
  }

  async function cancel(btn) {
    await busy(btn, 'Cancelling…', async () => {
      try {
        const s = await api.cancelUpdate();
        apply(s);
        toast('Restart cancelled. The update stays downloaded; install it when you are ready.', { kind: 'info' });
        focusPrimary();
      } catch (e) {
        if (e.status !== 401) toast(e.message, { kind: 'error' });
        poll();
      }
    });
    schedule();
  }

  // ---------------------------------------------------------------- rendering
  const patch = (name, data, build) => {
    const key = JSON.stringify(data ?? null);
    if (prevKeys.get(name) === key) return false;
    prevKeys.set(name, key);
    slots[name].replaceChildren(...[build(data)].flat().filter(Boolean));
    return true;
  };

  function render() {
    // When a rebuilt panel held focus, put it somewhere sensible instead of <body>.
    const hadFocus = root.contains(document.activeElement) && document.activeElement !== root;
    const s = st;
    const a = s.available;
    const asset = a?.assets?.[0];
    const showCountdown = countdownRunning(s) && s.canInstall;

    renderNotices();
    patch('countdown', showCountdown ? { at: s.restartAt, v: a?.version } : null, (d) => (d ? countdownPanel(d.v) : null));
    if (showCountdown) startTicker();
    else {
      stopTicker();
      if (cd.els) live.textContent = '';
      cd.els = null;
      cd.restartAt = null;
    }

    patch('hero', {
      phase: s.phase, cur: s.currentVersion, v: a?.version, pre: a?.prerelease, pub: a?.publishedAt, size: asset?.size,
      can: s.canInstall, why: s.cannotInstallReason, countdown: !!s.restartAt, console: isConsole, checked: !!s.lastCheckedAt,
    }, hero);
    patch('notes', a ? { v: a.version, notes: a.notes } : null, (d) => (d ? notesPanel(d) : null));
    updateProgress();
    renderChecked();
    syncCheckButton();
    tick();

    if (hadFocus && !root.contains(document.activeElement)) focusPrimary();
    if (resultArrived) {
      resultArrived = false;
      const active = document.activeElement;
      if (!active || active === document.body) slots.notices.querySelector('.update-result')?.focus();
    }
  }

  function renderNotices() {
    const s = st || {};
    patch('notices', {
      r: s.lastResult, e: s.error, phase: s.phase, can: s.canInstall, p: pending?.to, load: loadError?.message,
    }, () => {
      const out = [];
      if (loadError && !pending) {
        out.push(notice('critical', 'Cannot load the update status', `${loadError.message} Retrying automatically.`,
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => poll() }, icon('refresh'), 'Retry now'), { live: true }));
      }
      const waiting = pending && !resultMatches(s.lastResult, pending);
      if (waiting) {
        const n = notice('info', `Finishing the update to ${pending.to}`, loadError
          ? 'WatchTower is not answering right now: the installer may be restarting it or restoring the previous version. Retrying automatically.'
          : 'WatchTower is back. The installer is confirming that the new version started correctly; if it did not, the previous version is restored automatically. The result appears here.');
        n.querySelector('.notice-icon').replaceChildren(h('span', { class: 'spinner spinner-sm' }));
        out.push(n);
      }
      if (s.error) out.push(errorNotice(s));
      // While waiting for this install's verdict, an older result would only confuse.
      if (s.lastResult && !waiting) out.push(resultNotice(s.lastResult, resultArrived));
      return out;
    });
  }

  function errorNotice(s) {
    const msg = s.error;
    let title = 'Could not check for updates';
    let retry = () => check();
    let label = 'Try again';
    if (/^download failed/i.test(msg)) {
      title = 'The download failed';
      if (s.available && s.phase === 'Available') { retry = (e) => download(e.currentTarget); label = 'Retry download'; } else retry = null;
    } else if (/installer/i.test(msg)) {
      title = 'The update could not be installed';
      if (s.canInstall) { retry = (e) => install(e.currentTarget); label = 'Try again'; } else retry = null;
    }
    return notice('critical', title, msg,
      retry ? h('button', { class: 'btn btn-sm', type: 'button', onclick: retry }, icon('refresh'), label) : null, { live: true });
  }

  function resultNotice(r, announce) {
    const when = h('time', { datetime: r.completedAt }, `${fmtDateTime(r.completedAt)} (${fmtRelativeServer(r.completedAt)})`);
    let n;
    if (r.success) {
      n = notice('ok', `Updated to ${r.toVersion}`, [r.message || `Updated from ${r.fromVersion} to ${r.toVersion}.`]);
    } else {
      const msg = r.message || `The update to ${r.toVersion} failed.`;
      const restored = /restor|roll/i.test(msg) ? '' : ` The previous version (${r.fromVersion}) was restored.`;
      n = notice('critical', `Update to ${r.toVersion} did not complete`, [msg + restored]);
    }
    n.classList.add('update-result');
    n.tabIndex = -1;
    if (announce) n.setAttribute('role', r.success ? 'status' : 'alert');
    n.querySelector('.notice-body').append(h('p', { class: 'notice-time' }, 'Finished ', when));
    return n;
  }

  function hero(d) {
    const s = st;
    const a = s.available;
    const asset = a?.assets?.[0];
    const tag = phaseTag(s);
    const head = h('header', { class: 'panel-head' },
      h('h2', { id: 'h-update', class: 'panel-title', tabindex: '-1' }, h('span', { class: 'slash', 'aria-hidden': 'true' }, '/ '), 'This installation'),
      tag);

    const versions = h('div', { class: 'update-versions' },
      h('div', { class: 'ver' }, h('p', { class: 'ver-label' }, 'Installed'), h('p', { class: 'ver-num' }, s.currentVersion || '–')),
      a ? [
        h('span', { class: 'ver-arrow', 'aria-hidden': 'true' }, '→'),
        h('div', { class: 'ver' },
          h('p', { class: 'ver-label' }, 'Available', a.prerelease ? h('span', { class: 'badge badge-pre' }, 'Prerelease') : null),
          h('p', { class: 'ver-num is-new' }, a.version)),
        h('dl', { class: 'update-meta' },
          h('div', null, h('dt', null, 'Published'), h('dd', null, a.publishedAt ? fmtDateTime(a.publishedAt) : '–')),
          h('div', null, h('dt', null, 'Package'), h('dd', null, fmtBytes(asset?.size)))),
      ] : null);

    return [head, versions, h('div', { class: 'update-action flush' }, action(s, d))];
  }

  function action(s) {
    const a = s.available;
    const v = a?.version;
    switch (s.phase) {
      case 'Checking':
        return h('p', { class: 'update-busy' }, h('span', { class: 'spinner', 'aria-hidden': 'true' }), 'Checking the release feed…');
      case 'Downloading': {
        const fill = h('div', { class: 'bar-fill bar-accent' });
        const pct = h('span', { class: 'progress-pct' }, '0%');
        const label = h('p', { class: 'progress-label', id: 'dl-label' }, `Downloading WatchTower ${v}`);
        const bar = h('div', { class: 'bar progress-bar', role: 'progressbar', 'aria-labelledby': 'dl-label', 'aria-valuemin': '0', 'aria-valuemax': '100' }, fill);
        progressEls = { fill, pct, label, bar, v };
        return [
          h('div', { class: 'progress' }, label, h('div', { class: 'progress-row' }, bar, pct)),
          h('p', { class: 'muted small' }, 'After the package is verified, WatchTower restarts to install it after a short countdown that you can cancel.'),
        ];
      }
      case 'ReadyToInstall':
        if (!s.canInstall) {
          return [
            cannotInstall(s.cannotInstallReason),
            s.restartAt ? h('div', { class: 'btn-row' },
              h('button', { class: 'btn', type: 'button', onclick: (e) => cancel(e.currentTarget) }, 'Cancel the scheduled restart')) : null,
          ];
        }
        if (s.restartAt) {
          return h('p', { class: 'muted' }, `WatchTower ${v} is downloaded and verified. The restart that installs it is scheduled above.`);
        }
        return [
          h('p', null, `WatchTower ${v} is downloaded and verified.`),
          h('div', { class: 'btn-row' },
            h('button', { class: 'btn btn-primary', type: 'button', dataset: { primary: '' }, onclick: (e) => install(e.currentTarget) },
              icon('restart'), 'Install now')),
          h('p', { class: 'muted small' }, 'Installing restarts the WatchTower service. Monitoring pauses for about a minute and this console reconnects on its own. If the new version does not start correctly, the previous version is restored automatically.'),
        ];
      case 'Installing':
        return h('p', { class: 'update-busy' }, h('span', { class: 'spinner', 'aria-hidden': 'true' }), `Installing WatchTower ${v || ''}…`);
      default:
        break;
    }
    if (a) {
      if (isConsole) return cannotInstall('Updates can only be installed while WatchTower runs as a Windows service. Install the new version with its installer, or run WatchTower as a service.');
      return [
        h('div', { class: 'btn-row' },
          h('button', { class: 'btn btn-primary', type: 'button', dataset: { primary: '' }, onclick: (e) => download(e.currentTarget) },
            icon('download'), 'Download update')),
        h('p', { class: 'muted small' }, 'The package is downloaded and its checksum (and signature, when configured) verified. WatchTower then restarts to install it after a short countdown that you can cancel.'),
      ];
    }
    if (s.phase === 'Failed') {
      return h('p', { class: 'muted' }, 'The last check did not finish. Try again with Check for updates.');
    }
    if (!s.lastCheckedAt) {
      return h('p', { class: 'muted' }, 'WatchTower has not checked for updates yet. Use Check for updates, or turn on automatic checks in ',
        h('a', { href: '#/config/updates' }, 'update settings'), '.');
    }
    return h('p', { class: 'empty-good' }, h('span', { class: 'tag-glyph', 'aria-hidden': 'true' }, '■'), `WatchTower ${s.currentVersion} is the latest version`);
  }

  function cannotInstall(reason) {
    return h('p', { class: 'notice-inline' }, icon('info'), h('span', null, reason || 'This update cannot be installed from here.'));
  }

  function notesPanel({ v, notes }) {
    const text = (notes || '').replace(/\r\n?/g, '\n').trim();
    return h('section', { class: 'panel update-notes', 'aria-labelledby': 'h-notes' },
      h('header', { class: 'panel-head' },
        h('h2', { id: 'h-notes', class: 'panel-title' }, h('span', { class: 'slash', 'aria-hidden': 'true' }, '/ '), `What's new in ${v}`)),
      // Plain text on purpose: notes come from the release feed and must never become markup.
      text
        ? h('div', { class: 'release-notes flush', tabindex: '0', role: 'region', 'aria-label': `Release notes for ${v}`, text })
        : h('p', { class: 'empty' }, 'This release has no notes.'));
  }

  function countdownPanel(v) {
    const num = h('span', { class: 'countdown-value' });
    const titleNum = h('span', { class: 'nowrap' });
    const title = h('h2', { id: 'h-countdown', class: 'countdown-title', tabindex: '-1' }, `Restarting to install ${v} in `, titleNum);
    const fill = h('div', { class: 'countdown-fill' });
    const restartNow = h('button', { class: 'btn btn-primary', type: 'button', onclick: (e) => install(e.currentTarget) }, icon('restart'), 'Restart now');
    const cancelBtn = h('button', { class: 'btn', type: 'button', onclick: (e) => cancel(e.currentTarget) }, 'Cancel');
    const note = h('p', { class: 'countdown-note' }, 'Monitoring pauses for about a minute while WatchTower installs the update and restarts. Cancel to install it later.');
    cd.els = { num, titleNum, title, fill, restartNow, cancelBtn, note, v };
    if (cd.restartAt !== st.restartAt) {
      cd.restartAt = st.restartAt;
      cd.total = Math.max(DEFAULT_COUNTDOWN_S, secondsLeft(st.restartAt));
      cd.lastSec = -1;
      cd.announcedAt = 0;
    }
    return h('section', { class: 'update-countdown', 'aria-labelledby': 'h-countdown' },
      h('div', { class: 'countdown-body' },
        h('p', { class: 'countdown-num', 'aria-hidden': 'true' }, num, h('span', { class: 'countdown-unit' }, 's')),
        h('div', { class: 'countdown-text' },
          statusTag('warning', 'Restart scheduled'),
          title, note,
          h('div', { class: 'btn-row' }, restartNow, cancelBtn))),
      h('div', { class: 'countdown-bar', 'aria-hidden': 'true' }, fill));
  }

  function tick() {
    const els = cd.els;
    if (!els || !st?.restartAt) return;
    const ms = msUntilServer(st.restartAt);
    const sec = Number.isNaN(ms) ? 0 : Math.max(0, Math.ceil(ms / 1000));
    els.fill.style.transform = `scaleX(${Math.max(0, Math.min(1, ms / 1000 / cd.total))})`;
    if (sec === cd.lastSec) return;
    cd.lastSec = sec;
    els.num.textContent = String(sec);
    if (sec > 0) {
      els.titleNum.textContent = `${sec} s`;
      // Screen readers hear the first value, then every 10 s and the last 5 s — never more
      // often than every 5 s.
      const now = Date.now();
      if (!cd.announcedAt || ((sec % 10 === 0 || sec === 5) && now - cd.announcedAt >= ANNOUNCE_GAP_MS)) {
        live.textContent = `Restarting to install WatchTower ${els.v} in ${sec} seconds.`;
        cd.announcedAt = now;
      }
    } else {
      els.title.textContent = `Installing WatchTower ${els.v} now…`;
      els.note.textContent = 'The service is restarting. This console reconnects on its own.';
      els.restartNow.disabled = true;
      els.cancelBtn.disabled = true;
      live.textContent = `Installing WatchTower ${els.v} now.`;
      // The service may stop before it answers again: poll at once so the overlay appears.
      clearTimeout(timer);
      timer = setTimeout(poll, 600);
    }
  }

  function startTicker() {
    if (ticker || !cd.els || document.visibilityState !== 'visible') return;
    ticker = setInterval(tick, TICK_MS);
  }
  function stopTicker() { clearInterval(ticker); ticker = 0; }

  function updateProgress() {
    if (!progressEls || st.phase !== 'Downloading') { progressEls = null; return; }
    const p = isNum(st.progress) ? Math.max(0, Math.min(1, st.progress)) : 0;
    const pct = Math.floor(p * 100);
    progressEls.fill.style.width = `${p * 100}%`;
    progressEls.pct.textContent = `${pct}%`;
    progressEls.bar.setAttribute('aria-valuenow', String(pct));
    progressEls.bar.setAttribute('aria-valuetext', p >= 1 ? 'Verifying' : `${pct} percent`);
    progressEls.label.textContent = p >= 1 ? `Verifying WatchTower ${progressEls.v}…` : `Downloading WatchTower ${progressEls.v}`;
  }

  function renderChecked() {
    if (!st) return;
    if (st.phase === 'Checking') { checked.textContent = 'Checking…'; checked.removeAttribute('title'); return; }
    if (st.lastCheckedAt) {
      checked.textContent = `Last checked ${fmtRelativeServer(st.lastCheckedAt)}`;
      checked.title = fmtDateTime(st.lastCheckedAt);
    } else {
      checked.textContent = 'Not checked yet';
      checked.removeAttribute('title');
    }
  }

  function syncCheckButton() {
    if (checkBtn.getAttribute('aria-busy') === 'true') return;
    const p = st?.phase;
    checkBtn.disabled = !st || p === 'Checking' || p === 'Downloading' || p === 'Installing';
  }

  /** Focus the most useful thing on the page: countdown, primary action, or the panel heading. */
  function focusPrimary() {
    const target = slots.countdown.querySelector('#h-countdown')
      || slots.hero.querySelector('[data-primary]')
      || slots.hero.querySelector('#h-update');
    target?.focus({ preventScroll: false });
  }

  function phaseTag(s) {
    switch (s.phase) {
      case 'Checking': return statusTag('info', 'Checking');
      case 'Available': return statusTag('info', 'Update available');
      case 'Downloading': return statusTag('info', 'Downloading');
      case 'ReadyToInstall': return s.restartAt && s.canInstall ? statusTag('warning', 'Restart scheduled') : statusTag('info', 'Ready to install');
      case 'Installing': return statusTag('warning', 'Installing');
      case 'Failed': return statusTag('critical', 'Check failed');
      case 'UpToDate': return statusTag('ok', 'Up to date');
      default: return s.available ? statusTag('info', 'Update available')
        : s.lastCheckedAt ? statusTag('ok', 'Up to date') : statusTag('stopped', 'Not checked');
    }
  }

  // ---------------------------------------------------------------- start
  api.status().then((s) => {
    if (destroyed) return;
    ctx.onStatus(s);
    const c = s?.agent?.isWindowsService === false;
    if (c !== isConsole) { isConsole = c; if (st) { prevKeys.delete('hero'); render(); } }
  }).catch(() => { /* the update status reports connection problems */ });

  // Keep "last checked 3 minutes ago" fresh between slow polls.
  const relTimer = setInterval(renderChecked, 30_000);
  poll();

  return {
    unmount() {
      destroyed = true;
      clearTimeout(timer);
      clearInterval(relTimer);
      stopTicker();
      controller?.abort();
      document.removeEventListener('visibilitychange', onVisibility);
    },
  };
}

function howItWorks() {
  const step = (title, text) => h('li', null, h('p', { class: 'strong' }, title), h('p', { class: 'muted' }, text));
  return h('aside', { class: 'panel updates-side', 'aria-labelledby': 'h-how' },
    h('header', { class: 'panel-head' },
      h('h2', { id: 'h-how', class: 'panel-title' }, h('span', { class: 'slash', 'aria-hidden': 'true' }, '/ '), 'How updates work')),
    h('ol', { class: 'steps' },
      step('Check', 'WatchTower checks its release feed on a schedule, or when you click Check for updates.'),
      step('Download and verify', 'The package is checked against its published SHA-256 checksum, and its signature when a signing key is configured.'),
      step('Restart', 'After a countdown that anyone can cancel, the service restarts into the new version.'),
      step('Health check', 'If the new version does not report healthy in time, the previous version is restored automatically.')),
    h('p', { class: 'side-foot' }, h('a', { href: '#/config/updates' }, 'Update settings'), ' — schedule, release channel, automatic download and countdown length.'));
}
