---
version: alpha
colors:
  forest: "#12332e"
  canvas: "#edf1ef"
  surface: "#ffffff"
  signal: "#e0d96e"
  ink: "#17212b"
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
    border: "1px solid #dce5e0"
---

## Overview

HomeCA is a focused local-administration surface for homelab operators. The signature is the deep-forest service shell with the pale verification accent; it should feel like an instrument panel, not a cloud dashboard.

## Colors

Forest anchors navigation and trust context. Signal is reserved for an explicit issuance or renewal action. White is a document-like workspace for certificates and CAs.

## Typography

Georgia is reserved for page hierarchy and CA identity; the system sans face carries dense operational data.

## Layout

The desktop shell has a persistent navigation rail; on narrow screens it becomes a horizontal route list. Tables own their horizontal overflow.

## Elevation & Depth

Panels are flat, separated by a quiet border. The issuance dialog alone uses a dimmed backdrop.

## Shapes

Use the compact five-pixel radius to avoid decorative softness.

## Components

Every action is a native button. Status is communicated by text and semantic color. Forms keep errors inline.

## Do's and Don'ts

Do keep sensitive data out of persistent browser storage. Do not use bright accents except for committed actions or warnings.
