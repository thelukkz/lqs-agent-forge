# Design System — PDF Report Agent

## Color Palette

| Token | Hex | Usage |
|---|---|---|
| `--bg` | `#0d0d0d` | Page background |
| `--surface` | `#1a1a1a` | Cards, tables, code blocks |
| `--border` | `#2a2a2a` | Dividers, table borders |
| `--text` | `#e5e5e5` | Primary body text |
| `--muted` | `#888888` | Captions, headers, footers |
| `--accent` | `#6b9fff` | Links, note borders |
| `--success` | `#4ade80` | Success tags |
| `--warning` | `#fbbf24` | Warning tags |
| `--error` | `#f87171` | Error tags |

## Typography

**Fonts:** Lexend (body/headings) · IBM Plex Mono (code/data)

**Scale:**
- `h1`: 32px / 700 / line-height 1.2 (cover: 42px)
- `h2`: 26px / 600
- `h3`: 21px / 500
- `h4`: 18px / 500
- Body: 14px / 400 / line-height 1.7
- Captions: 12px
- Code: 13px

**Line length:** `max-width: 720px` on `p`, `ul`, `ol`

## Page Layout

Every page is a `.page` div (210mm wide, min 297mm tall, padding 20mm).

```html
<!-- Cover page (no header) -->
<div class="page">
  <div class="cover-title">
    <h1>Title</h1>
    <p>Subtitle</p>
    <p style="color: var(--muted); font-size: 12px; margin-top: 2rem;">Date</p>
  </div>
  <div class="page-footer">
    <span>Document Title</span>
    <span>1</span>
  </div>
</div>

<!-- Content page -->
<div class="page">
  <div class="page-header">
    <span>Document Title</span>
    <span>Section Name</span>
  </div>
  <div class="content">
    <!-- content here -->
  </div>
  <div class="page-footer">
    <span>Document Title</span>
    <span>N</span>
  </div>
</div>
```

**Page breaks:**
- `page-break-after: always` on `.page` (last page auto)
- `page-break-after: avoid` on all headings
- `page-break-inside: avoid` on `.card`, `table`, `figure`, `pre`, `.note`

## Components

### Card
```html
<div class="card">
  <h3>Title</h3>
  <p>Content.</p>
</div>
```

### Table
```html
<table>
  <thead><tr><th>Column</th><th>Value</th></tr></thead>
  <tbody>
    <tr><td>Row</td><td>Data</td></tr>
  </tbody>
</table>
```

### Note / Callout
```html
<div class="note">
  <strong>Note:</strong> Important information.
</div>
```

### Tags / Badges
```html
<span class="tag">default</span>
<span class="tag success">done</span>
<span class="tag warning">pending</span>
<span class="tag error">failed</span>
```

### Two/Three Column Grid
```html
<div class="two-col">
  <div><!-- left --></div>
  <div><!-- right --></div>
</div>

<div class="three-col">
  <div><!-- col 1 --></div>
  <div><!-- col 2 --></div>
  <div><!-- col 3 --></div>
</div>
```

### Figure with Image
```html
<figure>
  <img src="C:/absolute/path/to/image.png" alt="Description">
  <figcaption>Caption text.</figcaption>
</figure>
```

## Print Rules

- `@page { size: A4; margin: 0; }` — margins controlled by `.page` padding (20mm)
- `print-color-adjust: exact` — preserves dark colors in Chromium
- `print_background: true` — **must** be set in PuppeteerSharp `PdfOptions`
- Images require absolute filesystem paths in `src` (not relative)

## Content Philosophy

> Perfection is achieved not when there is nothing more to add, but when there is nothing left to remove.

- One idea per paragraph
- Tables over bullet lists when data has structure
- Remove filler — fewer, stronger points
- Each page: one clear focus
