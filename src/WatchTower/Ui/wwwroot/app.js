// WatchTower console entry point: auth gate, app shell, hash router, unsaved-changes guard.
// theme.js is imported first so the dark/light class is applied before anything renders.
import { THEMES, getTheme, setTheme, onThemeChange } from './js/theme.js';
import { h, icon } from './js/dom.js';
import { api, ApiError } from './js/api.js';
import { notice, confirmDialog, restartButton, toast } from './js/ui.js';
import { renderSetup, renderLogin, renderUnreachable } from './js/views/auth.js';
import * as dashboardView from './js/views/dashboard.js';
import * as configView from './js/views/config.js';
import * as logsView from './js/views/logs.js';
import * as updatesView from './js/views/updates.js';
import * as accountView from './js/views/account.js';

const appEl = document.getElementById('app');

const ROUTES = [
  { id: 'dashboard', pattern: /^\/dashboard\/?$/, view: dashboardView, title: 'Dashboard', tag: 'Overview', nav: 'dashboard', live: true },
  { id: 'config', pattern: /^\/config(?:\/([\w-]+))?\/?$/, view: configView, title: 'Configuration', tag: 'Settings', nav: 'config' },
  { id: 'logs', pattern: /^\/logs\/?$/, view: logsView, title: 'Logs', tag: 'Diagnostics', nav: 'logs' },
  { id: 'updates', pattern: /^\/updates\/?$/, view: updatesView, title: 'Updates', tag: 'Maintenance', nav: 'updates' },
  { id: 'account', pattern: /^\/account\/?$/, view: accountView, title: 'Account', tag: 'Access', nav: 'account' },
];

const NAV_GROUPS = [
  { label: 'Monitoring', items: [
    { id: 'dashboard', href: '#/dashboard', label: 'Dashboard', icon: 'dashboard' },
    { id: 'logs', href: '#/logs', label: 'Logs', icon: 'logs' },
  ] },
  { label: 'Settings', items: [
    { id: 'config', href: '#/config', label: 'Configuration', icon: 'config' },
    { id: 'updates', href: '#/updates', label: 'Updates', icon: 'updates' },
    { id: 'account', href: '#/account', label: 'Account', icon: 'account' },
  ] },
];

const THEME_META = {
  dark: { label: 'Dark', icon: 'moon' },
  light: { label: 'Light', icon: 'sun' },
  system: { label: 'System', icon: 'monitor' },
};

const shell = {
  mounted: false,
  el: null,
  viewEl: null,
  titleEl: null,
  tagEl: null,
  crumbEl: null,
  liveEl: null,
  hostEl: null,
  actionsEl: null,
  bannerEl: null,
  navLinks: new Map(),
  current: null, // { route, controller, hash }
  restartPending: false,
  status: null,
  lastHash: '',
  suppressNextHashChange: false,
};

// ---------------------------------------------------------------- boot / auth gate
async function boot() {
  let state;
  try {
    state = await api.authState();
  } catch (e) {
    showScreen(renderUnreachable(e instanceof ApiError ? e.message : String(e), boot));
    return;
  }
  if (state.setupRequired) {
    showScreen(renderSetup(state, { onDone: boot, onRecheck: boot }));
  } else if (!state.authenticated) {
    showScreen(renderLogin({ onDone: boot }));
  } else {
    startShell();
  }
}

function showScreen(node) {
  teardownShell();
  appEl.className = 'screen';
  appEl.removeAttribute('aria-busy');
  appEl.replaceChildren(node);
  document.title = 'WatchTower';
  node.querySelector('[autofocus]')?.focus();
}

function teardownShell() {
  if (shell.current) {
    try { shell.current.controller.unmount?.(); } catch { /* ignore */ }
  }
  shell.current = null;
  shell.mounted = false;
}

window.addEventListener('wt:unauthorized', () => {
  if (!shell.mounted) return;
  showScreen(renderLogin({ onDone: boot, expired: true }));
});

// ---------------------------------------------------------------- shell
function navLink(n) {
  const a = h('a', { class: 'nav-link', href: n.href, title: n.label },
    icon(n.icon), h('span', { class: 'nav-label' }, n.label));
  shell.navLinks.set(n.id, a);
  return h('li', null, a);
}

/** Sidebar footer: an "Administrator" row that opens the theme switch and Sign out. */
function profileMenu() {
  const menuId = 'profile-menu';
  const themeIcon = h('span', { class: 'profile-theme' });
  const toggle = h('button', {
    class: 'profile-btn', type: 'button', 'aria-expanded': 'false', 'aria-controls': menuId,
  },
  h('span', { class: 'avatar', 'aria-hidden': 'true' }, 'AD'),
  h('span', { class: 'profile-name' }, 'Administrator'),
  themeIcon,
  icon('chevron', 'profile-chevron'));

  const themeButtons = THEMES.map((t) => {
    const b = h('button', { class: 'menu-item', type: 'button', 'aria-pressed': 'false', dataset: { theme: t } },
      icon(THEME_META[t].icon), h('span', null, THEME_META[t].label));
    b.addEventListener('click', () => { setTheme(t); close(true); });
    return b;
  });
  const menu = h('div', { class: 'profile-menu', id: menuId, hidden: true },
    h('p', { class: 'menu-tag', id: `${menuId}-theme` }, '/ Theme'),
    h('div', { class: 'menu-group', role: 'group', 'aria-labelledby': `${menuId}-theme` }, themeButtons),
    h('div', { class: 'menu-sep' },
      h('button', { class: 'menu-item menu-item-danger', type: 'button', onclick: () => { close(false); signOut(); } },
        icon('signout'), h('span', null, 'Sign out'))));

  const paint = (t) => {
    for (const b of themeButtons) b.setAttribute('aria-pressed', String(b.dataset.theme === t));
    themeIcon.replaceChildren(icon(THEME_META[t]?.icon || 'moon'));
    toggle.setAttribute('aria-label', `Administrator: theme (${THEME_META[t]?.label || 'Dark'}) and sign out`);
    toggle.title = `Theme: ${THEME_META[t]?.label || 'Dark'}. Sign out`;
  };
  const wrap = h('div', { class: 'sidebar-foot' }, menu, toggle);
  paint(getTheme());
  // The shell is rebuilt after every sign-in; drop listeners that belong to an old one.
  const offTheme = onThemeChange((t) => { if (wrap.isConnected) paint(t); else offTheme(); });
  const onDown = (e) => {
    if (!wrap.isConnected) { document.removeEventListener('mousedown', onDown); return; }
    if (!wrap.contains(e.target)) close(false);
  };
  document.addEventListener('mousedown', onDown);

  function open() {
    menu.hidden = false;
    toggle.setAttribute('aria-expanded', 'true');
    (themeButtons.find((b) => b.getAttribute('aria-pressed') === 'true') || themeButtons[0]).focus();
  }
  function close(refocus) {
    if (menu.hidden) return;
    menu.hidden = true;
    toggle.setAttribute('aria-expanded', 'false');
    if (refocus) toggle.focus();
  }
  toggle.addEventListener('click', () => (menu.hidden ? open() : close(false)));
  wrap.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && !menu.hidden) { e.stopPropagation(); close(true); }
  });
  wrap.addEventListener('focusout', (e) => { if (e.relatedTarget && !wrap.contains(e.relatedTarget)) close(false); });
  return wrap;
}

function startShell() {
  shell.navLinks.clear();
  const nav = h('nav', { class: 'sidebar', 'aria-label': 'Main' },
    h('a', { class: 'wordmark', href: '#/dashboard', 'aria-label': 'WatchTower dashboard' },
      h('img', { class: 'wordmark-tile', src: '/logo-tile.svg', alt: '', width: 24, height: 24 }),
      h('span', { class: 'wordmark-text', 'aria-hidden': 'true' }, 'WatchTower')),
    h('div', { class: 'sidebar-nav' },
      NAV_GROUPS.map((g, i) => h('div', { class: 'nav-group' },
        h('p', { class: 'nav-tag', id: `nav-g${i}` }, `/ ${g.label}`),
        h('ul', { class: 'nav-list', 'aria-labelledby': `nav-g${i}` }, g.items.map(navLink))))),
    profileMenu());

  shell.crumbEl = h('span', { class: 'crumb-current' }, '');
  shell.hostEl = h('p', { class: 'host-chip' });
  shell.liveEl = h('p', { class: 'live', hidden: true },
    h('span', { 'aria-hidden': 'true' }, 'LIVE'), h('span', { class: 'cursor', 'aria-hidden': 'true' }),
    h('span', { class: 'sr-only' }, 'Live: refreshes every 10 seconds'));
  shell.tagEl = h('p', { class: 'section-tag' }, '');
  shell.titleEl = h('h1', { class: 'page-title', tabindex: '-1' }, '');
  shell.actionsEl = h('div', { class: 'page-actions' });
  shell.bannerEl = h('div', { class: 'shell-banners' });
  shell.viewEl = h('main', { class: 'view', id: 'main' });

  shell.el = h('div', { class: 'shell' },
    h('a', { class: 'skip-link', href: '#main', onclick: (e) => { e.preventDefault(); shell.viewEl.focus(); } }, 'Skip to content'),
    nav,
    h('div', { class: 'workspace' },
      h('header', { class: 'topbar' },
        h('p', { class: 'crumbs' },
          h('span', { class: 'crumb-root' }, 'watchtower'),
          h('span', { class: 'crumb-sep', 'aria-hidden': 'true' }, '/'),
          shell.crumbEl),
        h('div', { class: 'topbar-right' }, shell.hostEl, shell.liveEl)),
      h('div', { class: 'page-head' },
        h('div', { class: 'page-heading' }, shell.tagEl, shell.titleEl),
        shell.actionsEl),
      shell.bannerEl,
      shell.viewEl));
  shell.viewEl.tabIndex = -1;

  appEl.className = 'app';
  appEl.removeAttribute('aria-busy');
  appEl.replaceChildren(shell.el);
  shell.mounted = true;
  shell.lastHash = '';

  // Header facts (machine, version) and restart-pending state, without blocking the view.
  api.status().then(onStatus).catch(() => { /* the dashboard reports errors itself */ });

  if (!location.hash || location.hash === '#' || location.hash === '#/') {
    history.replaceState(null, '', '#/dashboard');
  }
  route(false);
}

async function signOut() {
  if (shell.current?.controller.isDirty?.()) {
    const ok = await confirmDialog({
      title: 'Discard unsaved changes?',
      message: 'You have configuration changes that are not saved. Signing out discards them.',
      confirmLabel: 'Discard and sign out', danger: true,
    });
    if (!ok) return;
  }
  try { await api.logout(); } catch { /* signing out locally anyway */ }
  teardownShell();
  boot();
}

function onStatus(status) {
  if (!status || !shell.mounted) return;
  shell.status = status;
  const a = status.agent || {};
  const parts = [a.machineName, a.version ? `WatchTower ${a.version}` : null].filter(Boolean);
  shell.hostEl.replaceChildren(
    h('span', { class: 'host-name' }, a.machineName || ''),
    a.version ? h('span', { class: 'host-version' }, `v${a.version}`) : '');
  shell.hostEl.title = parts.join(', ');
  shell.hostEl.setAttribute('aria-label', parts.join(', '));
  if (typeof status.restartPending === 'boolean') setRestartPending(status.restartPending);
  if (status.update !== undefined) {
    const u = status.update || {};
    setUpdateMarker(u.availableVersion && u.phase !== 'Installing' ? u.availableVersion : null);
  }
  updateDocTitle();
}

/** Small accent marker on the Updates nav item while a newer version is available. */
function setUpdateMarker(version) {
  const a = shell.navLinks.get('updates');
  if (!a) return;
  let mark = a.querySelector('.nav-mark');
  if (!version) {
    mark?.remove();
    a.title = 'Updates';
    return;
  }
  if (!mark) {
    mark = h('span', { class: 'nav-mark' }, h('span', { class: 'sr-only' }));
    a.append(mark);
  }
  mark.firstChild.textContent = `, version ${version} available`;
  a.title = `Updates: WatchTower ${version} is available`;
}

function setRestartPending(pending) {
  shell.restartPending = !!pending;
  // The dashboard renders its own health area, which includes the restart banner.
  const showHere = shell.restartPending && shell.current?.route.id !== 'dashboard';
  shell.bannerEl.replaceChildren(showHere
    ? notice('warning', 'Restart needed', 'Some saved settings take effect only after WatchTower restarts.', restartButton())
    : '');
}

function updateDocTitle() {
  const t = shell.current?.title || shell.current?.route.title || 'WatchTower';
  const m = shell.status?.agent?.machineName;
  document.title = m ? `${t} – WatchTower on ${m}` : `${t} – WatchTower`;
}

const ctx = {
  /** Sets the browser tab title; pass heading=true to also replace the page heading. */
  setTitle(text, heading = false) {
    if (!shell.current) return;
    shell.current.title = text;
    if (heading) shell.titleEl.textContent = text;
    updateDocTitle();
  },
  onStatus,
  setActions(...nodes) { shell.actionsEl?.replaceChildren(...nodes.flat().filter(Boolean)); },
  setRestartPending,
  setUpdateMarker,
  get restartPending() { return shell.restartPending; },
  navigate(hash) { location.hash = hash; },
};

// ---------------------------------------------------------------- router
function parseHash() {
  const path = decodeURIComponent((location.hash || '').replace(/^#/, '')) || '/dashboard';
  for (const r of ROUTES) {
    const m = r.pattern.exec(path);
    if (m) return { route: r, params: m.slice(1) };
  }
  return null;
}

async function route(userInitiated = true) {
  if (!shell.mounted) return;
  const match = parseHash();
  if (!match) {
    history.replaceState(null, '', '#/dashboard');
    return route(userInitiated);
  }
  const { route: r, params } = match;
  const cur = shell.current;

  // Same view with in-place navigation support (config sections keep their drafts).
  if (cur && cur.route === r && cur.controller.update) {
    cur.controller.update(params);
    shell.lastHash = location.hash;
    return;
  }

  if (cur?.controller.isDirty?.()) {
    // Put the old URL back while we ask, so the address bar stays truthful.
    const target = location.hash;
    shell.suppressNextHashChange = true;
    history.replaceState(null, '', shell.lastHash || '#/dashboard');
    shell.suppressNextHashChange = false;
    const ok = await confirmDialog({
      title: 'Leave without saving?',
      message: 'You have configuration changes that are not saved. Leaving this page discards them.',
      confirmLabel: 'Discard changes', cancelLabel: 'Stay on this page', danger: true,
    });
    if (!ok) return;
    cur.controller.discard?.();
    history.replaceState(null, '', target);
    return route(userInitiated);
  }

  if (cur) {
    try { cur.controller.unmount?.(); } catch (e) { console.error(e); }
  }
  for (const [id, a] of shell.navLinks) {
    if (id === r.nav) a.setAttribute('aria-current', 'page');
    else a.removeAttribute('aria-current');
  }

  shell.viewEl.replaceChildren();
  shell.actionsEl.replaceChildren();
  shell.viewEl.dataset.view = r.id;
  shell.current = { route: r, controller: {}, title: r.title };
  shell.titleEl.textContent = r.title;
  shell.tagEl.textContent = `/ ${r.tag}`;
  shell.crumbEl.textContent = r.title.toLowerCase();
  shell.liveEl.hidden = !r.live;
  updateDocTitle();
  setRestartPending(shell.restartPending);
  try {
    shell.current.controller = r.view.mount(shell.viewEl, params, ctx) || {};
  } catch (e) {
    console.error(e);
    shell.viewEl.replaceChildren(notice('critical', 'This page failed to load', String(e?.message || e)));
  }
  shell.lastHash = location.hash;
  if (userInitiated) shell.titleEl.focus({ preventScroll: true });
  window.scrollTo(0, 0);
}

window.addEventListener('hashchange', () => {
  if (shell.suppressNextHashChange) return;
  route(true);
});

window.addEventListener('beforeunload', (e) => {
  if (shell.current?.controller.isDirty?.()) {
    e.preventDefault();
    e.returnValue = '';
  }
});

window.addEventListener('unhandledrejection', (e) => {
  const err = e.reason;
  if (err && err.name === 'AbortError') return;
  if (err instanceof ApiError && err.status === 401) return;
  console.error(err);
  toast(err?.message || 'Something went wrong.', { kind: 'error' });
});

boot();
