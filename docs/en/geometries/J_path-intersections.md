# Path intersections

This page defines how the core creates nodes and connects crossing or touching paths, and which intersections prevent placement. See [path geometry](A_path-geometry.md) for shape data.

## Crossings and connections

The network is a graph of nodes and paths. Planned and under-construction paths have no nodes. On completion (`STATUS_ACTIVE`), the core joins endpoints to nearby nodes, creates nodes at crossings with existing paths, and splits both paths. Splits preserve curve geometry, height settings, terrain following, and design profiles. Railways preserve their pre-split surface elevations as design profiles.

Two paths crossing in plan form a **level crossing** when surface heights match, creating a node; a **grade-separated crossing** when their occupied vertical ranges are disjoint; or a **collision** when those ranges overlap at different heights. Occupied height extends from the surface through `path_master.height`. Logical public transport and station building paths do not create crossing nodes. Infrastructure paths connect only within the same network, where `infra_products` overlap.

## Height comparisons

When both spans follow terrain, compare relative Y; surface spans always form level crossings. If either span does not follow terrain, has nonzero Y, or has a design profile, compare absolute elevations. Interpolated relative Y does not describe physical height at a crossing because it is based on each path's anchor ground.

Unity previews follow the same rule. Unity uses full master occupancy height for collision checks; core confirmation uses a slightly lower estimate to avoid rejecting front-end-approved geometry because of numerical error.

## Endpoint welding and vertical infrastructure connections

Joining endpoints to nodes changes horizontal position only, preserving endpoint Y. Roads connect when their centerline-to-shoulder areas touch. An endpoint reaching another road's interior splits it into a T-junction, with the same result regardless of completion order. Road–rail, rail–rail, and facility-terminal connections require placement snapping within `RAIL_JUNCTION_POSITION_TOLERANCE`. Roads and rails connect only to nodes at matching surface heights, keeping elevated and ground-level nodes separate.

Infrastructure paths connect to same-network nodes regardless of height. Endpoint Y may differ from node Y. Unity renders saved endpoint heights without adding vertical connecting parts.

## Railway endpoint rules

Rail and monorail endpoints support only simple endpoint-to-endpoint extension, even if the peer is unfinished, or siding merges and branches into a completed main line that is not being rebuilt. Position, height, and three-dimensional tangents must align. At branches, main and siding sections around the connection must lie in one plane, which may be inclined. Shared `RAIL_JUNCTION_*` constants define tolerances and section lengths. Completion splits the main line while preserving its absolute surface profile, so branching does not alter its longitudinal alignment.

`/paths/validate` reports these violations in `junction_violations`, separately from occupancy `colliding_path_ids`. Each contains a reason code and connection point `[x, absolute surface elevation, z]`.

## Water and quays

Sections above rivers, sea, or lakes must be bridges at least `WATER_BRIDGE_MIN_CLEARANCE` above water or fully underground deeper than clearance height. Terrain-following spans run along the bed, so surface roads and tracks cannot cross water. Infrastructure paths are excluded; `over_water_allowed` variants are also excluded from sea/lake checks. Sea/lake water means points outside land polygons whose terrain is below water level. Polygons alone would misclassify dry shores because they can differ from the waterline by several meters.

Reclaimed quay-top bands ([terrain](D_terrain.md)) cannot be crossed by surface spans, and anchors cannot be placed inside them even for elevated or underground paths. Earthworks could create grooves beyond the top, and relative-height conversion differs because Unity uses the higher of terrain and quay top while the core uses terrain. Only the core checks bands, repeating checks immediately before construction because completed reclamation may create new ones. Paths also cannot be planned across ongoing earthworks areas whose terrain will change on completion.

## Validation before placement

Core validation is authoritative; Unity estimates during interaction. `POST /api/v1/paths/validate` does not change the city. It constructs the same confirmed shape and checks it using creation rules, returning violations by category ([IPC](../programs/ipc.md)). In `colliding_sea_lake_ids`, sea is `"sea"` and lakes use water-surface `id`. `colliding_quay_band_ids` uses `reclaimed_land_id`; `colliding_grading_site_ids` uses earthworks `parcel_id`. Creation additionally checks conditions such as anchors on land and no more than one level crossing with the same peer.

For layouts such as grids, the UI expands points and settings through `POST /api/v1/paths/layout`, then uses ordinary validation and creation. Expansion excludes sections parallel to existing roads and sections invalid in isolation. Crossing existing roads connect at intersections when construction completes.

