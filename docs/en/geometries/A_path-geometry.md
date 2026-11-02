# A. Path geometry

This page defines path shape data and the conventions that give Unity and the core identical geometry. See [paths and nodes](../economics/03_paths-and-nodes.md) for categories and roles, and [path intersections](J_path-intersections.md) for crossings and connections.

## Coordinates and heights

Path points are `[x, y, z]` in meters. Y is **height relative to the reference surface**, not absolute elevation. The reference surface is the water surface over sea and lake water, and the ground elsewhere, including rivers. An anchor's Y (`start_point`, `end_point`, or waypoint `position`) is measured from the reference surface beneath that anchor. A control point's Y is measured from the reference surface beneath its owning anchor. Using the surface beneath the control point would introduce intermediate terrain relief into the curve and disagree with the saved shape. Over water, pier lengths and tunnel depths still use the ground at the bed as their reference.

One path can contain surface, elevated, and underground sections. Height editing uses settings such as `path_master.height_editable` and `min_height`. Distinguish anchor Y height settings from fill and excavation for surface roads.

## Curves and control points

Each pair of anchors is connected by a cubic Bézier curve. The four points are anchor i, its outgoing control (`start_control` or `control_out`), anchor i+1's incoming control (`end_control` or `control_in`), and anchor i+1. Paths with `curved` false connect anchors by straight lines without using controls.

Unity prefers saved controls. Previews and rendering use shared geometry calculations for missing controls and alignment with connecting paths. These rules must also remain consistent with the core; do not change only one side. Consult Unity's geometry and editing sources for the calculations.

## Terrain following (`follow_terrain`)

A span follows the reference surface only when both anchor Y values are zero and terrain following is enabled. Other spans interpolate anchors and controls converted to absolute elevations using their owning anchors' reference surfaces, ignoring intermediate terrain. Straight paths interpolate linearly between absolute endpoint elevations.

The path's `follow_terrain` controls this behavior; -1 means use `path_master.follow_terrain`. Non-following paths can form bridges over valleys or cuttings and tunnels through hills even with Y zero.

## Earthworks and design profiles

Surface-path fill and excavation are specified through `embankment_heights`: one height in meters per anchor, positive for fill and negative for cuts. Unity uses the retrieved terrain after earthworks without applying fill, excavation, or smoothing again during rendering.

A design profile (`station_grade_profile`) is a road-surface reference independent of terrain, with points `[x, absolute elevation, z]`. When rendering a path with a profile, it takes precedence over anchor relative heights and terrain; use the elevation at the horizontal projection onto the reference line. Use the retrieved shape without assuming uniformly spaced points.

## Length and occupancy

`length` is the straight-line start–end distance, or the anchor polyline length when `curved` is false, rather than curve length. `polygon` represents the path's horizontal occupancy for overlaps and interference. Road-surface rendering uses the centerline and master dimensions.

## Valid shapes

Unity editing uses master constraints on curves, gradients, heights, and similar properties to aid interaction. Final eligibility follows core validation results. See [path intersections](J_path-intersections.md) for validation and preview responsibilities and [terrain](D_terrain.md) for city bounds.
