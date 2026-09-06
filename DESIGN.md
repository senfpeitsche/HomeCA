---
version: alpha
colors:
  primary: "#594AE2"
  canvas: "#F8FAFC"
  surface: "#FFFFFF"
  ink: "#1E293B"
  muted: "#64748B"
typography:
  display:
    fontFamily: "Georgia, serif"
  body:
    fontFamily: "Inter, ui-sans-serif, system-ui, sans-serif"
rounded:
  DEFAULT: "5px"
spacing:
  page: "52px"
components:
  panel:
    border: "1px solid #E2E8F0"
---

## Overview

HomeCA is a focused local-administration surface for homelab operators. It follows MudBlazor's familiar application shell with a quiet slate workspace and a restrained indigo primary action; it should feel like a dependable local administration tool, not a branded cloud dashboard.

## Colors

Indigo marks the primary action and active navigation. Slate carries secondary information and chrome. Success uses blue rather than green; warnings, errors and information retain distinct semantic tones. The light and dark palettes preserve this hierarchy.

## Typography

The system sans face carries both page hierarchy and dense operational data, following MudBlazor defaults for familiar scanning and controls.

## Layout

The desktop shell has a MudBlazor app bar and responsive navigation rail; on narrow screens it follows the component's drawer behavior. Tables own their horizontal overflow.

## Elevation & Depth

Panels are flat, separated by MudBlazor's quiet divider. Dialogs use the shared component backdrop.

## Shapes

Use MudBlazor's compact four-pixel radius consistently.

## Components

Use MudBlazor components and their default control hierarchy. The global theme toggle switches the whole shell between accessible light and dark palettes. Status is communicated by text and semantic color. Forms keep errors inline.

## Do's and Don'ts

Do keep sensitive data out of persistent browser storage. Do not use green in the interface; use the defined blue success tone instead.
