# D. Terrain

This page defines conventions shared by the core, Unity, and Tauri for elevation, coastlines, water, rivers, reclaimed land, and background terrain outside the city. See [LOD](E_lod.md) for rendering range and quality and [data loading](H_data-loading.md) for retrieval and replacement order.

## D.1. Purpose

Terrain stores elevation across the city as raster grids, supporting 3D ground and placement of roads and buildings. Current elevation combines the original natural terrain with cells modified by road earthworks and other projects. The names far, near, and dynamic in storage and APIs refer to grid resolutions, rather than separate ground surfaces selected by camera distance.

## D.2. Map dimensions and grids

Map dimensions and grids are fixed when a city or scenario is created. The front end reads terrain configuration APIs instead of hard-coding them. `map_width` and `map_height` are even multiples of `cell_size`. Coarser far, near, and dynamic grid spacings are integer multiples of finer ones.

## D.3. Elevation reference

Elevation is absolute meters relative to sea level zero. Water levels, quay tops, parcel reference elevations, and camera heights use the same reference. Only path and node Y coordinates are relative to the ground ([path geometry](A_path-geometry.md)).

### D.3.1. Array orientation and directions

East is `+X`, north is `+Z`, and up is `+Y`; the world origin stays at map center. In every near, far, dynamic, and background elevation array, row zero is the southern edge at minimum Z, column zero is the western edge at minimum X, and `[0, 0]` is the southwest corner. This matches Unity's north-up overview, compass, and clockwise camera yaw with north (`+Z`) at zero.

External elevation sources such as images and DEMs often run north to south and must be reordered before storage. The game does not flip rows. Downward-Y 2D displays, including Tauri maps, terrain editors, and thumbnails, transform `+Z` upward. Right-handed 3D displays such as three.js compensate for east–west mirroring.

## D.4. Cells

Cells are elevation storage units and Unity terrain-tile units, with no gameplay meaning. Roads, buildings, and parcels may cross them. Editing terrain increments the affected cell's version.

Core cell indices are nonnegative and originate at the map's southwest corner. Unity indices originate at map center and may be negative. Because map dimensions are even multiples of cell size, the center lies on cell boundaries and the two systems correspond through integer offsets. Use `TerrainManager` conversion functions.

## D.5. Elevation retrieval

Elevation APIs return binary little-endian floating-point values in the orientation above. Current cell elevations are square float32 grids sharing boundary points with neighbors. Headers `X-Grid-Rows`, `X-Grid-Cols`, and `X-Cell-Version` provide dimensions and version. Citywide current elevation at far resolution and original natural terrain at near resolution use float16. Cells absent from the version list are version zero, unedited.

Interpolation near cell boundaries reads neighboring cells, so versions also reflect surrounding edits. The front end uses versions only to compare with retained terrain. Grounding and geometry use current elevation, rather than independently interpolating original terrain or storage arrays.

## D.6. Coastlines

Coastlines represent land as GeoJSON `Polygon` or `MultiPolygon` geometry in world XZ. At city creation, land is extracted from original terrain at elevation zero, lake and river areas become holes, and geometry is clipped to the map rectangle. Edges produced by map clipping are not shores. Land membership uses even–odd parity across all rings, including holes.

Coastlines are saved and expand during play only when reclamation completes. Road fill, cuts, and grading do not change them. Reload coastlines on every `session:reload` rather than retaining the initial result. They define [parcel types](B_land-geometry.md) and [water crossings](J_path-intersections.md).

## D.7. Water surfaces

Flat water surfaces such as upland lakes and reservoirs can have levels different from the sea at zero. `water_level` is absolute elevation. Their `polygon` uses the coastline format, with inner-ring holes, and bounds where water may exist. Only terrain below the water level inside it becomes a coastline hole. Unity renders a horizontal surface across the full area, with higher terrain visible in front of it. Rivers whose surface height changes downstream are separate.

## D.8. Rivers

A river retains its area and water-surface heights after the terrain editor carves its bed into elevation. The bed is ordinary terrain to the game. Channel, width, and depth used during carving are editor intermediates and are not passed to the game.

River `polygon` uses the water-surface format, with islands as holes. `profile` is a sequence of `[x, absolute elevation, z]` points ordered upstream to downstream, with non-increasing downstream elevation. Water height at any point is the height at the horizontally nearest point on the profile polyline. The editor, core, and Unity share this rule for rendering and path-crossing checks. The entire river area becomes a coastline hole.

## D.9. Natural resources and camera

Scenarios also contain initial [natural resources](../economics/17_supply-and-transport.md) and the starting 3D camera. Camera state consists of position with absolute `y`, clockwise `yaw` in degrees from north, and `pitch` in degrees downward, with 90 pointing straight down. There is no roll. City `camera` is saved, or `null` when unset. Unity updates it whenever the camera stops and restores the view after `game:start` during loading.

## D.10. Scenario terrain

The game does not bundle elevation maps. Scenarios (`.cg`) contain terrain independently. Users import grayscale PNG or `.npy` elevation files to create scenarios. Fresh imports contain elevation and optional background terrain, with default ground cover and climate. See [sessions and saves](../programs/sessions-and-saves.md) for scenarios and API reference for import fields.

Tauri's terrain editor reads and writes elevation, water surfaces, rivers, camera, ground cover, and climate through scenario terrain APIs. Resources and props are edited in scenario creation mode sessions; the terrain editor passes their loaded content through unchanged. Encrypted DLC scenarios are excluded to prevent terrain extraction.

Elevation, water surfaces, and rivers are locked in scenarios containing any paths, buildings, edited cells, or reclaimed land. Construction grounds objects against the terrain at that time; changing only the ground would cause disagreement. Saving elevation, water, or rivers makes the core regenerate derived values such as coastlines and parcel types.

## D.11. Ground cover and climate

Ground cover selects the city's visual surface type and affects rendering only. It is citywide and unchanged during play. Unity uses city `ground_cover` for terrain tiles, overview land, and background terrain; Tauri uses it for 2D elevation colors.

| Shared constant | Appearance |
| --- | --- |
| `GROUND_COVER_GRASSLAND` | Grassland, the default |
| `GROUND_COVER_DESERT` | Desert |
| `GROUND_COVER_LUSH` | Lush grass |
| `GROUND_COVER_SNOW` | Snowfield |
| `GROUND_COVER_SEMI_ARID` | Dry land |

Cities and scenarios save twelve months of weather, temperature, precipitation, and sunshine. See [weather and lighting](U_weather-and-lighting.md).

## D.12. Reclaimed land

When [reclamation](../economics/04_districts-and-parcels.md) completes, the project area is registered as reclaimed land and added to the coastline. Seaward edges become quays, rendered by Unity with concrete tops and vertical walls.

Reclaimed land stores only ID, area, and version. Quays, walls, top-cover bands, and top elevations are derived by the core from current coastline and terrain and returned by APIs. Adjacent reclamation can remove quays and increment the affected area's version. The front end uses returned geometry for rendering and tests rather than reclassifying quays or bands.

Ground immediately inside a quay retains its original seabed elevation, and a wider band covers the step. Unity interpolates between terrain grid points; raising terrain right up to the quay would make it protrude into the sea outside. Inside the band, ground is the higher of terrain and quay top. The core and Unity use this same [grounding rule](E_lod.md).

## Background terrain outside the city

The city boundary equals the detailed elevation map boundary. Background terrain depicts land continuing beyond it, such as peninsulas and continents. It comes from the same real terrain but is neither editable nor subject to earthworks and does not extend city bounds. Seams are not interpolated, so opposite shores inside the city rectangle must also be included in detailed terrain.

Scenarios and saves contain background terrain. Its API returns little-endian float32 elevation in the common orientation. Headers `X-Grid-Rows`, `X-Grid-Cols`, `X-Grid-Spacing`, `X-Origin-X`, and `X-Origin-Z` provide dimensions, spacing, and southwest world-XZ origin. No background returns 204. A coarse coastline supports coloring external land on city maps. Because it can disagree at the boundary, use it only outside the city rectangle.

Roads, tracks, buildings, and parcels can be placed outside city bounds only in scenario creation mode. Roads split at the city boundary on completion, with external sections assigned to the common outside-city district. Automatic changes such as building spawning and equipment deterioration do not occur outside the city.

