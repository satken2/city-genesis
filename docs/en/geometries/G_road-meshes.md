# Road meshes

Unity assembles path models for roads, tracks, power lines, ropeways, and similar structures from API data and master dimensions. This page uses roads to explain shared mechanisms. See [track meshes](N_track-meshes.md), [station building meshes](Q_station-building-meshes.md), and [path geometry](A_path-geometry.md) for specific structures and data meanings.

`PathManager` owns generation, updates, caching, and LOD. A generator selected by `PathMeshFactory`, such as `RoadPath`, creates geometry.

## Generation flow

1. **Select a generator.** Master `model_type` chooses `default` roads, `track` railways, `station_house` station buildings, `pier` ferry piers, `outline` paths with no physical form such as waterways, drawn as dashed lines along both edges of their width, `power_line` power-line towers and wires, or `none` for no rendering. Missing values use `default`.
2. **Create a centerline.** Sample saved cubic Béziers using shared geometry calculations. Follow [path geometry](A_path-geometry.md) for control completion, terrain following, and design profiles. Use retrieved terrain elevations after earthworks and keep rendered road cross-sections horizontal.
3. **Connect and trim.** Nodes continuing an identical cross-section straight ahead connect paths directly at the node. Other nodes trim centerlines at cut sections and fill the perimeter with intersection surfaces, connecting sidewalks and walls using matching models ([path intersections](J_path-intersections.md)). Logical public transit paths do not participate in intersection generation; only the underlying roads and tracks determine junction geometry. Ropeways, power lines, and pipelines without surfaces render to anchors without intersections.
4. **Build splines.** Create splines from trimmed points. Straight paths with waypoint corners omit waypoints inside the intersection and split the remaining spans into separate splines. Preserve the remaining corners and each span's height settings, filling corners like intersections.
5. **Place parts.** Dreamteck Splines SplineMesh bends repeated model parts along the spline.
6. **Create selection geometry.** A separate road-width ribbon along the spline supplies the collider.

Rendering does not modify saved data. Intersection trimming, ramp height blending, and pillar omission are visual adjustments that do not alter saved geometry, connectivity, or traffic. Changing street-parked cars and ropeway cabins are rendered separately by [vehicle rendering](O_vehicle-rendering.md). Placement previews use the same generators.

## Cross-sections and height

Path `width`, copied from the variant when the core creates the path, is total road width. Sidewalks lie inside its edges, walls and guardrails outside, and medians at the center. Master fields such as `sidewalk_width`, `wall_width`, and `center_width` size the parts. Height has two layers: the spline floats slightly above terrain, and parts sit at fixed offsets from it. `RoadPartHeights` centralizes these values for intersections, vehicles, and pedestrians too. Models are authored with this placement position as their origin, and rendering uses vertex heights relative to that origin directly. Road surfaces and track beds place their top at the origin; structures rising above ground, such as water conduits, place their geometry above it.

Marked streets and motorways (`PATH_STREET`, `PATH_MOTORWAY`) render lane and stop lines in the road shader rather than separate part planes. Markings and vehicle lanes share one lane-position definition.

## Part names

Models use `Models/Paths/p{path_variant}/{part}`, falling back to `Models/Paths/p{path_type}/{part}`. When the player chose an appearance for the path (`appearance`), the appearance folder `Models/Paths/p{path_variant}/{appearance number}/` is searched first. An appearance folder holds only the parts that differ from the standard set, and parts it lacks come from the variant folder. Close-up `_close` parts are searched in the same folder as their part.

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
| `span` | Bridge structure stretched across each span between piers (trusses, arches, and the like) |
| `tree`, `center_tree`, `streetlight`, `utility_pole`, `utility_box`, `fence`, `bench`, `bin`, `planter`, `advert` | Roadside equipment |
| `far` | Far model |
| `{part}_close` | Close-up model |

Dedicated structures such as suspension bridges, on-street LRT, and ropeway stations keep generator-specific parts in the same folders. Structures drawn only when their part exists, such as suspension bridge towers, can differ per appearance by placing them in appearance folders. Materials are shared by name ([material resolution](R_material-resolution.md)). Master `body_material` names the material for surfaces without models, such as intersections and far strips.

## Fallback and LOD

Search appearance, then variant, then type, omitting absent parts. Missing `body` alone falls back to a white strip of `width` so play can continue.

Draw LOD follows camera distance per path or per section for long roads ([LOD](E_lod.md)). Close prefers `_close` spline parts. Far repeats the `far` model (searched in the same order as parts) along points, falling back to a strip of `width`. The strip stands in for a physical body, so `outline` paths, which have none, draw nothing at Far or in the citywide overview.

## Elevated and underground paths

Variants with `height_editable` and `pillar_spacing` place caps and ground-reaching pillars at anchors and regular intervals. Supports avoid other paths' carriageways and tracks and road surfaces above their feet, but may stand on sidewalks and medians defined by `sidewalk_width` and `center_width`. Conflicting pillars become portal frames or are omitted. Caps and crossbeams may project above traffic clearance. `single_span` variants bridge each anchor interval with one surface span.

`base_height` is a visual offset: render at relative height minus `base_height`. It serves models such as power lines whose geometry already includes height above ground.

The same generator renders underground-path cuttings and tunnels according to the retrieved terrain and road-surface geometry. Tunnels stay hidden under terrain and show from outside only through their portals. A headwall covering the portal's cut face (wall, arch ring, and coping) is generated from the terrain rather than taken from a part, and the terrain shader skips terrain inside the tunnel interior within the headwall's thickness. Tunnel interiors can be clicked only in the [underground view](../programs/unity-visual-modes.md), which makes terrain transparent.

## Roadside equipment

Saved per-path values select equipment; `PATH_EQUIPMENT_FIELDS` lists fields. Values are `PATH_EQUIPMENT_NONE` or `PATH_EQUIPMENT_STANDARD`, except `utility_mode`, which uses `PATH_UTILITY_NONE`, `PATH_UTILITY_OVERHEAD`, or `PATH_UTILITY_UNDERGROUND`. Master `*_allowed` fields permit equipment and `default_*` fields set new-path defaults. Equipment is visual only and does not affect the economy.

## Construction display

`STATUS_CONSTRUCTING` paths show work progressing from start to end, with elevated pillars before girders. Active paths switch to normal meshes. `construction_progress` is the percentage of required construction volume procured. It is supplied on path-list entries as a rendering field outside the generated schema.

