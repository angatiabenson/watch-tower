// Thin wrapper over fetch() implementing the conventions in API.md:
// JSON in/out, X-WatchTower-Request on every non-GET, 401 → "wt:unauthorized" event,
// error bodies { error } or { errors: { key: msg } } surfaced as ApiError.

export class ApiError extends Error {
  constructor(status, body, fallback) {
    const errors = body && typeof body.errors === 'object' && body.errors ? body.errors : null;
    const msg = (body && typeof body.error === 'string' && body.error)
      || (errors && (errors._ || Object.values(errors)[0]))
      || fallback
      || defaultMessage(status);
    super(msg);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
    this.errors = errors;
  }
}

function defaultMessage(status) {
  if (status === 0) return 'Cannot reach WatchTower. Check that the service is running, then try again.';
  if (status === 401) return 'Your session has ended. Sign in again.';
  if (status === 403) return 'This action is not allowed from here.';
  if (status === 404) return 'This feature is not available in this version of WatchTower.';
  if (status === 429) return 'Too many attempts. Wait a few minutes, then try again.';
  if (status >= 500) return `WatchTower hit an internal error (HTTP ${status}). Check the logs for details.`;
  return `Request failed (HTTP ${status}).`;
}

export async function request(path, { method = 'GET', body, signal } = {}) {
  const init = {
    method,
    credentials: 'same-origin',
    cache: 'no-store',
    signal,
    headers: { Accept: 'application/json' },
  };
  if (method !== 'GET') {
    init.headers['X-WatchTower-Request'] = '1';
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body ?? {});
  }

  let res;
  try {
    res = await fetch(`/api${path}`, init);
  } catch (e) {
    if (e && e.name === 'AbortError') throw e;
    throw new ApiError(0, null);
  }

  const text = await res.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); } catch { data = null; }
  }

  if (res.status === 401 && !path.startsWith('/auth/')) {
    window.dispatchEvent(new CustomEvent('wt:unauthorized'));
  }
  if (!res.ok) throw new ApiError(res.status, data);
  return data;
}

const post = (path, body) => request(path, { method: 'POST', body });

export const api = {
  authState: () => request('/auth/state'),
  setup: (password) => post('/auth/setup', { password }),
  login: (password) => post('/auth/login', { password }),
  logout: () => post('/auth/logout'),
  changePassword: (currentPassword, newPassword) => post('/auth/password', { currentPassword, newPassword }),

  status: (signal) => request('/status', { signal }),

  config: () => request('/config'),
  saveConfig: (changes, reset) => request('/config', { method: 'PUT', body: { changes, reset } }),

  services: () => request('/system/services'),
  appPools: () => request('/system/apppools'),
  timeZones: () => request('/system/timezones'),

  testEmail: () => post('/actions/test-email'),
  restart: () => post('/actions/restart'),

  logs: (lines) => request(`/logs?lines=${encodeURIComponent(lines)}`),
};

/** Memoise a lookup for the lifetime of the page (timezones etc.). Failures are not cached. */
export function cached(fn) {
  let p = null;
  return () => {
    if (!p) p = fn().catch((e) => { p = null; throw e; });
    return p;
  };
}
