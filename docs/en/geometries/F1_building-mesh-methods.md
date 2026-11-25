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

### Parking lots around buildings

A modular building whose appearance folder has the stall lines' material `site_marking` draws its whole parcel as a parking lot, in place of the fence, the scatter parts, and the ground panel. The placement plan is built and its parts are held as for [gas stations](#gas-stations), treating the parcel edge that the footprint's front edge faces as the road-facing edge.

The whole site is paved. A planted strip with a curb runs around its edge and opens for driveways on the road-facing edge. Parking stalls are drawn in front of the walk along the building's front, in facing rows on open ground, and along the site's edges, each with a drive aisle left clear behind it. On a shallow site, a driveway and the road beyond it serve as the aisle. The items in front of the shop `site_clutter1`… stand on the front walk near the entrance, and the planted strip holds the sign `site_sign`, lights `site_light`, shrubs `site_shrub1`…, and trees `site_tree1`….

Each parked car `site_car1`… is drawn only while the building's operation rate exceeds the rate fixed for its stall. The more a building operates, the more cars stand in its lot, and a paused or vacant building has none.

## Prebuilt and mashup buildings

Prebuilt buildings (`MODEL_PREBUILT`) suit towers, statues, Ferris wheels, and other shapes unsuitable for wall assembly. They stack `base-prebuilt`, `mid-prebuilt`, and `top-prebuilt`, aligned to the front edge without stretching to fit the parcel. `base` and `top` each represent one floor; `mid` spans the remaining floors. Only existing tiers are stacked, so `top-prebuilt` alone can represent an entire building. These use folder `0` without appearance variants. Landmarks stack similarly in their own folders.

Mashup buildings (`MODEL_MASHUP`) represent facilities as multiple wings, such as school buildings and gymnasiums. The core supplies editable `sub_buildings` layouts. Width and depth use sub-building master `width` and `height`, in cells of side `MASHUP_CELL_SIZE`. `position` is the wing center in XZ relative to the parent's `center_point`. `rotation` is counterclockwise degrees from +X; width follows that direction. Wing floor counts are not saved: parent floors are clamped to each sub-building master's range. Wings whose sub-building master `loose` is enabled, such as heaps and parked machines, are drawn shifted and turned from the planned position and rotation by amounts fixed for each building and wing, so they do not line up on the grid. The distant view places them at the same position and rotation.

Wings stack parts such as `mashup-{model_prefix}-base-prebuilt`, but keep each tier at its actual size and place it on the previous tier's top. Numbered folders are selected per part. Wing models are authored smaller than the cells they occupy, and the remainder is the gap between wings. Each wing grounds at its own terrain position and retains its layout and actual-size footprint in Far. Prebuilt selection extrudes the footprint to the rendered tiers' top; mashup selection extrudes the parcel to the highest wing, covering actual-size models and spaces between wings. Prebuilt road frontages come from markers in the ground-level tier.

## Ground assets and open spaces

Ground assets (`MODEL_MODULAR_GROUND`) serve fields, gardens, and paved squares without a building body. Fences and ground panels cover the parcel, actual-size tiles fill the positions that fit inside it on a grid along the footprint's front edge, and the parcel also supplies selection geometry. Tiles take the same heading as the distant model. The building footprint is used when the parcel is unavailable.

Parking (`MODEL_PARKING`), station rotaries (`MODEL_ROTARY`), and parks (`MODEL_PARK`) derive layout plans from parcels and combine terrain-following surfaces with actual-size objects. Surfaces use materials such as `site_asphalt` and `site_grass`; objects use models such as `site_bench` and `site_tree`. Park trees, shrubs, plaza facilities, play equipment, and flower beds are numbered models per kind, such as `site_tree1` and `site_feature1`, used consecutively from 1 for as many as exist. Other open spaces, including paved squares or multistory parking, still follow their master's ground or modular `model_type`.

Layout depends on the parcel shape relative to its frontage edge and `visual_variant2`, independently of building ID, load order, and draw LOD. Parks connect entrances and plazas with curved paths, and larger sites get more entrances, plazas, and paths. When the model folder has court surface materials (`site_diamond`, `site_track`, `site_pitch`, `site_court`), the courts that fit the site are placed first, largest first, and the rest of the site becomes the park. Objects are placed to fit their actual model sizes, and `visual_variant3` changes the tree density. Parking bays, paths, and planting are visual only and do not simulate parking or passage; parks remain traversable across the entire parcel ([districts and parcels](../economics/04_districts-and-parcels.md)). Rotary frontages are intersections of planned paving with frontage edges.

## Utility plants

Substations (`MODEL_SUBSTATION`), water pumping stations (`MODEL_WATER_STATION`), sewage treatment plants (`MODEL_SEWAGE_PLANT`), and waste treatment plants (`MODEL_WASTE_PLANT`) build a placement plan from the parcel shape, as open spaces do. The core does not send the placement of their equipment, and the player cannot edit it.

Every plant opens a gate on the road-facing edge, runs a service road in from the gate along one side of the site, and lines up rows of equipment from the front to the rear. A row repeats its part at actual size as many times as fits, so the amount of equipment follows the parcel size, and an irregular parcel only loses the units that do not fit. Plants differ only in what their rows hold, listed from the front. A shallow parcel keeps the front rows, and a deep one repeats the same sequence behind a cross road. `visual_variant2` decides which side the gate is on.

Equipment uses `site_{name}` models such as `site_office` and `site_transformer`, which can have a close-up `_close` and a distant `_far` with the same origin and footprint. Equipment is combined into the building mesh, while small parts such as pipes and yard lights are drawn from shared models like the trees in a park. The perimeter fence is the site decoration `fence-w`, opened at the gate, where `gate` is placed. Each unit rests at the terrain elevation of its own position, and the distant view places each unit's `_far` in the same layout. Parts missing from the model folder are not placed.

## Gas stations

Gas stations (`MODEL_GAS_STATION`) also build a placement plan from the parcel's shape. The backend does not send the placement, and players cannot edit it. Because the building footprint is smaller than the parcel, the plan uses the whole parcel as its site instead of the footprint, and treats the parcel edge that the footprint's front edge faces as the road-facing edge. When the parcel cannot be resolved, the footprint is used.

The whole site is a concrete forecourt. A planted strip with a curb runs around its edge and opens for driveways on the road-facing edge. The shop `site_store` stands in the rear corner, and the fueling area repeats `site_canopy`, one roof bay with an island and two pumps, edge to edge in both directions between the shop and the road (or beside the shop when fewer fit there), forming one roof. The number of bays is proportional to the site's area, and the roof's edge band `site_fascia` is attached only to edges with no neighboring bay. As the site gets smaller, the plan gives up the parking stalls in front of the shop, then the shop itself, places the office `site_office` instead of the shop, and finally keeps only the fueling area.

Around them stand the price sign `site_sign`, the underground tanks' fill covers `site_fill` and vent pipes `site_vent`, the air station `site_air`, and the dumpster enclosure `site_dumpster`, and parking stalls are drawn in front of the shop, on open ground, and along the site's edges. The items in front of the shop `site_clutter1`…, parked cars `site_car1`…, and the planted strip's shrubs `site_shrub1`… and trees `site_tree1`… are numbered parts, used for as many as are numbered consecutively from 1. Parts are held and drawn as for utility plants: the fueling bays, shop, office, and sign are units with a distant `_far` model, and the rest are small objects drawn with shared models. There is no perimeter fence or gate.

## Huts

Huts (`MODEL_HUTS`) line up many single-story houses on one parcel. The backend does not send the placement, and players cannot edit it. As with gas stations, the whole parcel is the site instead of the footprint.

Huts stand in rows along the road-facing edge. The number of rows and the number of huts in each row follow from the parcel's depth and frontage so that the huts stretch as little as possible. The parcel's remainder is taken up by the gaps between huts and by stretching each hut, earthen pad included, along its width and depth. Along a slanted edge the huts shrink, and where even a shrunk hut does not fit, the ground stays empty. Every hut faces the road, and a lane `lane` runs between neighboring huts.

The huts use the numbered parts `site_hut1`…, as many as are numbered consecutively from 1, mixed within the parcel. Parts are held as for utility plants: huts are combined into the building mesh, and the distant view places each hut's `_far` with the same placement and stretch. No ground panel is laid; the site shows the terrain.

## Facilities connected to terrain or tracks

### Ports

Ports (`MODEL_PORT`) combine a docking waterfront and a landside cargo yard. `port_crane` equipment stands at the waterfront and `port_building` at parcel edge zero, the road side. Space between contains stacked `port_container{n}` or ground-level `port_cargo{n}`. Variant folders `a{variant}` define cargo types; parcel and waterfront shapes, ordinary-model dimensions, and `visual_variant2` determine placement. `port_building` markers supply road frontages.

On natural shores, the quay line returned as building `berth_line` clips the seaward side, and a quay slab extrudes to the seabed. Unity never derives this line itself; construction previews also use the line returned in the response. See [land geometry](B_land-geometry.md) for parcel shapes and quay-line meanings. Its top uses `floor` material to blend with landside ground. Ports on reclaimed land use [terrain-rendered quays](D_terrain.md) without an extra slab. Reclamation changes do not advance building versions, so ports must also redraw when reclamation content changes. Waterfronts supply [ship docking positions](V_ambient-vehicles.md).

### Depots

Rail depots (`MODEL_RAIL_YARD`) converge internal tracks onto a terminal node. `yard_*` parts form sidings, gathering tracks, turnout curves, overhead wires and poles, buffers, and maintenance sheds. Lead tracks match the connected terminal track count. Equipment follows terrain; ground panels use only that variant's own `floor`. Selection extrudes the footprint to overhead-pole height so wires and sheds are selectable.

The terminal is `depot_node_id`, falling back to `infra_node_id`. Electrified depots share it between transport and power networks ([supply and transport](../economics/17_supply-and-transport.md)). Terminal positions or connected tracks can change without advancing the building version; notify and redraw the building for those changes.

### Open pits and quarries

Open pits (`MODEL_OPEN_PIT`) and quarries (`MODEL_QUARRY`) dig the terrain itself. The excavated shape is drawn by reloading the terrain cells whose version changes as mining progresses ([LOD](E_lod.md), [supply and transport](../economics/17_supply-and-transport.md)). Building rendering covers the equipment in the unexcavated frontage yard, the heavy machines, rocks, and materials scattered over the site, and the heavy machines working where the digging is. The frontage yard is the strip from the front edge of the footprint, `OPEN_PIT_YARD_DEPTH` deep for an open pit and `QUARRY_YARD_DEPTH` deep for a quarry.

| Method | How it digs | Excavation area |
| --- | --- | --- |
| Open pit | Digs downward. On flat land it becomes a stepped pit | A further `OPEN_PIT_RIM_MARGIN` behind the frontage yard, and the same distance from the parcel boundary |
| Quarry | Cuts the hill at the rear down in steps to the level of the frontage yard. The floor continues level from the frontage yard, and flat land is not dug | Directly behind the frontage yard, `QUARRY_RIM_MARGIN` from the parcel boundary |

Equipment in the frontage yard is laid out from the parcel shape and the actual sizes of the parts, by the same rule as utility plants. The core does not send its placement. An open pit follows the excavated rock from the pit to the gate: a haul road along the rim of the pit, the crushing plant `site_crusher` with its intake facing the pit, the product stockpiles `site_stockpile1`… joined to it by the elevated conveyor `site_conveyor`, a service road for shipping the product, and the office and workshop on the street side. A wide frontage repeats the set of a crushing plant and its stockpiles. A quarry's frontage yard is a single row: the office beside the gate and the crushing and screening plant `site_plant`, with its intake facing the working face, placed along the frontage, with product stockpiles and a settling pond between them.

On the free ground between the equipment, parked heavy machines (`site_truck`, `site_loader`, and others), rocks `site_rock1`…, small spoil heaps `site_heap1`…, and materials `site_clutter1`… are scattered without aligning their headings or positions. Rocks and heaps are also placed along the edges of the site and in the excavation area. Numbered parts are used for as many as are numbered consecutively from 1. These are drawn from shared models like the trees in a park and are not shown in the distant view. The heavy machines working where the digging is (pairs of an excavator and a dump truck) are placed by finding the level being dug in the current terrain (the lowest ground of an open pit, the floor of a quarry), so they move as the digging progresses.

The building center lies in the excavation area, so center-height parcel extrusion misses the frontage yard, while a flat surface or white box would cover the hole. Selection combines a terrain-following parcel surface and equipment bounding boxes. Ground panels cover the entire parcel, including the frontage yard and the excavation area.

## Construction, demolition, and collapse

Construction and demolition use dirt covering the site, with scaffolds and equipment for the state. Ground assets, open spaces, ports, depots, open pits, and quarries use only dirt. Collapse uses a debris heap for every method.

| State | Rendering |
| --- | --- |
| Construction (`STATUS_CONSTRUCTING`) | Dirt ground panel, `scaffold-wall` and `scaffold-corner`, and `crane-*` tower cranes above a floor-count threshold |
| Demolition (`STATUS_DEMOLISHING`) | Dirt ground panel, `demo-scaffold-wall` and `demo-scaffold-corner`, `demo-ceil`, and an `excavator` on top |
| Collapse (`STATUS_COLLAPSED`) | A heap of rubble and sand matching the building's volume, settled at the angle of repose. Taller buildings leave higher, wider heaps that spread unevenly past the footprint onto other parcels and roads. Close-ups add broken columns with exposed rebar inside the footprint. `status_reason` equal to `REASON_FIRE` uses burned materials |

Scaffold tiers follow `construction_progress`, the percentage of required construction or demolition volume procured. Construction grows upward with progress; demolition retains floors corresponding to the undemolished portion. Both show at least one floor. Progress and completion advance building versions and trigger diff reloads. Missing construction parts fall back to `common`, then are omitted.

