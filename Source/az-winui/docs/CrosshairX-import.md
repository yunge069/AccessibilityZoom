# Crosshair X import feasibility

Checked on 7 November 2026. The investigation below informed the static crosshair
imports implemented in version 4.3. Crosshair X settings remain read-only.

## Image import

Crosshair X's official Steam listing advertises crosshair image export:
https://store.steampowered.com/app/1366800/Crosshair_X/

The local Crosshair X library contains three embedded PNG layers. All decoded
successfully and use 8-bit RGBA (PNG color type 6), confirming that transparency
is represented in real local designs. Two are 5 x 5 pixels; one is 1170 x 2080.
An importer must handle tiny images without unwanted smoothing and large images
without distorting their aspect ratio.

Crosshair X also caches the current crosshair as a standalone SVG drawing,
alongside its model, in the persistentCrosshair.crosshairImage setting. The
current cached SVG parses successfully and has a 20 x 20 viewBox. Its global
scale/position settings are separate from the SVG artboard.

PNG is the simplest interchange format for static designs. Import a copy into
Accessibility Zoom's data folder, retain alpha, and allow size/opacity adjustment.
An image import preserves appearance but does not provide individual line/dot
editing.

## Share-code import

The open-source Reticly importer documents Crosshair X short codes and share URLs:
https://github.com/corund207/Reticly/blob/main/src/Import/CodeImporter.cs

It resolves the code through this public GET endpoint:

    https://8yy0fp6ycd.execute-api.us-east-1.amazonaws.com/Prod/fetchsharedcrosshair?linkId=<code>

An independent request using the public example code xe4lbu6zh8 returned HTTP 200.
The response contained crosshairModel, a base64-encoded JSON layer array. Decoding
and parsing succeeded; the example contained 33 model layers. No account token
or local user data was sent in this request.

Therefore code import is technically feasible. It requires a network lookup,
followed by local storage and static rendering of the design data. This is an
observed service endpoint, not a documented compatibility contract; keep an
offline image/JSON import path and clear errors for unavailable/removed codes.

## Direct import of the existing local library

Local source directory:

    %APPDATA%\CrosshairX\crosshair-store

The directory currently contains 24 crosshair documents plus a manifest.
Every crosshair document parsed as JSON and contains a model array. The documents
contain 186 model layers and three image layers in total. Observed shapes include
rectangle/triangle lines and square/circle/diamond/ring dots, plus outlines,
positions, layer scale, and firingOptions.

This offers a useful third entry point: import the user's saved Crosshair X
crosshairs directly, without a manual export or network request. Read/copy the
design documents; do not write back into the Crosshair X library. Preserve the
source model so an importer can be improved without losing the original data.

## Suggested implementation scope

- Import image: start with transparent PNG; add standalone SVG if supported by
  the chosen renderer.
- Import code/link: fetch the design once, validate it, and save it locally.
- Import saved Crosshair X designs: browse the local JSON library, with previews.
- Render the static base design; firing/animation fields do not need playback.
- Show the crosshair at the selected screen's center, with configurable size,
  opacity, and a toggle included in the existing binding settings.
- Compensate for the Windows full-screen zoom transform so the crosshair stays
  centered and its display size can remain fixed while zoom changes.

Implementation update: version 4.3 adds the three import routes, static geometry
rendering, saved design/preferences storage, monitor/size/opacity controls, and a
remappable F7 toggle. A public code and local designs were rendered successfully.
Automated checks also exercise transparent PNG data, offline persistence,
placement math and a hidden native alpha surface. Visible overlay behavior and
picker/dialog interaction await a manual check. No UI automation was used.
