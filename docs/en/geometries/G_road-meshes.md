# Road meshes

Unity assembles path models for roads, tracks, power lines, ropeways, and similar structures from API data and master dimensions. This page uses roads to explain shared mechanisms. See [track meshes](N_track-meshes.md), [station building meshes](Q_station-building-meshes.md), and [path geometry](A_path-geometry.md) for specific structures and data meanings.

`PathManager` owns generation, updates, caching, and LOD. A generator selected by `PathMeshFactory`, such as `RoadPath`, creates geometry.

## Generation flow

1. **Select a generator.** Master `model_type` chooses `default` roads, `track` railways, `station_house` station buildings, or `none` for no rendering. Missing values use `default`. Structures such as power lines use dedicated generators.
2. **Create a centerline.** Sample saved cubic Béziers, filling missing controls by the core's rules. Surface spans follow terrain; elevated, underground, and non-following (`follow_terrain`) spans interpolate anchor and control heights. The core levels terrain across road width when construction starts, so rendering keeps cross-sections horizontal.
3. **Connect and trim.** Nodes continuing an identical cross-section straight ahead connect paths directly at the node. Other nodes trim centerlines at cut sections and fill the perimeter with intersection surfaces, connecting sidewalks and walls using matching models ([path intersections](J_path-intersections.md)). Logical public transit paths do not participate in intersection generation; only the underlying roads and tracks determine junction geometry. Ropeways, power lines, and pipelines without surfaces render to anchors without intersections.
4. **Build splines.** Create splines from trimmed points. Straight paths with waypoint corners omit waypoints inside the intersection and split the remaining spans into separate splines. Preserve the remaining corners and each span's height settings, filling corners like intersections.
5. **Place parts.** Dreamteck Splines SplineMesh bends repeated model parts along the spline.
6. **Create selection geometry.** A separate road-width ribbon along the spline supplies the collider.

Rendering does not modify saved data. Intersection trimming, ramp height blending, and pillar omission are visual adjustments that do not alter saved geometry, connectivity, or traffic. Changing street-parked cars and ropeway cabins are rendered separately by [vehicle rendering](O_vehicle-rendering.md). Placement previews use the same generators.

## Cross-sections and height

Master `width` is total road width. Sidewalks lie inside its edges, walls and guardrails outside, and medians at the center. Master fields such as `sidewalk_width`, `wall_width`, and `center_width` size the parts. Height has two layers: the spline floats slightly above terrain, and parts sit at fixed offsets from it. `RoadPartHeights` centralizes these values for intersections, vehicles, and pedestrians too.

Marked streets and motorways (`PATH_STREET`, `PATH_MOTORWAY`) render lane and stop lines in the road shader rather than separate part planes. Markings and vehicle lanes share one lane-position definition.

## Part names

Models use `Models/Paths/p{path_variant}/{part}`, falling back to `Models/Paths/p{path_type}/{part}`.

| Part | Role |
| --- | --- |
| `body` | Road surface |
| `line` | Markings for paths not using shader markings |
| `sidewalk` | Sidewalk |
| `wall` | Noise barriers, guardrails, parapets |
| `center`, `center_start`, `center_end` | Median and rounded ends |
| `wall_underground`, `ceiling_underground` | Tunnel walls and ceiling |
| `pillar`, `pillar_cap` | Elevated support and cap |
| `pillar_leg`, `pillar_crossbeam` | Portal-frame support legs and beam |
| `portal` | Tunnel entrance |
| `tree`, `center_tree`, `streetlight`, `utility_pole`, `utility_box`, `fence`, `bench`, `bin`, `planter`, `advert` | Roadside equipment |
| `far` | Far model |
| `{part}_close` | Close-up model |

Dedicated structures such as suspension bridges, on-street LRT, and ropeway stations keep generator-specific parts in the same folders. Materials are shared by name ([material resolution](R_material-resolution.md)). Master `body_material` names the material for surfaces without models, such as intersections and far strips.

## Fallback and LOD

Search variant then type, omitting absent parts. Missing `body` alone falls back to a white strip of `width` so play can continue.

Draw LOD follows camera distance per path or per section for long roads ([LOD](E_lod.md)). Close prefers `_close` spline parts. Far repeats the variant's `far` model along points, falling back to a strip of `width`.

## Elevated and underground paths

Variants with `height_editable` and `pillar_spacing` place caps and ground-reaching pillars at anchors and regular intervals. Supports avoid other paths' carriageways and tracks and road surfaces above their feet, but may stand on sidewalks and medians defined by `sidewalk_width` and `center_width`. Conflicting pillars become portal frames or are omitted. Caps and crossbeams may project above traffic clearance. `single_span` variants bridge each anchor interval with one surface span.

`base_height` is a visual offset: render at relative height minus `base_height`. It serves models such as power lines whose geometry already includes height above ground.

Underground paths compare depth at each point with master clearance `height`. Shallow sections are cuttings excavated by the core at construction and render normally. Deep tunnel sections are hidden, with `portal` at boundaries. [Underground view](../programs/unity-visual-modes.md) also renders tunnel sections.

## Roadside equipment

Saved per-path values select equipment; `PATH_EQUIPMENT_FIELDS` lists fields. Values are `PATH_EQUIPMENT_NONE` or `PATH_EQUIPMENT_STANDARD`, except `utility_mode`, which uses `PATH_UTILITY_NONE`, `PATH_UTILITY_OVERHEAD`, or `PATH_UTILITY_UNDERGROUND`. Master `*_allowed` fields permit equipment and `default_*` fields set new-path defaults. Equipment is visual only and does not affect the economy.

## Construction display

`STATUS_CONSTRUCTING` paths show work progressing from start to end, with elevated pillars before girders. Active paths switch to normal meshes. `construction_progress` is the percentage of required construction volume procured. It is supplied on path-list entries as a rendering field outside the generated schema.

