# Buddy Art Direction

## Decision

Hermes uses a local WinUI vector avatar for the Buddy surface instead of ASCII art or a runtime web image. The companion can be crafted by species, palette, eyes, and accessory, and those choices are persisted with the buddy soul.

## Research Notes

- DiceBear is the closest fit for the product direction: it is an open-source SVG avatar library with many configurable avatar styles. Several relevant styles are CC0, including Pixel Art Neutral, Shapes, Lorelei, Notionists, Open Peeps, and Thumbs. DiceBear code is MIT licensed, while style licenses vary by collection.
- Quaternius and Kenney-style asset packs are excellent CC0 sources for game-grade 3D/2.5D assets, but they are heavier than this UI needs and introduce asset pipeline questions for a desktop settings surface.
- OpenGameArt/itch.io CC0 sprite packs are useful references, but most are fixed sprites rather than a clean character-crafting system.

## Implementation Choice

The first Hermes pass uses native WinUI shapes rather than vendored remote art. That keeps the app offline-safe, avoids shipping a large asset pack, and lets the same Buddy data drive both the full page and side panel.

The visual style intentionally borrows the avatar-system pattern from open-source avatar libraries: small composable parts, deterministic defaults, user-tunable traits, and no network dependency at render time.

## Sources Checked

- DiceBear styles and licenses: https://www.dicebear.com/styles/
- DiceBear Pixel Art Neutral, CC0 1.0 style: https://www.dicebear.com/styles/pixel-art-neutral/
- DiceBear repository, MIT code: https://github.com/dicebear/dicebear
- Quaternius free assets: https://quaternius.com/
