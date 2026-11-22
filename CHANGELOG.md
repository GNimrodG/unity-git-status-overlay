# Changelog

## 1.0.0 - 2026-11-22

First release.

- Project window: U/A/M/R/! letters for changed assets (asset and `.meta` as one item), folder dots, deleted files
  listed in the nearest existing folder's tooltip, files inside dirty submodules.
- Hierarchy: changed and new GameObjects in open scenes and the prefab stage, from the scene YAML diff against HEAD,
  including the changed child inside prefab instances; deleted objects and an unsaved-changes hint on the scene row.
- Hierarchy: prefab override glyphs (property overrides, added/removed components, added GameObjects).
- Inspector: changed properties get a gutter bar with an old → new tooltip; changed or new components get M/U in their
  header. Covers scene objects, prefab-instance overrides, `[SerializeReference]` data and selected modified assets.
- Settings under Tools → Git Status and Preferences → Git Status Overlay.
