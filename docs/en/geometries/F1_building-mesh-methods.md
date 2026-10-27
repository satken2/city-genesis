# Building mesh methods

`model_type` selects assembly. Construction, demolition, and collapse states take precedence and render sites or rubble instead of completed buildings. See [building meshes](F_building-meshes.md) for common inputs, naming, appearance variants, and draw LOD.

## Modular buildings

Ordinary buildings place wall and corner parts around each floor's footprint. Site decoration, rooftop structures, entrances, exterior stairs, and foundations are shared; the four methods differ in roofs.

| model_type | Roof |
| --- | --- |
| `MODEL_MODULAR_FLAT` | Flat |
| `MODEL_MODULAR_HOUSE` | Hipped, pitched roof |
| `MODEL_MODULAR_GABLE` | Two slopes and gable walls |
| `MODEL_MODULAR_SHED` | A thin single slope and gable walls |

Roofs are generated without shape parts, taking surface material from `ceil` and windowless gable material from `generic-wall`. Roof rise scales with floor height, preventing taller buildings from receiving disproportionately large roofs. `interior-floor`, if present, supplies each floor and doubles as the rooftop for flat roofs. When `building_shape` is `SHAPE_FULL`, footprints filling their parcels ([building geometry](C_building-geometry.md)) render one top-floor layer of walls and corners with roof and rooftop structures.

## Prebuilt and mashup buildings

Prebuilt buildings (`MODEL_PREBUILT`) suit towers, statues, Ferris wheels, and other shapes unsuitable for wall assembly. They stack `base-prebuilt`, `mid-prebuilt`, and `top-prebuilt`, aligned to the front edge without stretching to fit the parcel. `base` and `top` each represent one floor; `mid` spans the remaining floors. Only existing tiers are stacked, so `top-prebuilt` alone can represent an entire building. These use folder `0` without appearance variants. Landmarks stack similarly in their own folders.

Mashup buildings (`MODEL_MASHUP`) represent facilities as multiple wings, such as school buildings and gymnasiums. The core supplies editable `sub_buildings` layouts. Width and depth use sub-building master `width` and `height`, in cells of side `MASHUP_CELL_SIZE`. `position` is the wing center in XZ relative to the parent's `center_point`. `rotation` is counterclockwise degrees from +X; width follows that direction. Wing floor counts are not saved: parent floors are clamped to each sub-building master's range.

Wings stack parts such as `mashup-{model_prefix}-base-prebuilt`, but keep each tier at its actual size and place it on the previous tier's top. Numbered folders are selected per part. Each wing grounds at its own terrain position and retains its layout in Far. Prebuilt selection extrudes the footprint to the rendered tiers' top; mashup selection extrudes the parcel to the highest wing, covering actual-size models and spaces between wings. Prebuilt road frontages come from markers in the ground-level tier.

## Ground assets and open spaces

Ground assets (`MODEL_MODULAR_GROUND`) serve fields, gardens, and paved squares without a building body. Fences, ground panels, and actual-size tiles cover the parcel, which also supplies selection geometry. The building footprint is used when the parcel is unavailable.

Parking (`MODEL_PARKING`), station rotaries (`MODEL_ROTARY`), and parks (`MODEL_PARK`) derive layout plans from parcels and combine terrain-following surfaces with actual-size objects. Surfaces use materials such as `site_asphalt` and `site_grass`; objects use models such as `site_bench` and `site_tree`. Other open spaces, including paved squares or multistory parking, still follow their master's ground or modular `model_type`.

Layout depends on a frontage-aligned grid and `visual_variant2`, independently of building ID, load order, and draw LOD. Parking bays, paths, and planting are visual only and do not simulate parking or passage; parks remain traversable across the entire parcel ([districts and parcels](../economics/04_districts-and-parcels.md)). Rotary frontages are intersections of planned paving with frontage edges.

## Facilities connected to terrain or tracks

### Ports

Ports (`MODEL_PORT`) combine a docking waterfront and a landside cargo yard. `port_crane` equipment stands at the waterfront and `port_building` at parcel edge zero, the road side. Space between contains stacked `port_container{n}` or ground-level `port_cargo{n}`. Variant folders `a{variant}` define cargo types; parcel and waterfront shapes, ordinary-model dimensions, and `visual_variant2` determine placement. `port_building` markers supply road frontages.

On natural shores, parcels arrive without coastline clipping ([land geometry](B_land-geometry.md)). The quay line decided by the core (the building's `berth_line`: the chord between waterfront endpoints shifted seaward) clips the seaward side, and a quay slab extrudes to the seabed. The core judges the port's water depth just outside this line, so Unity never derives the line itself; construction previews are clipped by the same line returned in the preview response. Its top uses `floor` material to blend with landside ground. Ports on reclaimed land use [terrain-rendered quays](D_terrain.md) without an extra slab. Reclamation changes do not advance building versions, so ports must also redraw when reclamation content changes. Waterfronts supply [ship docking positions](V_ambient-vehicles.md).

### Depots

Rail depots (`MODEL_RAIL_YARD`) converge internal tracks onto a terminal node. `yard_*` parts form sidings, gathering tracks, turnout curves, overhead wires and poles, buffers, and maintenance sheds. Lead tracks match the connected terminal track count. Equipment follows terrain; ground panels use only that variant's own `floor`. Selection extrudes the footprint to overhead-pole height so wires and sheds are selectable.

The terminal is `depot_node_id`, falling back to `infra_node_id`. Electrified depots share it between transport and power networks ([supply and transport](../economics/17_supply-and-transport.md)). Terminal positions or connected tracks can change without advancing the building version; notify and redraw the building for those changes.

### Open pits

An open pit (`MODEL_OPEN_PIT`) is terrain; building rendering covers only equipment in the unexcavated frontage yard. Reload changed terrain cells as mining progresses ([LOD](E_lod.md), [supply and transport](../economics/17_supply-and-transport.md)). Equipment arrives as `sub_buildings` and uses mashup assembly.

The building center lies in the pit, so center-height parcel extrusion misses the frontage yard, while a flat surface or white box would cover the hole. Selection combines a terrain-following parcel surface and equipment bounding boxes. Ground panels cover the entire parcel, including yard and pit.

## Construction, demolition, and collapse

Unfinished states use shared dirt or rubble covering the site, with appropriate scaffolds, equipment, and structures. Ground assets, open spaces, ports, depots, and open pits use only dirt or rubble.

| State | Rendering |
| --- | --- |
| Construction (`STATUS_CONSTRUCTING`) | Dirt ground panel, `scaffold-wall` and `scaffold-corner`, and `crane-*` tower cranes above a floor-count threshold |
| Demolition (`STATUS_DEMOLISHING`) | Dirt ground panel, `demo-scaffold-wall` and `demo-scaffold-corner`, `demo-ceil`, and an `excavator` on top |
| Collapse (`STATUS_COLLAPSED`) | Rough rubble and optional `collapse` structure. `status_reason` equal to `REASON_FIRE` uses burned rubble material |

Scaffold tiers follow `construction_progress`, the percentage of required construction or demolition volume procured. Construction grows upward with progress; demolition retains floors corresponding to the undemolished portion. Both show at least one floor. Progress and completion advance building versions and trigger diff reloads. Missing construction parts fall back to `common`, then are omitted.

