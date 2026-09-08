# Brand assets

Masters for the HomeCA visual identity. Everything under
`src/HomeCA.Service/wwwroot/brand/` and `docs/assets/` is derived from these
files — edit here, then regenerate.

## Files

| File | Size | Use |
|---|---|---|
| `icon.png` | 1254×1254 | Icon only. Source for favicon and app icons. |
| `icon-alt.png` | 1254×1254 | Alternate icon, narrower shield. Unused; kept as a variant. |
| `logo-horizontal.png` | 2172×724 | Wordmark, no tagline. Source for the app bar logo. |
| `logo-horizontal-tagline.png` | 2172×724 | Wordmark with `HOMELAB PKI · by senfpeitsche`. Source for the login screen. |
| `logo-stacked.png` | 1254×1254 | Icon above wordmark. Currently unused. |
| `hero.png` | 1672×941 | Hero with background. Source for the README banner and social preview. |

`icon.png` is the one in use; `icon-alt.png` is kept as an alternative.

## Palette

Sampled from `icon.png`:

| Role | Hex | Contrast on white | Contrast on `#111827` |
|---|---|---|---|
| Navy | `#024381` | 9.89:1 | 1.79:1 |
| Teal | `#049595` | 3.66:1 | 4.85:1 |
| Green | `#30A63F` | 3.16:1 | 5.62:1 |
| Amber | `#FCBA03` | 1.73:1 | 10.28:1 |

Navy and amber each fail on one background, which is why the UI palette in
`Components/Layout/MainLayout.razor` uses lightness-adjusted variants rather
than the raw brand colours, and why the wordmark ships in two renderings.

## Regenerating derived assets

Requires ImageMagick 7 (`magick`). Run from the repository root.

### Favicon — shield crop, not the full icon

The full icon blurs at 16–32 px; the crop keeps the checkmark readable.

```bash
magick design/icon.png -background white -alpha remove \
  -gravity center -crop 46%x46%+0-20 +repage /tmp/shield.png
for s in 16 32 48; do magick /tmp/shield.png -resize ${s}x${s} -strip /tmp/sh-$s.png; done
magick /tmp/sh-16.png /tmp/sh-32.png /tmp/sh-48.png \
  src/HomeCA.Service/wwwroot/brand/favicon.ico
```

### App icons

`apple-touch-icon` drops the alpha channel because iOS composites transparency
onto black.

```bash
B=src/HomeCA.Service/wwwroot/brand
magick design/icon.png -resize 192x192 -colors 256 -strip -define png:compression-level=9 $B/icon-192.png
magick design/icon.png -resize 512x512 -colors 256 -strip -define png:compression-level=9 $B/icon-512.png
magick design/icon.png -background white -alpha remove -alpha off -resize 180x180 -strip $B/apple-touch-icon.png
```

### Wordmarks, light and dark

The dark renderings replace navy with white. Fuzz must stay well below 19% —
that is the RGB distance between navy and teal, and a wider tolerance swallows
the teal `CA` and the shield. 8% is safe. The tagline is a separate slate
(`#3C526B`) and needs its own pass.

```bash
B=src/HomeCA.Service/wwwroot/brand
magick design/logo-horizontal.png -trim +repage -resize x96 \
  -colors 256 -strip -define png:compression-level=9 $B/logo.png
magick design/logo-horizontal.png -trim +repage -fuzz 8% -fill white -opaque "#024381" \
  -resize x96 -colors 256 -strip -define png:compression-level=9 $B/logo-dark.png

magick design/logo-horizontal-tagline.png -trim +repage -resize 720x \
  -colors 256 -strip -define png:compression-level=9 $B/logo-wide.png
magick design/logo-horizontal-tagline.png -trim +repage \
  -fuzz 8% -fill white -opaque "#024381" \
  -fuzz 10% -fill "#94A3B8" -opaque "#3C526B" \
  -resize 720x -colors 256 -strip -define png:compression-level=9 $B/logo-wide-dark.png
```

Light and dark variants must keep identical pixel dimensions — the markup
declares intrinsic `width`/`height` once for both.

### README banner and social preview

`-colors 256` cuts the file size by about four fifths with no visible loss. The
social preview is cropped rather than squashed, because GitHub wants 2:1 and the
hero is 16:9.

```bash
magick design/hero.png -resize 1280x -colors 256 -strip \
  -define png:compression-level=9 docs/assets/banner.png
magick design/hero.png -resize 1280x -gravity center -crop 1280x640+0+0 +repage \
  -colors 256 -strip -define png:compression-level=9 docs/assets/social-preview.png
```

`docs/assets/social-preview.png` is not referenced by any page. Upload it under
*Settings → General → Social preview* so links to the repository render with it.
