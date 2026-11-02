# Path intersections

This page explains crossings and connections, and how to handle Unity placement previews and core validation results. See [path geometry](A_path-geometry.md) for shape data and [road meshes](G_road-meshes.md) for rendering assembly.

## Crossings and connections

The road network is a graph of nodes and paths. Lines crossing in plan are distinct from traversable connections. Unity uses the retrieved node–path relationships rather than reconstructing connections from visual overlap. Planned and under-construction paths do not necessarily have the same connection information as the completed road network.

A level crossing connects at the same height, as at road intersections and railway crossings. A grade-separated crossing passes independently at different heights. Overlapping passage space is treated as placement interference. Infrastructure, station buildings, and other path types have different connection meanings, so road intersection rendering does not apply to every path.

Infrastructure path endpoints may have Y values different from their connected nodes. Unity renders endpoints at their saved heights without adding parts to bridge the vertical difference.

## Shared geometry in Unity previews

Placement snapping and interference displays follow the same coordinate and height conventions as saved shapes. In particular, do not compare road-surface heights using only interpolated relative Y for sections that do not follow terrain. Anchors use different reference surfaces, so absolute elevation at the crossing is required. Passage-space height comes from master `height`; use shared geometry calculations for checks.

Rail connections require previews to account for height and tangent alignment as well as position. Paths crossing water require ground and water surfaces to be distinguished. Consult Unity's editing sources, shared geometry calculations, and shared constants for these interaction aids and their tolerances rather than defining separate rules per screen.

## Handling validation results

Unity checks are estimates during interaction; final eligibility follows the core response. `POST /api/v1/paths/validate` returns validation results without changing the city. Reflect API results even when the local preview shows no problem, since violations involving quays, land under earthworks, and other constraints may still be returned.

Display connection-shape violations in `junction_violations` separately from occupancy interference in `colliding_path_ids`. Connection-violation positions are `[x, absolute surface elevation, z]`, unlike the relative heights in saved paths. Interpret IDs for water, land, and other violations according to their response target type, rather than treating them as path IDs. Consult the API reference for the field inventory.

For grids and similar layouts, use the path list returned by `POST /api/v1/paths/layout` as input to validation and confirmation. The front end does not recreate the layout using separate rules. Follow [IPC](../programs/ipc.md) for preview, confirmation, and result-retrieval responsibilities.
