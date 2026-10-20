# A. Path geometry

This page defines path shape data and the conventions that give Unity and the core identical geometry. See [paths and nodes](../economics/03_paths-and-nodes.md) for categories and roles, and [path intersections](J_path-intersections.md) for crossings and connections.

## Coordinates and heights

Path points are `[x, y, z]` in meters. Y is **height relative to the ground**, not absolute elevation. An anchor's Y (`start_point`, `end_point`, or waypoint `position`) is measured from the ground beneath that anchor. A control point's Y is measured from the ground beneath its owning anchor, rather than beneath the control point itself. Using the latter would introduce intermediate terrain relief into the curve and disagree with the core.

Surface, elevated, and underground sections are determined by anchor Y, rather than variant, and can coexist in one path. Y is editable only for variants with `path_master.height_editable`; negative Y requires a negative `min_height`. Y specifies elevation or underground placement. Surface road height adjustments, fill, and excavation belong to terrain earthworks and are not written into Y. Excavating a cutting is an exception: anchor Y is adjusted to preserve absolute elevation.

## Curves and control points

Each pair of anchors is connected by a cubic Bézier curve. The four points are anchor i, its outgoing control (`start_control` or `control_out`), anchor i+1's incoming control (`end_control` or `control_in`), and anchor i+1. Paths with `curved` false connect anchors by straight lines without using controls.

### Automatic control points

The core fills missing controls using the following rules and saves them. Unity prefers stored controls and calculates only missing ones using the same rules. Changing only one implementation would make occupancy and parcel checks disagree with rendering.

- Without waypoints, controls lie at one-third and two-thirds of the start–end segment, forming a straight line.
- With waypoints, each anchor's tangent is the vector between its previous and next anchors, or the neighboring-anchor vector at endpoints. The outgoing control is one-sixth of this vector forward from the anchor; the incoming control is one-sixth backward, using Catmull–Rom tangents.

When an endpoint joins an existing path, generated endpoint controls align with the other path's continuation when the angular difference is within `PATH_TANGENT_ALIGN_MAX_ANGLE_DEG`. Unity placement previews follow the same rule.

## Terrain following (`follow_terrain`)

A span follows terrain only when both anchor Y values are zero and terrain following is enabled. Other spans interpolate anchors and controls converted to absolute elevations using their owning anchors' ground heights, ignoring intermediate terrain. Straight paths interpolate linearly between absolute endpoint elevations.

The path's `follow_terrain` controls this behavior; -1 means use `path_master.follow_terrain`. Non-following paths can form bridges over valleys or cuttings and tunnels through hills even with Y zero.

## Earthworks and design profiles

Surface-path fill and excavation use `embankment_heights`, rather than Y: one height in meters per anchor, positive for fill and negative for cuts, linearly interpolated between anchors. Construction changes terrain accordingly, smoothing the road longitudinally before applying it. Unity follows the finished terrain without smoothing it again during rendering.

A design profile (`station_grade_profile`) is a road-surface reference independent of terrain. Its points are `[x, absolute elevation, z]`, with absolute rather than relative Y. Paths laid together, such as station tracks, can carry this profile. Their surface height comes from horizontal projection onto it instead of Y and terrain. Splitting preserves the profile; terrain changes do not regenerate it.

## Length and occupancy

`length` is the straight-line start–end distance, or the anchor polyline length when `curved` is false, rather than curve length. The occupancy `polygon` expands the centerline by half the road width on each side. It is used for overlap and interference checks, not rendering.

## Valid shapes

Core validation is authoritative. Anchors must lie on land unless the variant allows water placement. Master data defines curve eligibility, minimum radius, maximum gradient, and maximum non-following span length per variant. Gradients use the surface after earthworks. See [path intersections](J_path-intersections.md) for water and quay crossings and [terrain](D_terrain.md) for placement outside the city.

