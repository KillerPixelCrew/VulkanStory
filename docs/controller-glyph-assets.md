# Controller glyph assets — 2026-10-01

Requested during the renderer/SDL port. The current ControllerGlyphs helper uses
ordinary text (including Unicode PlayStation shapes) and draws its own badge.
It does not bundle a controller font or icon set. SDL button labels and retained
profile remaps already feed the hints, so replacement assets must preserve that
mapping rather than assume Xbox A/B/X/Y positions for every controller.

## Sources reviewed

| Candidate | Format and coverage | Distribution |
| --- | --- | --- |
| [PromptFont by Shinmera](https://git.tymoon.eu/shinmera/promptfont) | TTF/OTF, glyph metadata and C# constants; Xbox, Sony, Nintendo and generic categories, sticks, D-pad, triggers and keyboard/mouse | SIL Open Font License; ship the license and author attribution |
| [Kenney Input Prompts](https://kenney.nl/assets/input-prompts) | Controller icon assets, including Xbox, PlayStation, Switch and Steam families | CC0 listed by the creator; retain package provenance |

Recommendation: PromptFont for scalable inline/button hints. It supplies codepoint
metadata and controller families, avoiding dependence on the game's ordinary
font having Unicode symbol coverage. Kenney is a suitable alternative for image
badges. Recommendation is based on coverage and packaging described by upstream;
neither asset set has been imported or rendered in this game yet.

## Integration work

Pin a release and retain original font, glyph metadata, license and attribution.
Load the font privately through the existing game GUI/font backend or rasterize
glyphs to a bounded cache through the existing Skia/Cairo path; no user OS font
installation. Use separate glyph spans so prose keeps the game's text font.
Map SDL labels/controller family and remapped physical controls to glyph IDs,
including Nintendo letter ordering and Sony shoulder/trigger names. Retain text
fallback for unsupported buttons/axes or an unavailable font. Recompose hints
when the active controller, input source or profile changes.

No font installed, new image assets downloaded, build/test run or game launched.
Correct in-game rendering remains implementation and acceptance work.

## Private glyph implementation — 2026-10-01

Imported unmodified PromptFont TTF and glyph metadata version 1.15.0 from the
CDNJS distribution documented by the author. License and attribution/source
hashes are retained under packaging/notices/promptfont and staged into the client
package. Earlier research-only statements above describe the preceding checkpoint.

Game embeds the font/metadata. ControllerPromptFont loads SKTypeface privately,
maps semantic button labels to metadata names, rasterizes antialiased white alpha
glyphs and masks them into Cairo with the hint's existing color. This follows the
game's BGRA premultiplied surface convention. A locked cache retains at most 64
glyph/size surfaces; graphics detachment disposes it and the typeface.

World-interaction hints and the controller badge helper now attempt this renderer
before their existing text drawing. Unknown controls, unsupported font glyphs and
load failures fall back to readable labels. Font failures are logged once.
No symbol codepoints are inserted into ordinary prose/settings text; its game
font remains in use. Existing controller/profile change notifications remain.

SDL-derived labels preserve face-button remaps. Shoulder/back/menu labels now
distinguish Nintendo L/R and minus/plus from Xbox LB/RB/View/Menu and Sony
L1/R1/Share/Options. Trigger and stick-axis labels retain their existing mapping.
Unidentified devices retain the current generic/Xbox-shaped label fallback;
Steam Input label overrides and per-device art selection are not claimed here.

Implementation only: no builds, tests, raster previews, packages, font OS
installation, deployment or game launches. Compilation and actual in-game glyph
appearance remain unverified.

## Remapping UI completion

Source review confirmed action-remapping rows already use the controller badge
helper. Axis rows now also draw the corresponding stick-direction/trigger glyph,
using the same semantic axis mapping as world hints. Axis names and explicit
direction buttons remain readable, and profile changes recompose the panel.
The font rasterizer now scales wide glyph bounds to fit the badge before drawing,
so shoulder/trigger art cannot be cropped by its square cache surface.
Source-only; no builds, previews, tests or game runs. UI appearance remains open.
