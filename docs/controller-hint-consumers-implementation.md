# Controller hint consumers

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

`ControllerHintConsumerPatches` attaches the retained controller prompt behavior
to the original game GUI without injected fields or a shared Optimum contract.

- Original world-interaction `DrawHotkey` draws the retained circle/rounded-key
  glyph when the owned snapshot has an active controller mapping. Otherwise the
  original mouse/keyboard drawing body runs. Font/layout, plus spacing, outline,
  fill and label math come from the retained glyph implementation.
- Original mouse/key combination loaders decorate their fresh ConfigItem labels
  through the owned `ControllerHints.LabelFor`, preserving original codes,
  keyboard mappings, error checks, sorting and hotkey capture behavior.
- A ConditionalWeakTable supplies GUI-owned listeners for snapshot changes.
  The snapshot keeps only weak delegate references; the listener keeps a weak
  GUI reference. Refresh invokes the original `ReLoadKeyCombinations` only when
  a composed config list exists and no item/hotkey capture is active.
- The required `graphics-controller-hints` group validates typed original targets,
  installs under its own Harmony owner and removes with the startup transaction.
  Inactive routing preserves the original GUI bodies.

Provenance: retained `DrawWorldInteractionUtil.cs.patch` glyph body and
`GuiCompositeSettings.cs.patch` label/subscription behavior at baseline
`386e0d05386d0b228b439d09aeca851428f7bbf3`. Original GUI lifetime and list refresh
remain with the original assemblies.

Hint consumers are connected in source. Visible glyphs, changing input source,
remapping refresh, capture and GUI collection/lifetime remain unverified. Analog
movement consumers/negotiation, shader override compatibility, native runtime
packaging and ordinary mod settings/world integration are still unfinished.
