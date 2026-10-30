# LOD

Unity independently decides which cells remain resident, how terrain is rendered, and the detail level of each object. See [data loading](H_data-loading.md) for retrieval and replacement order and [terrain](D_terrain.md) for data formats and coordinate conventions.

## Responsibilities and owners

| Responsibility | Main implementation | Basis |
| --- | --- | --- |
| Resident range | `LodConfig` / `CameraCellChecker` | Select cells around the focus and camera using field of view and distance |
| Terrain rendering | `TerrainManager` / Unity Terrain | Current-elevation tiles in resident cells; overview land and background terrain outside |
| Grounding | `Utils.GetGroundHeight` | Displayed terrain tiles, falling back to citywide current elevation or background terrain |
| Building and road draw LOD | `DrawLodEvaluator` | Building screen size and road nearest-point distance |
| Props | `PropManager` | Screen size and physical distance |
| Generation time budget | `RenderWorkScheduler` | Total synchronous work across generators |

The far, near, and dynamic names retained in `LodCellSets` and `LodTier` are compatibility entry points for existing managers. Every set contains the same resident cells. Do not infer terrain source, grounding behavior, or fixed far meshes from those names.

## Resident range

Candidates come from around the camera focus and camera position, ranked by view and distance up to a cell limit. The focus is the sightline intersection with the mathematical height-zero plane, rather than a terrain collider. This prevents terrain loading from moving the focus and triggering further loading. Resident cells receive some retention preference so small camera movements do not repeatedly load and unload edge cells.

Outside this range, overview land and background terrain supply ground, while the citywide overview supplies buildings and surface roads. Camera far clipping extends to the most distant map or background corner.

Unity and the core share a map-centered origin. Unity does not shift it when the camera travels far away: moving only Transforms would misalign terrain cells, unloaded-area heights, saved camera coordinates, and drag reference points.

## Terrain rendering

All resident cells use tiles of the same resolution from current elevation computed locally with the core's rule ([terrain](D_terrain.md)); Unity Terrain adjusts on-screen detail. Shared boundary grid points prevent height seams.

Outside resident cells, one citywide overview land mesh covers only the core coastline's land. A water plane supplies the sea, preventing coarse terrain from protruding above water offshore. Shaders sink overview land beneath cells with visible terrain tiles so it cannot poke through.

Tiles, overview land, and external background terrain share surface rendering: procedural colors, cliff rock, beaches, elevation and slope colors, and contours. Adjust each ground cover's colors and surface qualities in `GroundCoverPalette` without changing the shared shader. `TerrainManager` applies identical values to all three material types.

## Grounding

Use `Utils.GetGroundHeight` for CPU ground queries. It returns visible tile height, otherwise citywide current elevation inside the city or background height outside. Reclamation bands use the higher of terrain and quay top ([terrain](D_terrain.md)). Objects provisionally grounded on citywide elevation are repositioned when detailed tiles arrive. GPU terrain-following overlays such as parcel fills use a moving height-texture window around the camera.

## Object draw LOD

Buildings and roads choose Close, Normal, or Far individually; props also support Hidden. Buildings use screen size, roads use distance to their anchor polyline, and props use both screen size and actual distance. Different promotion and demotion thresholds prevent oscillation. Expensive Close assembly is limited to a few nearby objects. Values live in `DrawLodConfig`. See [building meshes](F_building-meshes.md) for building parts and [road meshes](G_road-meshes.md) for far roads.

Promotion from Far waits until the terrain tile beneath a building or crossed by a road is published, avoiding immediate reconstruction of detailed shapes built on coarse citywide elevation. Long normal roads divide surfaces into fixed spline sections with independent draw LOD.

Rendering centerlines, after intersection trimming, are reused while road data and traversed terrain-cell versions remain unchanged. Centerline changes advance the road geometry version, which dependent renderers such as vehicles and transit highlights use to detect changes.

## Citywide overview

`CityOverviewManager` renders buildings and surface roads across the entire city from the beginning, independently of residency. The core's overview list is combined into meshes per terrain cell. Buildings use boxes at Far heights; roads use Far models or strips, grounded on citywide current elevation. Objects with real Far, Normal, or Close rendering, and objects hidden by filters, are excluded to avoid duplication. Reload the list after epochs and confirmed edits and rebuild only changed cells.

## Props and forests

Area forests are scattered on the GPU. Other props thin resident candidates using a fixed ranking derived from saved coordinates, retaining the same points when budgets change. See [terrain props](M_terrain-props.md).

## Generation budget and lifetime

Generation shares a frame-time budget. Start and stop generation coroutines through `RenderWorkScheduler`, which limits total synchronous generator work to a fixed fraction of the actual frame time, never less than that fraction of the target frame time. Do not call Unity's `StopCoroutine` directly: it does not dispose nested work. Stop work when its owning GameObject is disabled or destroyed. Publish terrain tiles, place parcels, and take in building data ahead of other work because object detail depends on terrain and building generation waits for parcels and building data. Work that may appear later, such as building ground panels, some site decoration, and ambient pedestrians and vehicles, runs at low priority: only with budget left over by normal generation. Deferred site decoration is built for the nearest buildings first.

Meshes from retired rendering objects are retained in an LRU with per-target and global limits derived from PC memory.

## Water rendering and depth offsets

The sea is a scene plane at absolute elevation zero. Lakes and rivers use the same `Custom/WaterLit` shader. Terrain tiles extend below sea level to represent the seabed, so zero-elevation terrain and sea can have identical depth. Shader depth offsets determine ordering and prevent flicker.

| Target | Shader | Equal-depth ordering |
| --- | --- | --- |
| Buildings, roads, and other objects | `Custom/DepthOffsetLit` | In front of terrain |
| Sea, lakes, rivers | `Custom/WaterLit` | In front of terrain, covering the bed |
| Resident terrain tiles | `Custom/DepthOffsetTerrainLit` | Behind objects and water |
| Overview land | `Custom/DepthOffsetSimpleLitFar` | Furthest behind |

This exposes shores along the terrain's zero-elevation contour. Replacing object or water materials with shaders lacking depth offsets causes distant Z-fighting.

## Road-marking LOD and image budget

Road-marking image resolution depends on screen size independently of road geometry LOD. Markings are baked into content-shared pages and retained within a fixed image budget. When insufficient, reduce resolution or omit complex markings. Marking-image readiness is not an initial-load completion condition.

