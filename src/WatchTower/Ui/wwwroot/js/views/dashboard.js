// Dashboard: live status from GET /api/status, refreshed every 10 s while the tab is visible.
import {
  h, icon, statusTag, fmtPct, fmt1, fmt0, fmtGb, fmtMbAsGb, fmtDateTime, fmtRelative, fmtTime,
  fmtDuration, humanize, isNum, plural,
} from '../dom.js';
import { api } from '../api.js';
import { notice, restartButton, copyButton } from '../ui.js';
import { stripChart, usageBar } from '../chart.js';

const REFRESH_MS = 10_000;

export function mount(root, _params, ctx) {
  let timer = 0;
  let controller = null;
  let destroyed = false;
  let lastOk = null;
  const prevKeys = new Map();

  const updated = h('span', { class: 'muted small' }, 'Loading…');
  const refreshBtn = h('button', { class: 'btn btn-sm', type: 'button', onclick: () => refresh() }, icon('refresh'), 'Refresh');
  const errorSlot = h('div');
  const slots = {
    health: h('div', { class: 'dash-health' }),
    strip: h('section', { class: 'stats-strip', 'aria-label': 'Summary' }),
    readouts: h('section', { class: 'readouts', 'aria-label': 'Processor and memory' }),
    agent: h('section', { class: 'panel dash-agent', 'aria-labelledby': 'h-agent' }),
    disks: h('section', { class: 'panel dash-disks', 'aria-labelledby': 'h-disks' }),
    services: h('section', { class: 'panel dash-services', 'aria-labelledby': 'h-services' }),
    pools: h('section', { class: 'panel dash-pools', 'aria-labelledby': 'h-pools' }),
    io: h('section', { class: 'panel dash-io', 'aria-labelledby': 'h-io' }),
    incidents: h('section', { class: 'panel dash-incidents', 'aria-labelledby': 'h-incidents' }),
  };

  ctx.setActions(updated, refreshBtn);
  root.append(
    errorSlot,
    slots.health,
    slots.strip,
    h('div', { class: 'dash-grid' },
      slots.readouts, slots.agent, slots.disks, slots.incidents, slots.services, slots.pools, slots.io));

  // Rebuild a slot only when its data changed: keeps focus stable and stops screen
  // readers from re-announcing unchanged banners every 10 seconds.
  const patch = (name, data, build) => {
    const key = JSON.stringify(data ?? null);
    if (prevKeys.get(name) === key) return;
    prevKeys.set(name, key);
    slots[name].replaceChildren(...[build(data)].flat().filter(Boolean));
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
    if (document.visibilityState === 'visible') refresh();
    else { clearTimeout(timer); controller?.abort(); }
  };
  document.addEventListener('visibilitychange', onVisibility);

  function render(s) {
    const m = s.metrics || {};
    patch('health', { health: s.health, restart: s.restartPending }, renderHealth);
    patch('strip', { svc: m.services, pools: m.appPools, disks: m.disks, inc: s.incidents }, renderStrip);
    patch('readouts', { cpu: m.cpu, ram: m.ram }, renderReadouts);
    // Uptime in whole minutes so the panel is not rebuilt on every tick.
    patch('agent', { agent: { ...s.agent, uptimeSeconds: undefined, uptimeMinutes: Math.floor((s.agent?.uptimeSeconds ?? 0) / 60) }, portal: s.portal, email: s.email }, renderAgent);
    patch('disks', m.disks, renderDisks);
    patch('services', m.services, renderServices);
    patch('pools', m.appPools, renderPools);
    patch('io', { q: m.diskQueues, n: m.network }, renderIo);
    patch('incidents', s.incidents, renderIncidents);
  }

  refresh();

  return {
    unmount() {
      destroyed = true;
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

function renderHealth({ health, restart }) {
  const errors = health?.errors || [];
  const warnings = health?.warnings || [];
  const out = [];
  if (restart) out.push(notice('warning', 'Restart needed', 'Some saved settings take effect only after WatchTower restarts.', restartButton()));
  if (errors.length) out.push(notice('critical', errors.length === 1 ? 'WatchTower has a problem' : `WatchTower has ${errors.length} problems`, errors, null, { live: true }));
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
  if (!a.length) return ['–', h('span', { class: 'muted' }, noneText), null];
  return [`${a.length - down}/${a.length}`,
    down ? statusTag('critical', `${down} down`) : statusTag('ok', 'All running'),
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

function renderAgent({ agent: a = {}, portal, email }) {
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
    return h('li', { class: 'disk' },
      h('div', { class: 'disk-head' },
        h('span', { class: 'disk-drive' }, d.drive),
        level !== 'ok' ? statusTag(level, level === 'critical' ? 'Critical' : 'Low space') : null,
        h('span', { class: 'disk-pct' }, fmtPct(d.usagePercent), h('span', { class: 'muted' }, ' used'))),
      bar,
      h('p', { class: 'disk-meta' }, `${fmtGb(d.freeGb)} free of ${fmtGb(d.totalGb)}`,
        h('span', { class: 'muted' }, `Warning at ${fmt0(d.warningPercent)}%, critical at ${fmt0(d.criticalPercent)}%`)));
  }))];
}

function statusList(items, nameOf, subOf) {
  const sorted = [...items].sort((a, b) => Number(a.isRunning) - Number(b.isRunning) || nameOf(a).localeCompare(nameOf(b)));
  return h('ul', { class: 'status-list' }, sorted.map((it) => h('li', { class: it.isRunning ? '' : 'down' },
    h('div', { class: 'status-name' },
      h('span', { class: 'strong' }, nameOf(it)),
      subOf(it) ? h('span', { class: 'muted small' }, subOf(it)) : null),
    statusTag(it.isRunning ? 'ok' : 'critical', it.status || (it.isRunning ? 'Running' : 'Stopped')))));
}

function countLine(items) {
  const down = items.filter((i) => !i.isRunning).length;
  return h('span', { class: 'panel-meta' }, down ? `${down} of ${items.length} down` : `${items.length} watched`);
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
  out.push(Array.isArray(q) && q.length
    ? table('Disk I/O queue', ['Disk', 'Queue length'], q.map((d) => h('tr', null, h('td', null, d.instance), h('td', { class: 'num' }, fmt1(d.queueLength)))))
    : emptyState('No disk queue samples yet.'));
  out.push(Array.isArray(n) && n.length
    ? table('Network output queue', ['Adapter', 'Packets queued'], n.map((d) => h('tr', null, h('td', null, d.interface), h('td', { class: 'num' }, fmt0(d.outputQueueLength)))))
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
