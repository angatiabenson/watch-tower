// Theme: Dark (default) / Light / System, persisted in localStorage and applied by toggling
// the `dark` class on <html> (light = class absent), the same convention as the Visibility
// Portal. app.js imports this module first so the class is set before the first render.

const KEY = 'wt.theme';
export const THEMES = ['dark', 'light', 'system'];
const media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
const listeners = new Set();

export function getTheme() {
  try {
    const v = localStorage.getItem(KEY);
    return THEMES.includes(v) ? v : 'dark';
  } catch {
    return 'dark';
  }
}

function apply(theme) {
  const dark = theme === 'system' ? (media ? media.matches : true) : theme !== 'light';
  document.documentElement.classList.toggle('dark', dark);
}

export function setTheme(theme) {
  if (!THEMES.includes(theme)) return;
  try { localStorage.setItem(KEY, theme); } catch { /* storage disabled: apply for this page only */ }
  apply(theme);
  for (const fn of listeners) fn(theme);
}

/** Called with the new choice whenever the theme changes (in this tab or another). */
export function onThemeChange(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

media?.addEventListener?.('change', () => { if (getTheme() === 'system') apply('system'); });
window.addEventListener('storage', (e) => {
  if (e.key !== KEY) return;
  const t = getTheme();
  apply(t);
  for (const fn of listeners) fn(t);
});

apply(getTheme());
