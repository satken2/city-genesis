# Building meshes

Unity assembles FBX parts using core-provided footprints, floor counts, and appearance values. Master `agent_master.model_type` selects assembly independently of [footprint shape](C_building-geometry.md). This page defines shared conventions; [building mesh methods](F1_building-mesh-methods.md) covers individual methods and construction or collapse rendering.

## Inputs

Vertex ordering and frontage follow [building geometry](C_building-geometry.md). Buildings are grounded at terrain elevation at `center_point`, except water-parcel buildings at sea level. Floor heights follow the master's `height_per_floor` grid. Fences, ground panels, and other site decorations follow parcel geometry rather than the building footprint. `visual_variant` selects appearance; `visual_variant2` seeds variation such as rooftop or scattered-part placement. Identical values produce identical appearance.

Modular building bodies are horizontal. Concrete foundations fill downhill gaps on slopes, while site decoration follows terrain. Entrances and exterior stairs stay inside the footprint, setting back the main body as needed. The footprint therefore includes projecting parts.

## Part locations and names

FBX files live at `Assets/Models/Buildings/{prefix}/{number}/{part}.fbx`, with matching relative wrapper-prefab paths under `Assets/Resources/Models/Buildings/` ([material sharing](P_material-sharing.md)). Select the first available prefix: landmark `landmark-{model_prefix}`, variant-specific `a{variant}`, or building-type shared `b{bd_type}`. `Resources/Config/agent_model_config.yaml` lists landmark and variant folders. Cities using an asset pack search this order in the pack first ([asset packs](X_asset-packs.md)). Parts shared by all buildings live in `Buildings/common/`.

| Part name | Role |
| --- | --- |
| `{level}-{type}{n}` | Modular exterior: levels `base`, `mid`, `top`, and site `fence`; types `w` for walls, `c` for corners, `stair` for exterior stairs. Position n refers to the edge starting at vertex n−1 for walls and stairs, or vertex n−1 for corners. Position 1 is the front |
| `roofprop` / `entrance` / `interior-floor` | Rooftop structures, entrances, and floor slabs |
| `ceil` / `floor` / `generic-wall` | Material-only sources for roofs, ground panels, and gable walls ([material resolution](R_material-resolution.md)) |
| `tile` / `scatter{n}` | Ground tiles and vehicles or materials scattered outside the body |
| `base-prebuilt` / `mid-prebuilt` / `top-prebuilt` | Prebuilt tiers; mashup wings use names such as `mashup-{model_prefix}-base-prebuilt` |
| `port_*` / `yard_*` / `site_*` | Port, depot, and open-space parts |
| `scaffold-*` / `crane-*` / `demo-*` / `excavator` / `collapse` | Construction, demolition, and collapse sites |
| `far` / `mashup-{model_prefix}-far` | Far models for whole buildings or individual wings |
| `{part}_close` | Close-up model |
| `road_access_{type}_{identifier}` | Empty model objects marking road-access frontages |

## Appearance variants and fallback

`{number}` is a folder for one appearance model. A building picks one number from the model table of its prefix folder and `visual_variant` ([asset packs](X_asset-packs.md)). Parts come from the largest folder at or below that number that contains the part, and the building-type shared folder (`b{bd_type}`) is searched with the same number. Modular buildings choose one folder for the whole building based on `mid-w1`; other methods choose per part, except prebuilt buildings and depots, which always use `0`.

For missing walls and corners, search the same folder in this order: position 1 at the same level, the same position at `mid`, then `mid` position 1. Omit corners if none exists. `floor` and construction parts also search `common`. Buildings with nothing renderable use a white extruded-footprint box to show location.

## Draw LOD

Close prefers `{part}_close` in the selected folder and falls back to the ordinary part. Folder selection always depends on ordinary-part availability, regardless of LOD. Far never assembles parts: it stretches one far model to the building envelope, or each wing for mashups and open pits, falling back to boxes. Construction, demolition, and collapse use low Far boxes. See [LOD](E_lod.md) and [data loading](H_data-loading.md) for retention, reuse, and waiting for terrain.

Scatter parts (`scatter{n}`) are not built into the building mesh. Like [terrain props](M_terrain-props.md), their shared models are drawn with GPU instancing across buildings. Their draw LOD is selected per part with the terrain-prop rules regardless of the building, including the handling of `_close` and `_far`. Generation that creates no shown building object, such as plan previews, includes scatter parts in the single mesh.

## Road-access frontages

Facilities can provide entrances to which road rendering blends paving ([road meshes](G_road-meshes.md)). Frontages come from markers in prebuilt and port models or station-rotary layout plans. They exist only for active, paused, or scheduled-for-demolition buildings and do not depend on draw LOD.

