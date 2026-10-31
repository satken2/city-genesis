# Material resolution

These conventions assign material instances to generated faces. [Building](F_building-meshes.md), [road](G_road-meshes.md), [track](N_track-meshes.md), and [station building](Q_station-building-meshes.md) generators share them. See [material sharing](P_material-sharing.md) for names and shared instances across models.

## Material sources

Modeled components use their prefab materials. Faces without shape parts, including roofs, ground panels, gables, quays, and open-space surfaces, take material-only inputs such as `ceil`, `floor`, `generic-wall`, and `site_*`.

Common faces independent of parts use shared materials looked up by name: default `BuildingMaterial`, foundation concrete `COMMON_CONCRETE`, site soil `ConstructionDirt`, and rubble `CollapsedRubble` or `CollapsedRubbleBurned`. `BuildingMaterial` is the final fallback, so building meshes never lack a material. A material looked up by name comes from the asset pack when it has one with that name, and otherwise from directly under `Assets/Resources/` ([asset packs](X_asset-packs.md)).

Ground panels alone may be omitted. They apply only to buildings with master `draw_floor`; if no `floor` part exists, omit the panel instead of using a default material. This avoids unnecessary slabs under tile-based facilities. Station building exclusive-area ground follows the same `floor` availability rule.

## Buildings and paths

Buildings use prefab material instances directly without cloning. Buildings using the same parts share instances. Each building combines into one mesh and groups faces by material instance to reduce draws; matching names with different instances cannot combine.

Paths always use a shared pool keyed by material name. A same-named material looked up by name takes precedence. Otherwise the pool clones the part material and shifts render order one step later to avoid Z-fighting, sharing one instance per name. Intersection and sidewalk joins also use this pool instead of prefab materials directly.

Path master `body_material` names the variant's representative surface material under `Assets/Resources/`, used by model-free faces such as intersection asphalt and far strips.

A path's paint material `PATH_PAINT` is replaced with a copy painted in the path's color (API `color`: the path's own color, otherwise the variant's default color), sharing one instance per color. Which parts are painted is decided by the parts of the model that use this material name. Variants that use no color keep the model's own color.

New generators should follow one of these two approaches rather than adding independent resource loading or `new Material` calls.

## Replacement after generation

Hover, planned or construction state colors, district colors, and heatmaps are applied afterward by visual-mode components replacing renderer materials and restoring them when the mode ends ([Unity visual modes](../programs/unity-visual-modes.md)). Materials resolved here define the ordinary appearance. Building scatter parts are not part of the building's renderers, so they are not replaced: they keep their ordinary appearance and follow only the building's visibility ([building meshes](F_building-meshes.md)).

