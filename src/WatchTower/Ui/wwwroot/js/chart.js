// Hand-rolled SVG strip chart: one 1.5 px series of percentages over time, fixed 0–100 % scale
// (honest context for utilisation), a dashed alert-threshold line, and a hover/keyboard
// crosshair read-out. No dependencies.
import { h, svg, fmtPct, fmtTime, isNum } from './dom.js';

const W = 600;   // viewBox units; the SVG scales to its container width
const H = 120;
const PAD_T = 6;
const PAD_B = 4;

export function stripChart({ series, threshold, label }) {
  const pts = (Array.isArray(series) ? series : [])
    .map((p) => ({ t: new Date(p.t).getTime(), v: p.v, iso: p.t }))
    .filter((p) => Number.isFinite(p.t) && isNum(p.v));

  const wrap = h('div', { class: 'chart' });
  if (pts.length < 2) {
    wrap.append(h('p', { class: 'chart-empty' }, 'The trend appears after a few samples.'));
    return wrap;
  }

  const t0 = pts[0].t;
  const t1 = pts[pts.length - 1].t;
  const span = Math.max(1, t1 - t0);
  const x = (t) => ((t - t0) / span) * W;
  const y = (v) => PAD_T + (1 - Math.min(100, Math.max(0, v)) / 100) * (H - PAD_T - PAD_B);

  const line = pts.map((p, i) => `${i ? 'L' : 'M'}${x(p.t).toFixed(1)},${y(p.v).toFixed(1)}`).join('');

  const vals = pts.map((p) => p.v);
  const min = Math.min(...vals);
  const max = Math.max(...vals);
  const last = vals[vals.length - 1];
  const summary = `${label}: ${fmtPct(last)} now; between ${fmtPct(min)} and ${fmtPct(max)} since ${fmtTime(pts[0].iso)}` +
    (isNum(threshold) ? `; alert threshold ${fmtPct(threshold)}.` : '.');

  const cross = svg('line', { class: 'chart-cross', x1: 0, x2: 0, y1: 0, y2: H, 'vector-effect': 'non-scaling-stroke' });
  const g = svg('svg', {
    class: 'chart-svg', viewBox: `0 0 ${W} ${H}`, preserveAspectRatio: 'none',
    role: 'img', 'aria-label': summary,
  },
    // recessive grid at 25 / 50 / 75 %
    [25, 50, 75].map((v) => svg('line', { class: 'chart-grid', x1: 0, x2: W, y1: y(v), y2: y(v), 'vector-effect': 'non-scaling-stroke' })),
    svg('path', { class: 'chart-line', d: line, 'vector-effect': 'non-scaling-stroke' }),
    isNum(threshold) ? svg('line', { class: 'chart-threshold', x1: 0, x2: W, y1: y(threshold), y2: y(threshold), 'vector-effect': 'non-scaling-stroke' }) : null,
    cross);

  // Marker + tooltip are HTML so they stay round / crisp despite the non-uniform SVG scale.
  const dot = h('span', { class: 'chart-dot', hidden: true, 'aria-hidden': 'true' });
  const tip = h('span', { class: 'chart-tip', hidden: true, 'aria-hidden': 'true' });
  const plot = h('div', { class: 'chart-plot', tabindex: '0', 'aria-label': `${summary} Use the arrow keys to read individual samples.` }, g, dot, tip);
  const live = h('span', { class: 'sr-only', 'aria-live': 'polite' });

  if (isNum(threshold)) {
    const tl = h('span', { class: 'chart-threshold-label' }, `Alert ${fmtPct(threshold)}`);
    tl.style.top = `${(y(threshold) / H) * 100}%`;
    plot.append(tl);
  }

  let idx = -1;
  const show = (i, announce = false) => {
    idx = Math.max(0, Math.min(pts.length - 1, i));
    const p = pts[idx];
    const px = (x(p.t) / W) * 100;
    const py = (y(p.v) / H) * 100;
    cross.setAttribute('x1', x(p.t)); cross.setAttribute('x2', x(p.t));
    cross.classList.add('on');
    dot.hidden = false; dot.style.left = `${px}%`; dot.style.top = `${py}%`;
    tip.hidden = false;
    tip.replaceChildren(h('strong', null, fmtPct(p.v)), ' ', fmtTime(p.iso, true));
    tip.style.left = `${px}%`;
    tip.classList.toggle('flip', px > 70);
    if (announce) live.textContent = `${fmtPct(p.v)} at ${fmtTime(p.iso, true)}`;
  };
  const hide = () => { idx = -1; cross.classList.remove('on'); dot.hidden = true; tip.hidden = true; };

  plot.addEventListener('pointermove', (e) => {
    const r = plot.getBoundingClientRect();
    const t = t0 + ((e.clientX - r.left) / r.width) * span;
    let best = 0;
    for (let i = 1; i < pts.length; i++) if (Math.abs(pts[i].t - t) < Math.abs(pts[best].t - t)) best = i;
    show(best);
  });
  plot.addEventListener('pointerleave', hide);
  plot.addEventListener('blur', hide);
  plot.addEventListener('keydown', (e) => {
    const step = { ArrowLeft: -1, ArrowRight: 1, Home: -Infinity, End: Infinity }[e.key];
    if (step === undefined) { if (e.key === 'Escape') hide(); return; }
    e.preventDefault();
    const start = idx < 0 ? pts.length - 1 : idx;
    show(step === -Infinity ? 0 : step === Infinity ? pts.length - 1 : start + step, true);
  });

  wrap.append(plot,
    h('div', { class: 'chart-axis', 'aria-hidden': 'true' },
      h('span', null, fmtTime(pts[0].iso)),
      h('span', null, 'now')),
    live);
  return wrap;
}

/** Horizontal usage bar with tick marks at the warning and critical thresholds. */
export function usageBar({ percent, warning, critical, label }) {
  const p = isNum(percent) ? Math.min(100, Math.max(0, percent)) : 0;
  const level = isNum(critical) && p >= critical ? 'critical' : isNum(warning) && p >= warning ? 'warning' : 'ok';
  const fill = h('span', { class: `bar-fill bar-${level}` });
  fill.style.width = `${p}%`;
  const ticks = [];
  for (const [v, cls, name] of [[warning, 'tick-warning', 'warning'], [critical, 'tick-critical', 'critical']]) {
    if (!isNum(v)) continue;
    const t = h('span', { class: `bar-tick ${cls}`, title: `${name} at ${fmtPct(v)}` });
    t.style.left = `${Math.min(100, Math.max(0, v))}%`;
    ticks.push(t);
  }
  const bar = h('div', {
    class: 'bar', role: 'meter', 'aria-valuemin': 0, 'aria-valuemax': 100, 'aria-valuenow': p.toFixed(1),
    'aria-label': label, 'aria-valuetext': `${fmtPct(p)} used`,
  }, fill, ...ticks);
  return { bar, level };
}
