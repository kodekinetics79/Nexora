---
name: Nexora
description: Evidence-led enterprise commercial operations from governed intake to collected cash.
colors:
  primary-action: "#075dcc"
  primary-action-hover: "#064da9"
  evidence-navy: "#08172a"
  completion-teal: "#20c7b5"
  canvas: "#f8fafc"
  paper: "#ffffff"
  ink: "#0f172a"
  muted-ink: "#64748b"
  evidence-rule: "#35506f"
  field-border: "#aeb8c7"
  dark-paper: "#1e293b"
  dark-ink: "#f1f5f9"
typography:
  display:
    fontFamily: "Cambay, Source Sans 3, sans-serif"
    fontSize: "clamp(2.25rem, 4vw, 3.5rem)"
    fontWeight: 700
    lineHeight: 1.08
    letterSpacing: "-0.025em"
  headline:
    fontFamily: "Cambay, Source Sans 3, sans-serif"
    fontSize: "clamp(1.75rem, 3vw, 2.25rem)"
    fontWeight: 700
    lineHeight: 1.1
    letterSpacing: "-0.02em"
  title:
    fontFamily: "Source Sans 3, system-ui, sans-serif"
    fontSize: "1.25rem"
    fontWeight: 700
    lineHeight: 1.3
  body:
    fontFamily: "Source Sans 3, system-ui, sans-serif"
    fontSize: "1rem"
    fontWeight: 400
    lineHeight: 1.5
  label:
    fontFamily: "Source Sans 3, system-ui, sans-serif"
    fontSize: "0.875rem"
    fontWeight: 600
    lineHeight: 1.2
    letterSpacing: "0.02em"
rounded:
  compact: "4px"
  control: "8px"
  navigation: "10px"
  card: "12px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "16px"
  lg: "24px"
  xl: "32px"
  2xl: "48px"
components:
  button-primary:
    backgroundColor: "{colors.primary-action}"
    textColor: "{colors.paper}"
    typography: "{typography.label}"
    rounded: "{rounded.control}"
    padding: "9px 16px"
    height: "44px"
  button-primary-hover:
    backgroundColor: "{colors.primary-action-hover}"
    textColor: "{colors.paper}"
    typography: "{typography.label}"
    rounded: "{rounded.control}"
    padding: "9px 16px"
    height: "44px"
  input-outlined:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    typography: "{typography.body}"
    rounded: "{rounded.control}"
    padding: "16px 14px"
    height: "58px"
  navigation-active:
    backgroundColor: "{colors.primary-action}"
    textColor: "{colors.paper}"
    typography: "{typography.label}"
    rounded: "{rounded.navigation}"
    padding: "10px 16px"
    height: "44px"
  card-standard:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    rounded: "{rounded.card}"
    padding: "16px"
  chip-complete:
    backgroundColor: "{colors.completion-teal}"
    textColor: "{colors.evidence-navy}"
    typography: "{typography.label}"
    rounded: "{rounded.control}"
    padding: "4px 8px"
---

# Design System: Nexora

## Overview

**Creative North Star: "The Governed Ledger"**

Nexora makes consequential commercial work feel traceable before it feels decorative. Its visual world is assured, precise, and evidence-led: deep navy establishes chain-of-custody context, quiet paper surfaces hold the operator's task, and restrained blue and teal identify action and verified completion. Dense records stay legible through disciplined rules, alignment, and typography rather than ornamental effects.

The system should remain calm under real operational load. Authentication demonstrates the world with an illustrative ledger, but that split composition is a surface expression rather than a universal page template; new screens should reveal evidence, ownership, status, and lineage in the structure that best fits their task.

**Key Characteristics:**

- Evidence-led hierarchy with clear ownership, status, and lineage.
- Cambay display type paired with Source Sans 3 operational text.
- Navy, white, cobalt, and teal used in stable semantic roles.
- Restrained flat surfaces, fine rules, and solid actions.
- Compact enterprise density without sacrificing 44px targets or visible focus.

## Colors

The palette behaves like a commercial record: dark evidence context, quiet working surfaces, one governed action color, and one verified-completion color.

### Primary

- **Governed Cobalt:** Use `primary-action` for the clearest next action, active navigation, and focused task progression; use `primary-action-hover` only for its hover state.

### Secondary

- **Verified Teal:** Use `completion-teal` for completed, reconciled, posted, or otherwise verified states. It is not a second call-to-action color.

### Neutral

- **Evidence Navy:** Use `evidence-navy` for evidence context, lineage, and dark operational regions.
- **Working Canvas and Paper:** Use `canvas` for the application ground and `paper` for contained work surfaces.
- **Operational Ink:** Use `ink` for primary copy and `muted-ink` for supporting text that must remain readable.
- **Evidence Rule:** Use `evidence-rule` to separate dense records without introducing extra cards.
- **Field Border:** Use `field-border` to keep inputs visibly bounded at rest.
- **Dark Surfaces:** Use `dark-paper` and `dark-ink` when the interface is inverted; preserve the same hierarchy rather than inventing a different palette.

### Named Rules

**The Evidence Before Accent Rule.** Navy holds evidence, blue advances work, and teal confirms governed completion; never exchange those roles for decoration.

## Typography

**Display Font:** Cambay (with Source Sans 3 and sans-serif fallbacks)

**Body Font:** Source Sans 3 (with system-ui and sans-serif fallbacks)

**Character:** Cambay gives Nexora an assured, recognisable voice at moments of orientation. Source Sans 3 carries dense records, labels, controls, and long operational sessions with neutral clarity.

### Hierarchy

- **Display** (700, responsive 36–56px, 1.08): Product and surface-defining statements only; keep measures compact and deliberate.
- **Headline** (700, responsive 28–36px, 1.1): Page titles and major task headings.
- **Title** (700, 20px, 1.3): Card, section, and workflow group titles.
- **Body** (400, 16px, 1.5): Instructions, explanations, record details, and form copy; keep long prose near 65–75 characters per line.
- **Label** (600, 14px, 0.02em tracking): Controls and compact operational labels. Uppercase micro-labels may increase tracking, but never at the expense of scanning.

### Named Rules

**The Two Voices Rule.** Cambay names and frames the work; Source Sans 3 operates it. Do not use the display face as dense data text.

## Layout

Use an 8px base rhythm, with 4px reserved for compact alignment and 16–32px for normal grouping. Pages should reveal the next governed action first, place evidence and restrictions near the decision they affect, and use dividers or alignment before introducing another container.

The authenticated workspace follows a seven-stage commercial navigation spine in stable journey order: Inbox → Leads → RFQs → Quotes → Orders → Fulfilment → Receivables. Permission filtering may remove inaccessible destinations, but it must not reorder the remaining stages. Setup and the searchable screen directory sit outside that daily spine.

At 1200px and wider, navigation is persistent at 280px and may collapse to an 88px icon rail. Below 1200px, it becomes an overlay so the working canvas retains its width. Content must remain usable from 320px upward, avoid horizontal scrolling, and condense supporting evidence before hiding brand or primary action context.

## Elevation & Depth

Nexora is flat by default. Tonal contrast, one-pixel rules, and deliberate grouping create depth at rest; shadows are reserved for raised menus, selected navigation, and high-emphasis action feedback. No surface uses gradients, glass, or blur as its identity. The shell's translucent top bar is functional chrome, not a pattern to repeat through page content.

### Shadow Vocabulary

- **Action Lift** (`0 10px 24px -16px rgba(9, 105, 232, 0.8)`): A restrained lift for the highest-emphasis solid action.
- **Selected Navigation** (`0 10px 15px -3px rgba(7, 93, 204, 0.3)`): A small state shadow for the current rail item.
- **Overlay** (`0 10px 40px rgba(0, 0, 0, 0.2)`): Menus and other temporary surfaces above the shell.

### Named Rules

**The Flat-by-Default Rule.** If hierarchy can be expressed with spacing, tone, or a fine rule, do not add a shadow.

## Shapes

Controls use disciplined 8px corners; standard cards use 12px corners; navigation rows use 10px corners; compact evidence frames may tighten to 4px. Circles are semantic exceptions for avatars, stage markers, and status indicators—not a general decorative motif. Borders remain visible on fields and data regions. Every interactive target is at least 44px in both dimensions.

## Components

### Buttons

- **Shape:** Solid and assured, with 8px corners and a 44px minimum target.
- **Primary:** Governed cobalt with high-contrast white text and no gradient. Full-width task actions may grow taller when the task benefits, while preserving the same radius and type treatment.
- **Hover / Focus:** Darken one step on hover. Use a visible three-pixel focus outline or ring, never a color-only shift.
- **Secondary / Ghost:** Keep the surface flat and the label readable; reserve these variants for recovery, navigation, or lower-priority actions.

### Chips

- **Style:** Compact, plainly labelled status indicators. Completion uses verified teal; warnings and blockers use accessible semantic colors rather than repurposing cobalt.
- **State:** Never communicate selected or completed state through color alone; pair color with text, icon, weight, or `aria-current` / `aria-checked` semantics.

### Cards / Containers

- **Corner Style:** Restrained 12px corners.
- **Background:** Paper on the quiet canvas, or dark paper in dark mode.
- **Shadow Strategy:** Flat at rest; use a one-pixel border and the elevation rules above.
- **Border:** Low-contrast but persistent, strong enough to distinguish adjacent records and controls.
- **Internal Padding:** Start at 16px and increase by the spacing scale when the content needs a slower reading rhythm.

### Inputs / Fields

- **Style:** Persistent outline, paper background, 8px corners, clear label, and a 58px standard field height on high-attention forms.
- **Focus:** Shift the border to governed cobalt and add a visible three-pixel ring.
- **Error / Disabled:** Keep explanatory text readable, connect messages to their fields, and do not reduce disabled guidance to low-contrast grey.

### Navigation

The daily rail expresses the seven-stage commercial spine in journey order. Active rows use solid accessible primary color, high-contrast text, stronger weight, and `aria-current`; inactive rows stay quiet. At 1200px and wider the rail persists and may collapse, while smaller viewports use an overlay drawer with explicit open and close state.

## Do's and Don'ts

### Do:

- **Do** show evidence, owner, status, and the next governed action in the same decision context.
- **Do** use Cambay for orientation and Source Sans 3 for operational reading.
- **Do** preserve solid actions, persistent field borders, visible focus, and 44px minimum targets.
- **Do** keep the seven commercial navigation stages in their journey order across roles and breakpoints.

### Don't:

- **Don't** use gradients, glass effects, animated scanner motifs, or generic AI spectacle.
- **Don't** turn every evidence group into a floating card when alignment and fine rules are clearer.
- **Don't** use teal as a general accent or blue as proof of completion.
- **Don't** copy the login page's split composition into unrelated operational screens.
