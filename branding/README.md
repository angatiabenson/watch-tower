# WatchTower brand

One visual identity for every WatchTower surface: the agent's built-in web console and the
Visibility Portal. Dark-first, brutalist operations aesthetic, **WatchTower Green** accent.

## Files

| File | Use |
|---|---|
| `watchtower-theme.css` | Design tokens (CSS custom properties). Copy verbatim — never fork values per product. |
| `logo-tile.svg` | App icon / favicon: green mark on a near-black square. Use on any background. |
| `logo-mark.svg` | Mark only, `currentColor` stroke — tint it with CSS (`color: var(--wt-accent)`). |
| `logo-mark-green.svg` | Mark only, fixed green, for places that cannot inherit colour. |
| `logo-wordmark-dark.svg` | Tile + `WATCHTOWER` + cursor, for dark backgrounds. |
| `logo-wordmark-light.svg` | Same, for light backgrounds. |
| `../logo.png` | 512 px raster of the tile (README, places that need a PNG). |

The mark is the outlined **T** tower on a base line (kept from the original logo) with a
small beacon at the top of the stem. Square corners, no rounding, no gradients. Minimum size
16 px (favicon); give it clear space of ¼ of its width.

## Colour

| Token | Dark (default) | Light | Notes |
|---|---|---|---|
| `--wt-accent` | `#10B981` | `#047857` | WatchTower Green. Primary buttons, active nav bar, focus ring, cursor, links. |
| `--wt-on-accent` | `#04130C` | `#FFFFFF` | Text/icons on a filled accent surface (white fails on bright green). |
| `--wt-accent-dim` | 15 % green | 12 % green | Selected rows, accent badges. |
| `--bg-0…3` | `#0A0B0D` → `#1E232B` | `#FAFAF9` → `#E2E0DC` | Page → panel → raised → hover. |
| `--line-1/2` | `#23272F` / `#2E343E` | `#D0CCC6` / `#BDB9B2` | 1 px hairlines; `line-2` on hover. |
| `--fg-1/2/3/dim` | warm off-white scale | warm ink scale | Primary → secondary → labels → disabled. |
| `--ok --warn --crit --info` | `#6DBE7C #E6B14C #E5484D #5A9FE6` | `#276749 #8A5A12 #C53030 #2B6CB0` | All ≥ 4.5:1 on panels. |

Every text/background pairing above meets WCAG AA (4.5:1) in both themes. Status is never
conveyed by colour alone: pair it with a word or the `■` glyph (`■ RUNNING`, `■ OFF`).
Red is a **status** colour only (critical) — it is no longer the brand accent.

## Type

* **Geist** for UI text, **Geist Mono** for labels, numbers, navigation, buttons and badges
  (both SIL OFL 1.1 — self-host the woff2 files; the agent console's CSP blocks font CDNs).
* Scale: 11 / 13 / 14 (base) / 16 / 20 / 28 / 40 / 56 px.
* Section tags: `/ OVERVIEW` — mono 11 px, uppercase, letter-spacing .08em, `--fg-3`, placed
  above a 20 px semibold page title.

## Shape and motifs

* Radius 0–2 px everywhere. Panels are `--bg-1` with a 1 px `--line-1` border. No shadows,
  no gradients, no glassmorphism.
* Primary button: accent fill, `--wt-on-accent` text, mono uppercase, letter-spacing .08em.
  Outline: 1 px `--line-2`. Destructive: `--crit` outline that fills on hover.
* Badges: 1 px coloured border + matching `*-soft` fill + mono uppercase .06em.
* Navigation: mono 11 px uppercase; active item = `--bg-2` + 2 px left accent bar.
* Stats strip: a grid of cells split by 1 px borders with large mono numerals.
* `LIVE █` block cursor in accent (1 s `steps(1)` blink; static under `prefers-reduced-motion`).
* Spinner: 14 px square, 1 px border, accent top edge, 800 ms `steps(8)` rotation.
* Charts: 1.5 px lines, dashed `--crit` threshold line, mono axis labels, no heavy fills.
* Focus: 2 px accent ring with 2 px offset on `--bg-0`.

## Themes

Dark is the default. Offer **Dark / Light / System**, persist the choice, and apply it by
toggling the `dark` class on `<html>` (light = class absent).
