# Q. Station building meshes

`StationHousePath` renders variants with path master `type` equal to `PATH_STATION_HOUSE` and `model_type` equal to `station_house`. They consist of passages along waypoint polylines and entrance buildings at selected ends. See [stations](../economics/19_stations.md) for station meaning, platform membership, entrance ends, and passage modes.

## Passages

Per-path `passage_mode` selects `elevated`, `ground`, or `underground`. New construction starts with the path master's `default_passage_mode` and saves the mode selected during placement on the path. Underground passages have neither rendering nor selection surfaces, so their stations are selected through entrance buildings.

- Parts use `Models/Paths/p{path_variant}/passage_elevated` or `passage_ground`, falling back to matching parts in shared `Models/Paths/p{path_type}/`. Some variants use variant-specific `body` for supported modes.
- Models have ground-level origins and actual dimensions. Deck height, supports, and parapets belong to models; code changes neither width nor height. Passage elevation comes from the model, not path Y, and is not adjusted to building connections. Master `width` applies only to occupancy and selection width.
- Each start, waypoint, and end anchor is placed at the ground directly beneath it. Straight spans connect anchors using actual-length model tiles, without following intermediate terrain.

## Entrance buildings

`house_ends` selects `both`, `start`, or `end`. Buildings occupy exclusive rectangles of master `house_width × house_depth`, falling back to `STATION_HOUSE_DEFAULT_WIDTH` and `STATION_HOUSE_DEFAULT_DEPTH`.

- Parts use `Models/Stations/{path_variant}/{part}`, then shared `Models/Stations/{part}`. `body` supplies the building; `floor` supplies ground material.
- `body` retains actual size. Its origin is the midpoint of the rectangle's inner edge nearest the line, coinciding with the path endpoint unless displaced to avoid tracks. Local +Z faces outward. Depth and lateral `house_offset` are built into the model; code neither recenters it nor adds the offset again. Buildings ground beneath their origins, requiring foundations extending underground and geometry fitting master occupancy.
- Missing `body` uses a placeholder box matching the exclusive rectangle.
- An available `floor` draws a terrain-following surface over that rectangle. Without any `floor`, no surface is created.
- Clicking a building opens path details because the station is the station building path itself.

## Exclusive-area placement

Positions are not saved; shared deterministic rules derive them from path shape and surrounding rail or monorail tracks. The core uses the same rules for occupancy, so changing one implementation alone misaligns land use and appearance. Adding or removing tracks can move areas.

Master `house_placement` selects `outside`, the default placing buildings outward beyond track and platform occupancy, or `inside`, aligning to the track boundary beneath elevated structures. `house_offset` shifts rectangles laterally; positive is right when looking outward, so opposite ends shift in opposite world directions.

## Other paths

Station building paths create no nodes and freely cross roads, tracks, and other paths, outside [intersection rules](J_path-intersections.md). Only exclusive areas occupy land. Placement snaps only to the grid, not existing nodes or paths ([Unity input](../programs/unity-input.md)). Previews use the same generator and exclusive-area calculations as confirmed rendering.

