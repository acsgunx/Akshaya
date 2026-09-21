# Akshaya design rationale

This document is the "why" behind `tailwind.css`, `_theme.scss` and
`styles.scss`, and behind a handful of interaction rules that apply across
every feature. Read it before adding a component — most "why does this look
different from the rest of the app" questions are answered here.

## Where the styling lives

Screens are written in **Tailwind utility classes**, in the template. Three
global files carry everything that is not a class on an element.

(The four blotter screens are the exception — see "Two table libraries were
tried" below. They still use the primitives quarantined at the bottom of
`styles.scss`.)

- **`tailwind.css`** — the Tailwind entry AND the single source of truth for
  the design tokens. Every colour, radius, shadow and numeric column width is
  declared once, in its `@theme static` block, which does double duty: Tailwind
  generates the utilities from it (`--color-buy` → `bg-buy`, `text-buy`,
  `border-buy`), and the same custom property is readable at runtime, which is
  how the TradingView chart paints its candles in the app's own colours. Light
  and dark come from a single set of `light-dark()` declarations switched by
  `color-scheme`, so there is no second theme block to keep in step.

  The `static` matters: without it Tailwind emits only the variables some
  generated utility happens to reference, and anything read at runtime or from
  plain CSS silently resolves to nothing.

- **`_theme.scss`** — one `mat.theme()` call, plus `--ak-*` aliases pointing at
  the variables above. It declares no colours of its own. Dark is the default;
  light and the colour-blind-safe buy/sell pair are stored preferences, applied
  by `AppearanceStore` as `[data-theme]` and `[data-cvd-safe]` on `<html>`.

- **`styles.scss`** — the reset, plus two categories that a utility class
  genuinely cannot cover:
  - **Safety nets.** `tabular-nums` on every `td`/`th`, and a `:focus-visible`
    ring on everything. A utility only protects the element somebody remembered
    to put it on; these protect the ones they forgot, and both are correctness
    guarantees rather than styling.
  - **Library overrides.** Angular Material renders its own DOM, and CDK
    overlays render outside the component that opened them. (AG Grid needs
    none: it is themed entirely through its own theme object.)

**A component stylesheet should be empty.** The migrated screens' are nearly
so, and each non-empty one says at the top which of these three cases it is:

1. a `:host` rule, which has no template element to carry a class;
2. a state driven by a **bound** class — `routerLinkActive`'s `active`, the
   order ticket's `.selected`, the wizard's `.ak-saved-option--active` — where
   base and modifier have to be composable selectors, written with `@apply` so
   they stay in Tailwind's vocabulary; or
3. a `color-mix()` against a token, which has no utility equivalent.

If you are writing a card, a page header, a status pill or a spinner row in a
component stylesheet, it is a utility string in the template instead.

## Which component library owns what

**Angular Material owns every control**: buttons, dialogs, menus, form fields,
tabs, snackbars, tooltips, checkboxes. There is no second component library.

### Two table libraries were tried and both were rejected

The four blotter screens (watchlist, positions, holdings, orders) are still on
their original CDK-virtual-scroll + CSS-grid implementation. That is not for
lack of trying:

- **PrimeNG 22** is no longer open source. It is a commercial PrimeTek product
  requiring a license key, and without one it paints an "Invalid PrimeUI
  License" badge over the running application. Free "Community License"
  eligibility is capped at <$1M revenue, <5 developers, <10 employees.
- **AG Grid Community** (MIT, no key) replaced it, and its **cell renderers
  proved completely non-functional in this app**. No output from an Angular
  component renderer, from a renderer registered by name in the `components`
  map, or from a plain JS `ICellRenderer` with a `getGui()` — on both v35.1 and
  v36.1, with and without a custom theme, with and without
  `suppressAnimationFrame`. `valueGetter` text rendered fine; the moment a
  `cellRenderer` was present the cell rendered empty, silently, with no
  console error. Root cause not established; the most likely suspect is this
  app's zoneless change detection, which AG Grid's NgZone-based Angular adapter
  may not support.

Rather than ship four broken blotters, they were left on the implementation
that demonstrably works. The primitives they still need are quarantined in a
clearly marked PENDING MIGRATION block at the bottom of `styles.scss`; nothing
already migrated should reach for them.

### Expanded broker legs

Whatever replaces the current implementation must preserve one property: a
leg's quantity has to sit directly under the Qty heading and under the blended
quantity it contributes to. Today that is structural — the header, the rows and
the legs share one `--ak-cols` grid template. A sub-table with its own widths
makes the reader work out which number is which, which is the opposite of what
expanding a row is for.

Note that both candidate libraries put this behind a paywall or a rewrite: AG
Grid's master/detail is an Enterprise feature. The workaround that was built
and does work is to flatten legs into peer rows of the same height, which keeps
the alignment structural *and* keeps the list virtualisable.

## Why dark by default

A trading terminal is stared at for hours, often against other lit monitors
(charting software, news feeds), and often outside daylight hours for anyone
trading a foreign venue (an Indian trader watching NASDAQ overnight; a
Singapore trader catching the NSE open before their own market opens). Dark
reduces the luminance differential against everything else on the desk and is
what every professional trading terminal (Bloomberg Terminal, TradingView,
most broker platforms) converges on for exactly this reason.

We do **not** go pure black. Pure black (`#000`) next to saturated accent
colours (our buy/sell blue and amber, red for danger) causes visible halation
on OLED/AMOLED panels and reads as "broken" rather than "calm" — colour blooms
outward from bright glyphs against a truly black field. Our darkest surface
(`#14161a`) sits at roughly 8% luminance: dark enough for the
above, lifted enough that accent colours stay crisp.

Light mode exists (`[data-theme="light"]`) for daytime use or personal
preference, but it is an explicit, persisted user choice — never inferred from
`prefers-color-scheme`. A trading terminal changing its chrome because the
user's OS flipped to light mode for an unrelated reason (walking from a dark
room into daylight) is a surprise nobody asked for in the one app where
surprises are unwelcome.

## Buy/sell: why not red/green

Red/green is the default nearly every trading UI reaches for, and it is a bad
default: roughly 8% of men have red-green colour vision deficiency
(deuteranopia or protanopia), and red-on-dark-grey vs green-on-dark-grey is
close to the worst-case pair for them — both desaturate toward a similar dull
olive-brown at the low saturation a dark theme needs to avoid its own kind of
visual noise.

We use **blue (`#3b82f6`) for buy/long** and **amber (`#f59e0b`) for
sell/short**. This pair was chosen because:

- Blue and amber/orange sit on opposite ends of the spectrum humans perceive
  via the S-cone (blue) vs the L/M-cone difference (amber reads as warm
  regardless of which of L or M cone is missing), so the pair survives
  protanopia, deuteranopia, *and* tritanopia simulation — the three common
  forms of colour vision deficiency — as two visibly distinct hues, not just
  two distinct luminances.
- They additionally differ in perceived luminance at equal chroma (amber
  reads lighter than blue at the same saturation), so even a full
  achromatopsia (total colour blindness, very rare but real) still separates
  them by lightness alone.
- Neither is "red", so neither is visually confused with the app's actual
  danger/error colour (`--color-danger`, reserved exclusively for destructive
  actions and hard failures — never for "sell").

**Colour is never the only signal.** Every buy/sell indicator — order-ticket
toggle, position sign, watchlist change arrow — pairs its colour with a
redundant glyph (▲/▼, +/−, or the literal word "Buy"/"Sell"). A user who
disables colour entirely (forced-colours mode, a print-out, a colour-blind
user who still finds blue/amber close) loses nothing.

For the residual case, **Settings → Accessibility → "Colour-blind-safe
palette"** (`data-cvd-safe="true"` on `<html>`) swaps to a light-cyan /
dark-violet pair chosen purely for maximum perceptual distance and lightness
separation, at some cost to "does this look like a normal trading app".

## Price cells must never jitter

Any layout shift on a cell that updates several times a second — a column
getting one pixel wider because "1,482.30" became "999.85" and the glyph
widths differ — is disorienting at best and, at the moment someone is about
to click a row, a misclick hazard at worst. Three rules eliminate it:

1. **`font-variant-numeric: tabular-nums` everywhere a number can change.**
   Set globally on `td`/`th` in `styles.scss` and via Tailwind's `tabular-nums`
   utility for numbers outside tables (the order ticket's estimated value, the
   dashboard's P&L tiles). Every digit then occupies an identical advance width.
2. **Fixed `min-width` on numeric columns**, in `ch` units, as the
   `min-w-price` / `min-w-qty` / `min-w-pnl` utilities generated from
   `--spacing-price` / `--spacing-qty` / `--spacing-pnl`,
   sized for the longest realistic value (a 6-figure
   INR price with grouping, a signed 6-figure P&L). A column can only ever
   get *emptier* padding on a short number, never narrower.
3. **Flash-on-change is a background-colour animation only**
   (`animate-flash-up` / `animate-flash-down`, defined in the `@theme` block),
   never a font-weight, size or transform change — those are exactly the
   properties that alter a glyph's advance width and would reintroduce the
   jitter the first two rules just eliminated.

One level up, the same hazard applies to whole rows: **the live blotters do not
sort on live columns.** LTP, change% and order status are deliberately not
sortable, and the order blotter is not sortable at all. A table that reorders
itself while fills are arriving moves the row a trader is reaching for out from
under them, which is the misclick hazard above at row scale.

## Showing degraded and stale state

A number that stopped updating and still looks live is worse than an error
screen — it is a wrong number presented with full confidence. Every
data-bearing surface therefore carries **two independent pieces of state**
wherever it applies: connection state (is the pipe up) and data freshness (is
what's on screen current), because the two fail independently — a `Degraded`
stream can still deliver ticks late; a `Connected` stream that has not pushed
anything in ten seconds on a liquid instrument is itself a symptom.

- **`connection-status`** (shared component) renders one of `live` / `degraded`
  / `stale` / `disconnected` per connector — mapped straight onto the semantic
  tokens they mean (`--color-success`, `--color-warning`, `--color-danger`), with no
  intermediate alias layer to fall out of step — and is present everywhere that connector's data appears — the watchlist row, the
  order ticket header, the dashboard's per-venue strip. It never collapses to
  a binary "connected" dot; `degraded` (connected but behind or partially
  subscribed) is visually distinct from both `live` and `disconnected`.
- **`stale-banner`** appears above any table/chart whose underlying feed has
  not confirmed a tick within its expected cadence, and states explicitly how
  long ago the last good data arrived — never just "stale".
- **Session expiry** is a countdown, not a boolean. `connection-status` shows
  time-to-expiry once inside a warning window (configurable, defaults to 15
  minutes) so a trader is never mid-order when a broker session dies — the
  ugliest possible time to discover it.

## The keyboard model

Everything here is reachable and operable without a mouse, both because WCAG
2.2 AA requires it and because a trader placing time-sensitive orders
genuinely wants keyboard speed over pointing.

- Standard browser tab order everywhere; nothing traps focus except an open
  dialog (which returns focus to its trigger on close).
- **Order ticket**: `B` stages a buy, `S` stages a sell (only while the ticket
  itself has focus/is the active panel — never as a page-wide hotkey that
  could fire while typing a quantity), `Esc` cancels the ticket/closes the
  confirm step. Submitting is **never** bound to a single bare key — it always
  requires reaching the explicit confirm step (see below) and activating its
  button, so a stray keypress cannot send an order.
- **Kill switch**: reachable from every screen via a persistent, always-tab-
  visible control (not hidden behind a menu), and its confirmation dialog is
  the one place in the app where `Enter` does *not* submit by default — it
  requires an explicit confirm click/Space on the danger-styled button, to
  make an accidental double-Enter unable to both open and fire it.
- Every custom interactive element (connector chips, table rows that expand,
  the flash-on-change watchlist cells) is a real `button`/`role` with a
  visible `:focus-visible` ring (see `styles.scss`) — never a `div` with a
  click handler and no keyboard path.

## Small screens

Below Tailwind's `lg` breakpoint (64rem, 1024px) the app switches to a
**compact layout**. The number is not a phone size on purpose: the desktop top
bar needs ~960px before its links, the kill switch and the account link stop
overflowing, and the nine-column order blotter squeezes its instrument column
to nothing well before that. A portrait tablet is better served by the compact
layout than by a squeezed desktop.

**One breakpoint, two ways to read it.** Anything that is only styling uses
`lg:` / `max-lg:` utilities. Anything that is different *markup* — a grid row
versus a card, a virtual-scroll viewport versus a plain list — branches on
`LayoutService.compact()` (`core/layout.service.ts`), whose `COMPACT_QUERY` is
the exact complement of `lg:`. They cannot disagree about a given width. Don't
render both layouts and hide one with CSS: it doubles the DOM, and on the
watchlist it doubles every row's live subscription.

The rules the compact screens follow:

- **Navigation is a bottom tab bar**, five destinations, in the thumb's reach.
  The app has eight screens, so related ones share a tab and switch with
  `<ak-section-tabs>` at the top of each: Holdings | Positions (Portfolio),
  Orders | Fills, Account | Brokers. They stay separate routes with their own
  deep links. **The kill switch stays in the top bar**, always visible — on a
  phone the old top nav pushed it off the right edge, which is the one place
  it must never be.
- **Blotters become lists, P&L first.** A phone has room for four of the
  holdings grid's eight columns, and the ones it used to keep were the ones a
  phone user checks least. Each row leads with the symbol on the left and the
  number the user opened the app for (P&L, LTP, order status) on the right;
  supporting figures sit on a second and third line. Never drop the P&L column
  to make a table fit.
- **Symbol first, venue second.** `XNSE:HDFCBANK:Equity` truncated to fit a
  narrow cell becomes `XNSE:HD…` — the part every row shares survives and the
  part that tells rows apart is cut. Use the `akInstrument` pipe: `'symbol'`
  for the headline, `'detail'` for the venue and contract underneath.
- **Actions live behind a tap, and they are labelled.** Tapping a row expands
  it to show its broker legs and their actions (Sell, Exit, Add, Convert,
  Modify, Cancel) as buttons with words on them. The desktop's icon-only
  buttons lean on hover tooltips, and a finger cannot hover.
- **Lists scroll with the page and are not virtualised.** The CDK viewport
  needs a fixed row height and its own inner scroll box; compact rows expand in
  place, and a scroll box inside a scrolling page traps the thumb. The
  `cv-auto-[<height>]` utility (`content-visibility: auto`) lets the browser
  skip off-screen rows instead.
- **Nothing may be wider than the screen.** On a phone, overflow widens the
  layout viewport and the fixed tab bar slides off the bottom with it. The
  page content clips horizontal overflow as a safety net, but it is a net:
  size things to fit (see the fills date range, which drops Apply to its own
  row rather than squeeze three controls into 343px).

Two things are keyed on the **pointer**, not the width, because they are about
fingers rather than room:

- **Density.** Material runs at density -2 only under `(pointer: fine)` (see
  `_theme.scss`). A finger gets stock-size controls — 40px buttons and 48px
  touch targets instead of 32px — whatever the screen width.
- **Keyboard hints** (the order ticket's `B` / `S` / `Esc`) are hidden under
  `pointer-coarse:`.

And for iOS specifically: `viewport-fit=cover` with `env(safe-area-inset-*)`
padding so nothing sits under the notch or the home indicator; form fields at
16px or larger on touch, because Safari zooms the page into any smaller field
and never zooms back; and `<meta name="theme-color">` kept in step with the
top bar by `AppearanceStore`, so the browser's own toolbar blends into the app.

### Global CSS stays beneath the utilities

Tailwind emits its utilities inside `@layer utilities`, and in the cascade an
unlayered declaration beats every layered one, whatever the specificity. So
any global rule written outside a layer silently wins over every utility on
the elements it matches. That used to zero the margin of every `<p>` and
`<h1>` whatever its `mt-*` said, and pin every `<mat-icon>` at 24px in its
parent's colour whatever its `size-*` or `text-*` said. Both are fixed in
`styles.scss`, and the rules for keeping them fixed are:

- **Element defaults go in `@layer base`**, alongside Tailwind's preflight.
  The reset block in `styles.scss` is there; anything added to it must be too.
- **`<mat-icon>` takes utilities as-is** — `text-warning`, `size-[18px]
  text-[18px]`, `max-lg:hidden`. A `revert-layer` rule in `styles.scss`
  demotes Material's icon defaults below the utilities. Size an icon with both
  a box and a glyph utility (`size-[18px] text-[18px]`); either alone leaves
  the other at 24px, and the box clips the glyph.
- **Material's own components still need the `!` form.** Buttons, form
  fields, menus and chips style themselves with unlayered class rules, so a
  utility that fights one of their properties loses: `font-semibold!` on a
  `mat-stroked-button`, `px-2.5!` on the nav link. Icons *inside* those
  components are sized by Material's contextual rules for the same reason —
  leave them to it.

Two things in `styles.scss` are still unlayered. The focus ring and the
`td`/`th` tabular-nums rule are unlayered on purpose, so no utility can switch
them off. The pending-migration block (`ak-page-head`, `ak-col-*`, `ak-card`,
…) is unlayered only because it hasn't been migrated yet. A utility on the same
element as one of those classes loses on any property the class sets, so don't
mix the two. Migrate the element instead.

## Why the order ticket and broker-link wizard don't "know" a broker's name

This is a UX rule as much as an architecture one: the moment either component
special-cases a broker (`if (connectorId === 'mstock')`), the UI has silently
promised that every future broker looks like today's brokers. It doesn't —
the whole reason the backend ships a `ConnectorManifest` is that order types,
position-effect vocabulary, credential shape and login flow all vary per
broker and per jurisdiction. Both components read that manifest and render
*only* what it declares support for; see the inline comments in
`order-ticket.component.ts` and `broker-link-wizard.component.ts` for the
specific fields each one drives from.
