# CNAS „Protecția Socială" — Design System

> Aligned with **Hotărârea Guvernului nr. 677/2025** ("Modelul Unitar de Design"
> for Republic of Moldova government digital services). This document is the
> canonical reference for the CSS design tokens shipped in
> `src/Cnas.Ps.Web/wwwroot/css/hg677-tokens.css` and consumed by every Blazor
> page of the citizen portal (Cnas.Ps.Web).

HG 677/2025 mandates a unified visual language across Moldovan e-Government
services: a single colour palette, typography scale, spacing rhythm, and
elevation system, with WCAG 2.1 AA contrast guaranteed in both light and dark
modes. The token sheet expresses that vocabulary as CSS custom properties on
`:root` so any component can compose with `var(--cnas-*)` instead of hard-coded
literals.

---

## Token vocabulary

The table below lists the tokens currently shipped (file version 1.0.0 —
R0221 / TOR UI 016). Specific HEX values are an interim scheme pending the
parsed HG 677/2025 PDF reference; the **names** are stable contract and MUST
NOT be renamed without an architecture-test update.

### Colour — Primary (deep blue, MD national identity)

| Token                          | Purpose                                  |
|--------------------------------|------------------------------------------|
| `--cnas-color-primary-50`      | Tint backgrounds (banners, hover wash)   |
| `--cnas-color-primary-100`     | Light tint, headers on dark surfaces     |
| `--cnas-color-primary-200`     | Lower-emphasis fills                     |
| `--cnas-color-primary-300`     | Subtle accent borders                    |
| `--cnas-color-primary-400`     | Mid stop, focus indicators on light bg   |
| `--cnas-color-primary-500`     | **Brand baseline** — buttons, links      |
| `--cnas-color-primary-600`     | Hover / pressed state                    |
| `--cnas-color-primary-700`     | Active state                             |
| `--cnas-color-primary-800`     | Strong contrast headings                 |
| `--cnas-color-primary-900`     | Highest contrast (titles on light bg)    |

### Colour — Secondary (gold, MD flag accent)

| Token                          | Purpose                                  |
|--------------------------------|------------------------------------------|
| `--cnas-color-secondary-50`    | Background wash for warnings/highlights  |
| `--cnas-color-secondary-100`   | Light tint                               |
| `--cnas-color-secondary-300`   | Mid stop                                 |
| `--cnas-color-secondary-500`   | **Accent baseline** — call-to-action ring|
| `--cnas-color-secondary-700`   | Hover / pressed                          |
| `--cnas-color-secondary-900`   | High-contrast text on light bg           |

### Colour — Semantic

| Token                          | Purpose                                  |
|--------------------------------|------------------------------------------|
| `--cnas-color-success-{50,500,700}` | Confirmation / completed states     |
| `--cnas-color-warning-{50,500,700}` | Non-blocking warnings              |
| `--cnas-color-danger-{50,500,700}`  | Errors, destructive actions        |
| `--cnas-color-info-{50,500,700}`    | Informational messages             |

### Colour — Neutrals

| Token                          | Purpose                                  |
|--------------------------------|------------------------------------------|
| `--cnas-color-neutral-0`       | Page surface (white in light, dark in dark) |
| `--cnas-color-neutral-{50..900}` | Borders, dividers, secondary text      |
| `--cnas-color-neutral-1000`    | Body text (highest contrast)             |

### Typography

| Token                                  | Purpose                          |
|----------------------------------------|----------------------------------|
| `--cnas-font-family-sans`              | Default UI body / headings       |
| `--cnas-font-family-serif`             | Legal documents, printable forms |
| `--cnas-font-size-{xs,sm,base,lg,xl,2xl,3xl}` | Modular scale (1.25 ratio, base 16 px) |
| `--cnas-line-height-{tight,normal,relaxed}` | 1.2 / 1.5 / 1.75            |
| `--cnas-font-weight-{regular,medium,bold}` | 400 / 500 / 700              |

### Spacing — 4 px base scale

| Token                                  | Value     |
|----------------------------------------|-----------|
| `--cnas-space-0`                       | 0         |
| `--cnas-space-1`                       | 4 px      |
| `--cnas-space-2`                       | 8 px      |
| `--cnas-space-3`                       | 12 px     |
| `--cnas-space-4`                       | 16 px     |
| `--cnas-space-5`                       | 20 px     |
| `--cnas-space-6`                       | 24 px     |
| `--cnas-space-8`                       | 32 px     |
| `--cnas-space-10`                      | 40 px     |
| `--cnas-space-12`                      | 48 px     |
| `--cnas-space-16`                      | 64 px     |
| `--cnas-space-20`                      | 80 px     |
| `--cnas-space-24`                      | 96 px     |

### Border radius

| Token                  | Value      | Purpose                          |
|------------------------|------------|----------------------------------|
| `--cnas-radius-none`   | 0          | Tables, sharp edges              |
| `--cnas-radius-sm`     | 2 px       | Inputs, small buttons            |
| `--cnas-radius-md`     | 4 px       | Default                          |
| `--cnas-radius-lg`     | 8 px       | Cards, modals                    |
| `--cnas-radius-full`   | 9999 px    | Pills, avatars                   |

### Shadows / elevation

| Token                  | Purpose                                   |
|------------------------|-------------------------------------------|
| `--cnas-shadow-none`   | Flat                                      |
| `--cnas-shadow-sm`     | Resting cards                             |
| `--cnas-shadow-md`     | Raised cards, popovers                    |
| `--cnas-shadow-lg`     | Modals, dialogs                           |

### Z-index — named stacking tiers

| Token                  | Value | Purpose                            |
|------------------------|-------|------------------------------------|
| `--cnas-z-base`        | 0     | In-flow content                    |
| `--cnas-z-dropdown`    | 1000  | Menus, autocompletes               |
| `--cnas-z-sticky`      | 1100  | Sticky table headers, nav          |
| `--cnas-z-modal`       | 1300  | Modal dialogs                      |
| `--cnas-z-toast`       | 1400  | Toast notifications (topmost)      |

---

## Usage example

Components MUST reference tokens — never literals. The example below shows a
primary button that adapts automatically to light and dark mode:

```css
.btn-primary {
    background: var(--cnas-color-primary-500);
    color:       var(--cnas-color-neutral-0);
    padding:     var(--cnas-space-3) var(--cnas-space-4);
    border:      none;
    border-radius: var(--cnas-radius-md);
    font:        var(--cnas-font-weight-medium) var(--cnas-font-size-base) /
                 var(--cnas-line-height-normal)
                 var(--cnas-font-family-sans);
    box-shadow:  var(--cnas-shadow-sm);
}

.btn-primary:hover  { background: var(--cnas-color-primary-600); }
.btn-primary:active { background: var(--cnas-color-primary-700); }
.btn-primary:focus-visible {
    outline:        2px solid var(--cnas-color-secondary-500);
    outline-offset: 2px;
}
```

---

## Dark mode, reduced motion, and WCAG conformance

* **Dark mode.** `hg677-tokens.css` includes a
  `@media (prefers-color-scheme: dark)` block that overrides the primary and
  neutral colour scales. Components written with token references therefore
  flip themes automatically — no per-component branching required.
  HG 677/2025 requires WCAG 2.1 AA contrast in **both** modes; the tuning above
  preserves 4.5 : 1 body-text and 3 : 1 large-text contrast.
* **Reduced motion.** Animations and transitions defined in component
  stylesheets MUST be wrapped in a
  `@media (prefers-reduced-motion: no-preference)` block (or guarded by an
  equivalent rule). This is enforced via review, not by the token sheet itself.
* **WCAG conformance.** The accessibility test fixture (R0223) loads the
  citizen-portal static shell — including `hg677-tokens.css` — and runs
  axe-core 4.10 against it. Any palette tuning that regresses contrast will be
  caught by that CI job; do not bypass it.

---

## Responsive grid (R0222 / TOR UI 005-006)

HG 677/2025 + the SI Protecția Socială TOR (UI 005-006) require the citizen
portal to render responsively, with a **baseline desktop viewport of
1360 × 768** and graceful adaptation down to mobile and up to wide monitors.
The responsive primitives ship in
`src/Cnas.Ps.Web/wwwroot/css/cnas-responsive.css`, loaded by `index.html`
immediately after `hg677-tokens.css` so the breakpoint tokens resolve first.

### Breakpoint scale

| Token              | Value     | Purpose                                          |
|--------------------|-----------|--------------------------------------------------|
| `--cnas-bp-sm`     | 640 px    | Small phones landscape / large phones portrait   |
| `--cnas-bp-md`     | 768 px    | Tablet portrait                                  |
| `--cnas-bp-lg`     | 1024 px   | Tablet landscape / small laptop                  |
| `--cnas-bp-xl`     | **1360 px** | **Baseline** — desktop, citizen-portal target  |
| `--cnas-bp-2xl`    | 1920 px   | Full HD desktop / wide monitor                   |

> **Why both token + literal?** CSS Custom Properties cannot be used inside
> `@media` query conditions (the media query is resolved before the cascade
> resolves variables). The `--cnas-bp-*` tokens are reference-only Single
> Source of Truth; `@media` rules in component CSS hard-code the same
> literals. The architecture test
> `ResponsiveBreakpointsTests` pins both sides so they cannot drift.

### Utilities

`cnas-responsive.css` ships three opt-in primitives — no Bootstrap, no Tailwind.

* **Container** — bounded, centred wrapper.
  * `.cnas-container` — `max-width: 1360 px`, horizontal padding from
    `var(--cnas-space-4)`. Use for the standard citizen-portal page shell.
  * `.cnas-container-fluid` — full-width variant for staff-console pages
    that need to fill the viewport.
* **Grid** — 12-column flexbox.
  * `.cnas-row` — `display: flex; flex-wrap: wrap; gap: var(--cnas-space-4)`.
  * `.cnas-col-{12,6,4,3}` — percentage-based columns. Mobile-first: every
    column is full-width below `sm` (640 px) and adopts its percentage
    width at `≥ sm`.
* **Show / hide** — viewport-conditional display.
  * `.cnas-show-{sm,md,lg,xl,2xl}-up` — hidden by default, shown at `≥ bp`.
  * `.cnas-hide-{sm,md,lg,xl,2xl}-down` — shown by default, hidden at `< bp`.

### Usage example

```html
<div class="cnas-container">
    <div class="cnas-row">
        <div class="cnas-col-12 cnas-col-md-6">Card A</div>
        <div class="cnas-col-12 cnas-col-md-6">Card B</div>
    </div>
    <p class="cnas-show-xl-up">Desktop-only secondary nav.</p>
</div>
```

> The baseline viewport is **1360 × 768** in both **LTR** (ro / ru) and
> **RTL-ready** layouts. Real RTL viewport testing arrives with the RTL
> language pack work; the grid primitives already use the
> direction-independent `flex` + `gap` model so they Just Work when
> `dir="rtl"` is set on the document.

### Smoke testing

`tests/Cnas.Ps.Accessibility.Tests/ResponsiveSmokeTests.cs` boots the static
shell via the existing Playwright + Kestrel fixture and navigates `/` at four
representative viewports: `1360 × 768` (baseline), `1920 × 1080` (wide
desktop), `768 × 1024` (tablet portrait), `375 × 667` (small phone). Each
assertion confirms the page renders, the body element exists, and the
document has a positive `scrollWidth`. **Pixel-perfect layout comparison is
deferred** — these are smoke tests, not visual regression tests.

> **Defer.** Real component breakpoint behaviour (navbar collapse, table →
> card transformation, drawer-vs-sidebar nav) is pending the component
> library work. Per-component visual regression tests and RTL-specific
> viewport assertions land in a follow-up batch.

---

## Deferred

The following are intentionally out of scope for R0221 / R0222 and will land in
follow-up batches:

* **Real HG 677/2025 palette / typography import.** Replace the interim HEX
  values once the official HG 677/2025 PDF is parsed and the exact MD
  e-Government brand spec is locked.
* **Component library** — buttons, form inputs, tables, navigation, breadcrumbs,
  tabs, alerts. These will live in `BlazorCN` and consume the tokens defined
  here.
* **Per-component visual catalogue** (Storybook-equivalent) — a static
  documentation site that renders every component variant against the token
  sheet for design review.
* **Light-grey neutral refinement.** The neutral 50/100/200 stops are currently
  pragmatic defaults; future tuning will align them with the exact HG 677
  surface hierarchy.
* **Print stylesheet.** Print-specific token overrides (forcing black-on-white
  text, neutralising shadows) are not part of this batch.
* **Real component breakpoint behaviour** pending the component library.
* **Per-component visual regression tests** (pixel-perfect screenshot diffs).
* **RTL-specific viewport tests** — arrive with the RTL language pack work.
