# Track meshes

`TrackPath` renders variants with master `model_type` equal to `track`, including `PATH_TRACK`, `PATH_MONORAIL`, and station paths with platforms. Centerlines, LOD, colliders, underground splitting, and part lookup follow [road meshes](G_road-meshes.md). Track gauge and overhead-wire cross-sections must not vary with variant width: beds stretch to total width, while tracks, wires, and platforms retain actual size.

Appearance also depends on connected and nearby parallel paths. `PathManager` supplies this context; isolated mesh scenes without it render standalone tracks.

## Parts

Search `Models/Paths/p{path_variant}/{part}`, then `Models/Paths/p{path_type}/{part}`. Elevated sections prefer `body_elevated` and `track_elevated` when available. Pillars, tunnel walls and ceilings, and portals share road parts.

| Part | Role |
| --- | --- |
| `body` | Track bed; elevated beds include girders and slabs |
| `track` | One track's rails and sleepers, or monorail beam |
| `rail` | Rails for turnouts and flat crossings |
| `wire`, `pole`, `beam` | Overhead wires, poles, and portal beams |
| `wall` | Elevated parapets and noise barriers, or bridge trusses |
| `arcade` | Continuous support such as viaduct arches |
| `platform`, `platform_wall`, `platform_foundation` | Platform edge, outer boundary, and lower exterior wall |
| `platform_panel` | Material-only source for the platform top |
| `buffer_stop`, `platform_end` | Buffers and end fences |

Absent parts are omitted. Only when both bed and track are absent does rendering use a white strip so play can continue. Monorails have no bed.

## Surface, elevated, underground, and electrification

Anchor relative height, rather than variant, determines surface, elevated, and underground sections ([path geometry](A_path-geometry.md)). One path can contain all three. Above-ground sections use elevated parts and parapets; fully buried sections omit wire poles. Master `wire_spacing` determines electrification; empty values omit wires and poles.

## Track positions

Track positions derive from master `width`, `lanes`, and `center_width`, like road lanes, without dedicated position parameters. `lanes` equal to 2 means one path contains two tracks and always allows both directions. Path and travel direction select one track; two-track paths use the traffic-hand side in the direction of travel. Main tracks, connecting rails, buffers, and [vehicles](O_vehicle-rendering.md) share `TrackPath` position rules.

## Nodes

Rail nodes form turnouts or flat crossings. Perimeters use the road intersection mechanism but show track-bed surfaces. Rails are drawn only for direction pairs trains can actually traverse, using routing's turning-angle constraints and each path's `travel_direction`. Railway track ends unused by connecting rails receive buffer stops.

## Station paths and platforms

Station paths are track variants with `platform_width`. Their ordinary `width` joins surrounding tracks directly; platforms project beyond the bed by `platform_width`. Single-track stations use `platform_side` (`PLATFORM_SIDE_*`), resolving inside and outside to left and right from city `traffic_hand` and `travel_direction`. This matches core station occupancy rules; changing only one side misaligns site and appearance.

Double-track stations use side platforms outside the tracks or one island between them. Two-track stations use side platforms unless positive `center_width` selects an island. Two parallel single-track stations with inward platforms merge visually into one island; outward platforms form side platforms.

## Nearby track corridors

Nearby parallel tracks of the same kind render as one corridor, joining beds and omitting internal parapets or wire poles. This is visual only and does not alter core data or routing. A neighbor can change without advancing the current path's version, so railway data changes redraw nearby tracks too. Unfinished paths (`STATUS_TEMPORARY`, `STATUS_SCHEDULED_CONSTRUCTION`, `STATUS_PENDING`, `STATUS_CONSTRUCTING`) and placement previews can act as temporary neighbors. Removing them restores the original appearance.

