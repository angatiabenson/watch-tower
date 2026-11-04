// Dashboard: live status from GET /api/status, refreshed every 10 s while the tab is visible.
import {
  h, icon, statusTag, fmtPct, fmt1, fmt0, fmtGb, fmtMbAsGb, fmtDateTime, fmtRelative, fmtTime,
  fmtDuration, humanize, isNum, plural,
} from '../dom.js';
import { api, msUntilServer } from '../api.js';
import { notice, restartButton, copyButton, busy, toast } from '../ui.js';
import { stripChart, usageBar } from '../chart.js';
import { watchInstall, secondsLeft, countdownRunning } from './updates.js';
import { maintenanceBanner, startMaintenance } from './maintenance.js';

const REFRESH_MS = 10_000;
// One-time notice after an install: dismissal is remembered per completedAt.
const RESULT_SEEN_KEY = 'wt.updateResultSeen';
const RESULT_MAX_AGE_MS = 7 * 86400e3;

export function mount(root, _params, ctx) {
  let timer = 0;
  let controller = null;
  let destroyed = false;
  let lastOk = null;
  const prevKeys = new Map();
  let lastStatus = null;
  let updTicker = 0;
  let updEls = null; // parts of the live countdown notice

  const updated = h('span', { class: 'muted small' }, 'Loading…');
  const refreshBtn = h('button', { class: 'btn btn-sm', type: 'button', onclick: () => refresh() }, icon('refresh'), 'Refresh');
  // Hidden while a window is active: the banner then offers "Change…" and "End maintenance".
  const maintBtn = h('button', { class: 'btn btn-sm', type: 'button', hidden: true }, icon('pause'), 'Start maintenance');
  maintBtn.addEventListener('click', async () => {
    if (await startMaintenance()) await afterMaintenanceChange();
  });
  // The button that was used disappears with the re-render: move focus to its counterpart.
  async function afterMaintenanceChange() {
    await refresh();
    const target = maintBtn.hidden ? slots.maint.querySelector('button') : maintBtn;
    target?.focus();
  }
  const errorSlot = h('div');
  const slots = {
    maint: h('div', { class: 'dash-maint' }),
    update: h('div', { class: 'dash-update' }),
    health: h('div', { class: 'dash-health' }),
    strip: h('section', { class: 'stats-strip', 'aria-label': 'Summary' }),
    alerts: h('section', { class: 'panel dash-alerts', 'aria-labelledby': 'h-alerts' }),
    readouts: h('section', { class: 'readouts', 'aria-label': 'Processor and memory' }),
    agent: h('section', { class: 'panel dash-agent', 'aria-labelledby': 'h-agent' }),
    disks: h('section', { class: 'panel dash-disks', 'aria-labelledby': 'h-disks' }),
    endpoints: h('section', { class: 'panel dash-endpoints', 'aria-labelledby': 'h-endpoints', hidden: true }),
    services: h('section', { class: 'panel dash-services', 'aria-labelledby': 'h-services' }),
    pools: h('section', { class: 'panel dash-pools', 'aria-labelledby': 'h-pools' }),
    io: h('section', { class: 'panel dash-io', 'aria-labelledby': 'h-io' }),
    incidents: h('section', { class: 'panel dash-incidents', 'aria-labelledby': 'h-incidents' }),
  };

  ctx.setActions(updated, maintBtn, refreshBtn);
  // Announces a scheduled restart once (the ticking countdown title itself is not live).
  const live = h('p', { class: 'sr-only', 'aria-live': 'polite', 'aria-atomic': 'true' });
  root.append(
    live,
    errorSlot,
    slots.maint,
    slots.update,
    slots.health,
    slots.strip,
    slots.alerts,
    h('div', { class: 'dash-grid' },
      slots.readouts, slots.agent,
      // Disks and the (optional) endpoint checks share one grid area, so a server without
      // endpoint checks gets no empty row.
      h('div', { class: 'dash-stack' }, slots.disks, slots.endpoints),
      slots.incidents, slots.services, slots.pools, slots.io));

  // Rebuild a slot only when its data changed: keeps focus stable and stops screen
  // readers from re-announcing unchanged banners every 10 seconds.
  const patch = (name, data, build) => {
    const key = JSON.stringify(data ?? null);
    if (prevKeys.get(name) === key) return false;
    prevKeys.set(name, key);
    slots[name].replaceChildren(...[build(data)].flat().filter(Boolean));
    return true;
  };

  async function refresh() {
    clearTimeout(timer);
    controller?.abort();
    controller = new AbortController();
    try {
      const s = await api.status(controller.signal);
      if (destroyed) return;
      lastOk = new Date();
      errorSlot.replaceChildren();
      render(s);
      ctx.onStatus(s);
      updated.textContent = `Updated ${fmtTime(lastOk.toISOString(), true)}`;
    } catch (e) {
      if (e.name === 'AbortError' || destroyed) return;
      if (e.status === 401) return;
      // The service stopping at the end of an update countdown (or mid-install) is expected.
      const u = lastStatus?.update;
      if (u && (u.phase === 'Installing' || (countdownRunning(u) && msUntilServer(u.restartAt) < 2000))) {
        stopUpdateTicker();
        watchInstall({ version: u.availableVersion, from: lastStatus.agent?.version });
        return;
      }
      errorSlot.replaceChildren(notice('critical', 'Live status is unavailable',
        lastOk ? `${e.message} Showing data from ${fmtTime(lastOk.toISOString(), true)}.` : e.message, null, { live: true }));
      updated.textContent = lastOk ? `Last updated ${fmtTime(lastOk.toISOString(), true)}` : 'Not loaded';
    } finally {
      schedule();
    }
  }

  function schedule() {
    clearTimeout(timer);
    if (!destroyed && document.visibilityState === 'visible') timer = setTimeout(refresh, REFRESH_MS);
  }

  const onVisibility = () => {
    if (document.visibilityState === 'visible') {
      refresh();
      if (updEls) startUpdateTicker();
    } else {
      clearTimeout(timer);
      controller?.abort();
      stopUpdateTicker();
    }
  };
  document.addEventListener('visibilitychange', onVisibility);

  function render(s) {
    const m = s.metrics || {};
    lastStatus = s;
    const maint = s.maintenance && s.maintenance.active ? s.maintenance : null;
    // Older agents send no maintenance block: offer nothing rather than a failing action.
    maintBtn.hidden = !s.maintenance || !!maint;
    patch('maint', maint, (d) => (d ? maintenanceBanner(d, { onChanged: afterMaintenanceChange }) : null));
    renderUpdateSlot();
    const workers = Array.isArray(s.workers) ? s.workers : [];
    // Only stalled workers go into the key: healthy ones change lastSuccessAt every few seconds.
    const stale = workers.filter((w) => w.stale).map((w) => ({ name: w.name, at: w.lastSuccessAt, every: w.intervalSeconds }));
    patch('health', { health: s.health, restart: s.restartPending, stale }, renderHealth);
    patch('strip', { svc: m.services, pools: m.appPools, disks: m.disks, inc: s.incidents }, renderStrip);
    // Minute bucket: the "open for" durations advance without rebuilding on every refresh.
    if (Array.isArray(s.alerts)) {
      patch('alerts', { alerts: s.alerts, held: !!maint, minute: s.alerts.length ? Math.floor(Date.now() / 60000) : 0 }, renderAlerts);
    } else {
      slots.alerts.hidden = true; // older agent without the alerts list
    }
    patch('readouts', { cpu: m.cpu, ram: m.ram }, renderReadouts);
    // Uptime in whole minutes so the panel is not rebuilt on every tick.
    patch('agent', {
      agent: { ...s.agent, uptimeSeconds: undefined, uptimeMinutes: Math.floor((s.agent?.uptimeSeconds ?? 0) / 60) },
      portal: s.portal, email: s.email,
      workers: Array.isArray(s.workers) ? { total: workers.length, stale: stale.length } : null,
    }, renderAgent);
    patch('disks', m.disks, renderDisks);
    const endpoints = Array.isArray(m.endpoints) ? m.endpoints : [];
    slots.endpoints.hidden = !endpoints.length;
    patch('endpoints', endpoints, renderEndpoints);
    patch('services', m.services, renderServices);
    patch('pools', m.appPools, renderPools);
    patch('io', { q: m.diskQueues, n: m.network }, renderIo);
    patch('incidents', s.incidents, renderIncidents);
  }

  // ---------------------------------------------------------------- update notice
  function resultSeen(r) {
    try { return localStorage.getItem(RESULT_SEEN_KEY) === r.completedAt; } catch { return false; }
  }

  function renderUpdateSlot() {
    const s = lastStatus;
    const u = s?.update || null;
    const r = u?.lastResult;
    const showResult = !!(r && !resultSeen(r) && Date.now() - Date.parse(r.completedAt) < RESULT_MAX_AGE_MS);
    // A console-mode agent cannot install, so its countdown would never end in a restart.
    const canRestart = s?.agent?.isWindowsService !== false;
    const changed = patch('update', {
      phase: u?.phase, v: u?.availableVersion, at: countdownRunning(u) && canRestart ? u.restartAt : null,
      r: showResult ? r : null, cur: s?.agent?.version, console: !canRestart,
    }, (d) => renderUpdate(d, u));
    if (changed) {
      live.textContent = updEls
        ? `WatchTower will restart in ${secondsLeft(u.restartAt)} seconds to install version ${updEls.v}.`
        : '';
    }
    if (u?.phase === 'Installing') watchInstall({ version: u.availableVersion, from: s.agent?.version });
    if (updEls) startUpdateTicker(); else stopUpdateTicker();
  }

  function renderUpdate(d, u) {
    updEls = null;
    if (!u) return null;
    const out = [];
    const v = d.v;
    const view = (label = 'View update') => h('a', { class: 'btn btn-sm', href: '#/updates' }, label);
    if (d.at) {
      const cancelBtn = h('button', { class: 'btn btn-sm', type: 'button', onclick: (e) => cancelUpdate(e.currentTarget) }, 'Cancel');
      const n = notice('warning', `Restarting to install ${v}`,
        'Monitoring pauses for about a minute while WatchTower installs the update.', [cancelBtn, view('Details')]);
      updEls = { title: n.querySelector('.notice-title'), cancelBtn, v, lastSec: -1 };
      tickUpdate();
      out.push(n);
    } else if (d.phase === 'Installing') {
      out.push(notice('warning', `Installing WatchTower ${v || 'update'}…`, 'The service is restarting. This console reconnects on its own.'));
    } else if (v && d.phase === 'Downloading') {
      out.push(notice('info', `Downloading WatchTower ${v}`, null, view()));
    } else if (v && d.phase === 'ReadyToInstall') {
      out.push(notice('info', `WatchTower ${v} is ready to install`, d.console
        ? 'The update is downloaded, but it can only be installed while WatchTower runs as a Windows service.'
        : 'The update is downloaded and verified.', view()));
    } else if (v) {
      out.push(notice('info', `WatchTower ${v} is available`, d.cur ? `This server runs ${d.cur}.` : null, view()));
    }
    if (d.r) out.push(resultNotice(d.r));
    return out;
  }

  function resultNotice(r) {
    const dismiss = h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Dismiss update result', title: 'Dismiss' }, icon('close'));
    dismiss.addEventListener('click', () => {
      try { localStorage.setItem(RESULT_SEEN_KEY, r.completedAt); } catch { /* storage blocked: hide until the next refresh */ }
      prevKeys.delete('update');
      slots.update.replaceChildren(...[...slots.update.children].filter((c) => !c.contains(dismiss)));
      root.focus({ preventScroll: true });
    });
    const when = `Finished ${fmtDateTime(r.completedAt)}.`;
    if (r.success) {
      return notice('ok', `Updated to ${r.toVersion}`, `${r.message || `Updated from ${r.fromVersion} to ${r.toVersion}.`} ${when}`, dismiss);
    }
    const msg = r.message || `The update to ${r.toVersion} failed.`;
    const restored = /restor|roll/i.test(msg) ? '' : ` The previous version (${r.fromVersion}) was restored.`;
    return notice('critical', `Update to ${r.toVersion} did not complete`, `${msg}${restored} ${when}`,
      [h('a', { class: 'btn btn-sm', href: '#/updates' }, 'Details'), dismiss]);
  }

  function tickUpdate() {
    const u = lastStatus?.update;
    if (!updEls || !u?.restartAt) return;
    const sec = secondsLeft(u.restartAt);
    if (sec === updEls.lastSec) return;
    updEls.lastSec = sec;
    if (sec > 0) {
      updEls.title.textContent = `Restarting to install ${updEls.v} in ${sec} s`;
    } else {
      updEls.title.textContent = `Installing WatchTower ${updEls.v} now…`;
      updEls.cancelBtn.disabled = true;
      clearTimeout(timer);
      timer = setTimeout(refresh, 1500); // a failed refresh here opens the install progress
    }
  }

  function startUpdateTicker() {
    if (!updTicker && document.visibilityState === 'visible') updTicker = setInterval(tickUpdate, 250);
  }
  function stopUpdateTicker() { clearInterval(updTicker); updTicker = 0; }

  async function cancelUpdate(btn) {
    await busy(btn, 'Cancelling…', async () => {
      try {
        await api.cancelUpdate();
        toast('Restart cancelled. Install the update from the Updates page when you are ready.', { kind: 'info' });
      } catch (e) {
        if (e.status !== 401) toast(e.message, { kind: 'error' });
      }
    });
    await refresh();
    (slots.update.querySelector('a, button') || root).focus({ preventScroll: true });
  }

  refresh();

  return {
    unmount() {
      destroyed = true;
      stopUpdateTicker();
      clearTimeout(timer);
      controller?.abort();
      document.removeEventListener('visibilitychange', onVisibility);
    },
  };
}

// ------------------------------------------------------------------ sections

function panelHead(id, title, extra) {
  return h('header', { class: 'panel-head' },
    h('h2', { id, class: 'panel-title' }, h('span', { class: 'slash', 'aria-hidden': 'true' }, '/ '), title),
    extra || null);
}

function emptyState(text, link) {
  return h('p', { class: 'empty' }, text, link ? [' ', h('a', { href: link.href }, link.label)] : null);
}

const shortDtf = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });

/** "10:42" today, otherwise a short date and time. */
function whenText(iso) {
  const d = iso ? new Date(iso) : null;
  if (!d || Number.isNaN(d.getTime())) return null;
  const now = new Date();
  const today = d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth() && d.getDate() === now.getDate();
  return today ? fmtTime(iso) : shortDtf.format(d);
}

/**
 * Snapshot entries (drives, disks, adapters, services, pools, endpoints) carry `isStale` from the
 * agent: the last reading is older than its freshness window (the resource is still listed but no
 * longer read). They keep that reading, shown grey with the time it was taken.
 */
function staleText(readAt) {
  const at = whenText(readAt);
  return at ? `Stale since ${at}` : 'Stale';
}

function staleTag(readAt) {
  return statusTag('stale', staleText(readAt));
}

const staleTitle = (readAt) => (readAt ? `No new reading since ${fmtDateTime(readAt)}; showing the last one` : null);

function staleWorkerLine(w) {
  const at = whenText(w.at);
  const every = isNum(w.every) && w.every > 0 ? `, expected every ${fmtDuration(w.every)}` : '';
  return at ? `${w.name}: last successful check at ${at}${every}` : `${w.name}: no successful check since WatchTower started${every}`;
}

function renderHealth({ health, restart, stale }) {
  const errors = health?.errors || [];
  const warnings = health?.warnings || [];
  const out = [];
  if (restart) out.push(notice('warning', 'Restart needed', 'Some saved settings take effect only after WatchTower restarts.', restartButton()));
  if (errors.length) out.push(notice('critical', errors.length === 1 ? 'WatchTower has a problem' : `WatchTower has ${errors.length} problems`, errors, null, { live: true }));
  if (stale?.length) {
    out.push(notice('critical',
      stale.length === 1 ? 'A monitoring worker has stalled' : `${stale.length} monitoring workers have stalled`,
      [...stale.map(staleWorkerLine), 'Those checks are not running, and the heartbeat reports a failure until they recover.'],
      h('a', { class: 'btn btn-sm', href: '#/logs' }, 'Open logs')));
  }
  if (warnings.length) out.push(notice('warning', warnings.length === 1 ? '1 warning' : `${warnings.length} warnings`, warnings,
    h('a', { class: 'btn btn-sm', href: '#/config' }, 'Open configuration')));
  if (!out.length) out.push(h('p', { class: 'all-clear' }, h('span', { class: 'tag-glyph', 'aria-hidden': 'true' }, '■'), 'All checks are passing'));
  return out;
}

function readout(kind, title, metric, extraLine) {
  const box = h('div', { class: 'readout' });
  const head = h('h2', { class: 'readout-title' }, h('span', { class: 'slash', 'aria-hidden': 'true' }, '/ '), title);
  if (!metric || !isNum(metric.current)) {
    box.append(head, h('p', { class: 'readout-empty' }, 'Waiting for the first sample…'));
    return box;
  }
  const over = isNum(metric.threshold) && metric.current >= metric.threshold;
  box.append(
    head,
    h('div', { class: 'readout-figures' },
      h('p', { class: `readout-value${over ? ' over' : ''}` },
        fmt1(metric.current), h('span', { class: 'readout-unit' }, '%'),
        over ? h('span', { class: 'readout-flag' }, h('span', { 'aria-hidden': 'true' }, '■'), 'Above alert threshold') : null),
      h('dl', { class: 'readout-stats' },
        h('div', null, h('dt', null, 'Average'), h('dd', null, fmtPct(metric.average))),
        h('div', null, h('dt', null, 'Peak'), h('dd', null, fmtPct(metric.max))),
        h('div', null, h('dt', null, 'Alert at'), h('dd', null, fmtPct(metric.threshold))))),
    extraLine ? h('p', { class: 'readout-extra' }, extraLine) : null,
    stripChart({ series: metric.series, threshold: metric.threshold, label: `${title} usage, last hour` }));
  box.dataset.kind = kind;
  return box;
}

function stat(label, value, sub, kind) {
  return h('div', { class: 'stat' },
    h('p', { class: 'stat-label' }, label),
    h('p', { class: `stat-value${kind ? ` is-${kind}` : ''}` }, value),
    h('p', { class: 'stat-sub' }, sub || ' '));
}

function upDown(list, noneText) {
  const a = Array.isArray(list) ? list : [];
  const down = a.filter((x) => !x.isRunning).length;
  const stale = a.filter((x) => x.isStale).length;
  if (!a.length) return ['–', h('span', { class: 'muted' }, noneText), null];
  return [`${a.length - down}/${a.length}`,
    down ? statusTag('critical', `${down} down`) : stale ? statusTag('stale', `${stale} stale`) : statusTag('ok', 'All running'),
    down ? 'critical' : null];
}

/** Stats strip: one large numeral per watched area, split by 1 px rules. */
function renderStrip({ svc, pools, disks, inc }) {
  const d = Array.isArray(disks) ? disks : [];
  const crit = d.filter((x) => isNum(x.criticalPercent) && x.usagePercent >= x.criticalPercent).length;
  const warn = d.filter((x) => isNum(x.warningPercent) && x.usagePercent >= x.warningPercent).length - crit;
  const list = Array.isArray(inc) ? inc : [];
  const ic = list.filter((x) => x.severity === 'critical').length;
  return [
    stat('Services up', ...upDown(svc, 'None watched')),
    stat('App pools up', ...upDown(pools, 'None watched')),
    d.length
      ? stat('Disks at risk', String(crit + warn),
        crit ? statusTag('critical', `${crit} critical`) : warn ? statusTag('warning', `${warn} low on space`) : statusTag('ok', 'All healthy'),
        crit ? 'critical' : warn ? 'warning' : null)
      : stat('Disks at risk', '–', h('span', { class: 'muted' }, 'No drives sampled')),
    stat('Incidents, 24 h', String(list.length),
      ic ? statusTag('critical', `${ic} critical`) : list.length ? statusTag('info', 'None critical') : statusTag('ok', 'Quiet'),
      ic ? 'critical' : null),
  ];
}

function renderReadouts({ cpu, ram }) {
  const ramLine = ram && isNum(ram.totalMb)
    ? `${fmtMbAsGb(ram.availableMb)} free of ${fmtMbAsGb(ram.totalMb)}`
    : null;
  return [readout('cpu', 'Processor', cpu, 'Last 60 minutes'), readout('ram', 'Memory', ram, ramLine)];
}

function renderAgent({ agent: a = {}, portal, email, workers }) {
  const row = (k, v, cls) => h('div', null, h('dt', null, k), h('dd', { class: cls }, v ?? '–'));
  const portalText = portal?.enabled
    ? [statusTag('ok', 'Connected'), ' ', h('span', { class: 'break' }, portal.baseUrl || ''), portal.protocol ? ` (${portal.protocol})` : '']
    : [h('span', { class: 'muted' }, 'Not connected '), h('a', { href: '#/config/portal' }, 'Set up')];
  const emailText = email?.configured
    ? `Configured, ${plural(email.recipients ?? 0, 'recipient')}`
    : [statusTag('warning', 'Not configured'), ' ', h('a', { href: '#/config/email' }, 'Set up email')];
  return [
    panelHead('h-agent', 'This server'),
    h('dl', { class: 'facts' },
      row('Machine', a.machineName, 'strong'),
      row('Console address', [h('code', { class: 'addr' }, location.origin),
        copyButton(() => location.origin, { what: 'Console address' })], 'addr-row'),
      row('Operating system', a.os),
      row('WatchTower', a.version ? `Version ${a.version}` : null),
      row('Runs as', a.isWindowsService ? `Windows service “${a.serviceName}”` : 'Console process (not a service)'),
      row('Uptime', isNum(a.uptimeMinutes) ? fmtDuration(a.uptimeMinutes * 60) : null),
      workers ? row('Monitoring', !workers.total
        ? h('span', { class: 'muted' }, 'Starting…')
        : workers.stale
          ? statusTag('critical', `${workers.stale} of ${workers.total} stalled`)
          : statusTag('ok', `All ${workers.total} workers running`)) : null,
      row('Started', a.startedAt ? fmtDateTime(a.startedAt) : null),
      row('Process ID', isNum(a.processId) ? String(a.processId) : null),
      row('Email alerts', emailText),
      row('Visibility Portal', portalText),
      row('Data folder', a.dataDirectory, 'path'),
      row('Install folder', a.installDirectory, 'path')),
  ];
}

function renderDisks(disks) {
  const head = panelHead('h-disks', 'Disk space');
  if (!Array.isArray(disks) || !disks.length) return [head, emptyState('No drives sampled yet.')];
  return [head, h('ul', { class: 'disk-list' }, disks.map((d) => {
    const { bar, level } = usageBar({
      percent: d.usagePercent, warning: d.warningPercent, critical: d.criticalPercent, label: `Drive ${d.drive} usage`,
    });
    return h('li', { class: `disk${d.isStale ? ' is-stale' : ''}`, title: d.isStale ? staleTitle(d.readAt) : null },
      h('div', { class: 'disk-head' },
        h('span', { class: 'disk-drive' }, d.drive),
        d.isStale ? staleTag(d.readAt) : null,
        level !== 'ok' ? statusTag(level, level === 'critical' ? 'Critical' : 'Low space') : null,
        h('span', { class: 'disk-pct' }, fmtPct(d.usagePercent), h('span', { class: 'muted' }, ' used'))),
      bar,
      h('p', { class: 'disk-meta' }, `${fmtGb(d.freeGb)} free of ${fmtGb(d.totalGb)}`,
        h('span', { class: 'muted' }, `Warning at ${fmt0(d.warningPercent)}%, critical at ${fmt0(d.criticalPercent)}%`)));
  }))];
}

function statusList(items, nameOf, subOf) {
  const sorted = [...items].sort((a, b) => Number(a.isRunning) - Number(b.isRunning) || nameOf(a).localeCompare(nameOf(b)));
  return h('ul', { class: 'status-list' }, sorted.map((it) => h('li', {
    class: [it.isRunning ? '' : 'down', it.isStale ? 'is-stale' : ''].filter(Boolean).join(' '),
    title: it.isStale ? staleTitle(it.readAt) : null,
  },
  h('div', { class: 'status-name' },
    h('span', { class: 'strong' }, nameOf(it)),
    subOf(it) ? h('span', { class: 'muted small' }, subOf(it)) : null),
  h('span', { class: 'status-tags' },
    it.isStale ? staleTag(it.readAt) : null,
    // A stale entry's status is its last reading: grey, not an alarm.
    statusTag(it.isStale ? 'stale' : it.isRunning ? 'ok' : 'critical', it.status || (it.isRunning ? 'Running' : 'Stopped'))))));
}

function countLine(items) {
  const down = items.filter((i) => !i.isRunning).length;
  const stale = items.filter((i) => i.isStale).length;
  const text = down ? `${down} of ${items.length} down` : `${items.length} watched`;
  return h('span', { class: 'panel-meta' }, stale ? `${text}, ${stale} stale` : text);
}

function renderServices(services) {
  if (!Array.isArray(services) || !services.length) {
    return [panelHead('h-services', 'Windows services'),
      emptyState('No services are being watched.', { href: '#/config/services', label: 'Choose services to watch' })];
  }
  return [panelHead('h-services', 'Windows services', countLine(services)),
    statusList(services, (s) => s.displayName || s.name, (s) => (s.displayName && s.displayName !== s.name ? s.name : ''))];
}

function renderPools(pools) {
  if (!Array.isArray(pools) || !pools.length) {
    return [panelHead('h-pools', 'IIS application pools'),
      emptyState('No application pools are being watched.', { href: '#/config/apppools', label: 'Choose pools to watch' })];
  }
  return [panelHead('h-pools', 'IIS application pools', countLine(pools)), statusList(pools, (p) => p.name, () => '')];
}

function renderIo({ q, n }) {
  const table = (caption, cols, rows) => h('table', { class: 'table compact' },
    h('caption', null, caption),
    h('thead', null, h('tr', null, cols.map((c, i) => h('th', { scope: 'col', class: i ? 'num' : '' }, c)))),
    h('tbody', null, rows));
  const out = [panelHead('h-io', 'Queues')];
  const row = (d, name, value) => h('tr', { class: d.isStale ? 'is-stale' : null, title: d.isStale ? staleTitle(d.readAt) : null },
    h('td', null, name, d.isStale ? h('span', { class: 'stale-note' }, ` · ${staleText(d.readAt)}`) : null),
    h('td', { class: 'num' }, value));
  out.push(Array.isArray(q) && q.length
    ? table('Disk I/O queue', ['Disk', 'Queue length'], q.map((d) => row(d, d.instance, fmt1(d.queueLength))))
    : emptyState('No disk queue samples yet.'));
  out.push(Array.isArray(n) && n.length
    ? table('Network output queue', ['Adapter', 'Packets queued'], n.map((d) => row(d, d.interface, fmt0(d.outputQueueLength))))
    : emptyState('No network samples yet.'));
  return out;
}

const SEVERITY = {
  critical: ['critical', 'Critical'],
  warning: ['warning', 'Warning'],
  info: ['info', 'Info'],
};

function renderIncidents(incidents) {
  const list = Array.isArray(incidents) ? incidents : [];
  const crit = list.filter((i) => i.severity === 'critical').length;
  const head = panelHead('h-incidents', 'Incidents in the last 24 hours',
    list.length ? h('span', { class: 'panel-meta' }, crit ? `${list.length} total, ${crit} critical` : `${list.length} total`) : null);
  if (!list.length) return [head, h('p', { class: 'empty empty-good' }, h('span', { class: 'tag-glyph', 'aria-hidden': 'true' }, '■'), 'No incidents in the last 24 hours.')];
  return [head, h('ol', { class: 'incidents' }, list.map((i) => {
    const [kind, label] = SEVERITY[i.severity] || SEVERITY.info;
    return h('li', { class: `incident sev-${kind}` },
      h('time', { datetime: i.timestamp, title: fmtDateTime(i.timestamp) },
        h('span', { class: 'incident-time' }, fmtTime(i.timestamp)),
        h('span', { class: 'muted small' }, fmtRelative(i.timestamp))),
      statusTag(kind, label),
      h('div', { class: 'incident-body' },
        h('p', { class: 'strong' }, humanize(i.type)),
        i.description ? h('p', { class: 'incident-desc' }, i.description) : null));
  }))];
}

// ------------------------------------------------------------------ alerting

const SEV_RANK = { critical: 0, warning: 1, info: 2 };

/** Open alerts: most severe first, then the longest-running. */
function renderAlerts({ alerts, held }) {
  const list = [...(Array.isArray(alerts) ? alerts : [])].sort((a, b) =>
    (SEV_RANK[a.severity] ?? 3) - (SEV_RANK[b.severity] ?? 3) || String(a.openedAt).localeCompare(String(b.openedAt)));
  const crit = list.filter((a) => a.severity === 'critical').length;
  const head = panelHead('h-alerts', 'Open alerts',
    list.length ? h('span', { class: 'panel-meta' }, [
      crit ? `${list.length} open, ${crit} critical` : `${list.length} open`,
      held ? ' · notifications held' : '',
    ].join('')) : null);
  if (!list.length) {
    return [head, h('p', { class: 'empty empty-good' }, h('span', { class: 'tag-glyph', 'aria-hidden': 'true' }, '■'),
      'No open alerts. Everything WatchTower watches is within its limits.')];
  }
  return [head, h('ol', { class: 'alerts' }, list.map((a) => {
    const [kind, label] = SEVERITY[a.severity] || SEVERITY.info;
    const ageMs = -msUntilServer(a.openedAt);
    const notified = isNum(a.notifyCount) && a.notifyCount > 0;
    return h('li', { class: `alert sev-${kind}` },
      h('time', { class: 'alert-when', datetime: a.openedAt, title: `Opened ${fmtDateTime(a.openedAt)}` },
        h('span', { class: 'alert-time' }, whenText(a.openedAt) || '–'),
        h('span', { class: 'muted' }, Number.isFinite(ageMs) ? `open ${fmtDuration(Math.max(0, ageMs / 1000))}` : '')),
      statusTag(kind, label),
      h('div', { class: 'alert-body' },
        h('p', { class: 'strong' }, a.title || humanize(a.key)),
        a.description && a.description !== a.title ? h('p', { class: 'alert-desc' }, a.description) : null,
        a.key ? h('p', { class: 'alert-key' }, a.key) : null),
      h('div', { class: 'alert-notify' },
        notified
          ? [h('span', { class: 'alert-count' }, `Notified ${a.notifyCount}×`),
            a.lastNotifiedAt ? h('span', { class: 'muted' }, `last ${whenText(a.lastNotifiedAt)}`) : null]
          : [h('span', { class: 'alert-count is-held' }, 'Not sent'),
            h('span', { class: 'muted' }, held ? 'Held for maintenance' : 'Held: no channel took it')]));
  }))];
}

const ENDPOINT_STATUS = {
  up: ['ok', 'Up', 2],
  expiring: ['warning', 'Expiring', 1],
  down: ['critical', 'Down', 0],
  // Configured but invalid: not checked; the row shows its last result.
  configurationerror: ['warning', 'Config error', 1],
};
const ENDPOINT_TYPE = { http: 'HTTP', tcp: 'TCP', certificate: 'TLS cert' };

function endpointState(e) {
  const known = ENDPOINT_STATUS[String(e.status || '').toLowerCase()];
  if (known) return known;
  return e.isUp === false ? ENDPOINT_STATUS.down : ['info', e.status || 'Unknown', 1];
}

function certText(days) {
  if (!isNum(days)) return '–';
  if (days < 0) return 'Expired';
  return days < 1 ? '< 1 day' : `${fmt0(Math.floor(days))} d`;
}

/** Monitoring:Endpoints results (the checks are edited in Settings → Endpoint checks). */
function renderEndpoints(endpoints) {
  const list = [...endpoints].sort((a, b) => endpointState(a)[2] - endpointState(b)[2] || String(a.name).localeCompare(String(b.name)));
  const down = list.filter((e) => endpointState(e)[0] === 'critical').length;
  const expiring = list.filter((e) => endpointState(e) === ENDPOINT_STATUS.expiring).length;
  const misconfigured = list.filter((e) => endpointState(e) === ENDPOINT_STATUS.configurationerror).length;
  const stale = list.filter((e) => e.isStale).length;
  const meta = [down ? `${down} down` : '', expiring ? `${expiring} expiring` : '',
    misconfigured ? `${misconfigured} misconfigured` : '', stale ? `${stale} stale` : ''].filter(Boolean).join(', ') || `${list.length} checked`;
  return [
    panelHead('h-endpoints', 'Endpoints', h('span', { class: 'panel-meta' }, meta, ' · ',
      h('a', { href: '#/config/endpoints', 'aria-label': 'Edit endpoint checks' }, 'Edit'))),
    h('div', { class: 'table-scroll flush' }, h('table', { class: 'table endpoints' },
      h('thead', null, h('tr', null,
        h('th', { scope: 'col' }, 'Endpoint'),
        h('th', { scope: 'col' }, 'Status'),
        h('th', { scope: 'col', class: 'num' }, 'Response'),
        h('th', { scope: 'col', class: 'num' }, 'Certificate'))),
      h('tbody', null, list.map((e) => {
        const [kind, label] = endpointState(e);
        const certKind = isNum(e.certificateDaysLeft) && kind !== 'ok' && e.certificateDaysLeft < 30 ? kind : null;
        const title = e.isStale ? staleTitle(e.checkedAt) : e.checkedAt ? `Checked ${fmtDateTime(e.checkedAt)}` : null;
        return h('tr', { class: `ep sev-${kind}${e.isStale ? ' is-stale' : ''}`, title },
          h('td', { class: 'ep-main' },
            h('p', { class: 'ep-name' },
              h('span', { class: 'strong' }, e.name || '–'),
              e.type ? h('span', { class: 'badge ep-type' }, ENDPOINT_TYPE[String(e.type).toLowerCase()] || e.type) : null),
            e.target ? h('p', { class: 'ep-target' }, e.target) : null,
            e.detail ? h('p', { class: `ep-detail${kind === 'ok' ? '' : ` is-${kind}`}` }, e.detail) : null),
          h('td', { class: 'ep-status' }, statusTag(kind, label), e.isStale ? [h('br'), staleTag(e.checkedAt)] : null),
          h('td', { class: 'num' }, isNum(e.responseTimeMs) ? `${fmt0(e.responseTimeMs)} ms` : '–'),
          h('td', { class: `num${certKind ? ` is-${certKind}` : ''}` }, certText(e.certificateDaysLeft)));
      })))),
  ];
}
