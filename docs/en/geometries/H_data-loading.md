# Backend data loading

Unity renders city data from the core's HTTP API within resident cells. `CellDataLoader` owns load triggers and ordering; `TerrainManager` publishes terrain tiles; each `*Manager` owns its data and rendering lifetimes. See [LOD](E_lod.md) for residency and quality and [terrain](D_terrain.md) for formats.

## Update triggers

| Trigger | Update |
| --- | --- |
| Camera movement or projection change | Fetch resident-cell differences and retire departing cells |
| Epoch completion, confirmed edits, `session:reload` | Mark loaded data stale and refresh resident ranges by diff |
| District detail save (`zone:updated`) | Reload that district only |
| Forced reload | Remove all rendering objects and reload |
| Session switch or game exit | Clear pending work and all data |

Requests are grouped into load batches; a new batch cancels the older one. Stale markers are set when requested and cleared only after successful retrieval and application, allowing the next batch to inherit interrupted work. At session start and every `session:reload`, reload coastlines, lakes, rivers, and reclaimed land before parcels and buildings, which depend on quays and reclamation-band grounding.

## Retrieval and diff criteria

| Data | Retrieval unit | Redraw criterion |
| --- | --- | --- |
| Terrain | Current elevations per resident cell, citywide current elevation, background | Cell and citywide versions |
| Buildings, roads, nodes, parcels, props | Resident-range lists | Entity `version`, or placement and obstacle signatures for props, plus rendering-relevant shape, site, and terrain |
| Districts, interchanges/junctions, road names, resources | Citywide lists | `version` and content |
| Citywide overview buildings and roads | Citywide overview list | Content per terrain cell |

Building and road lists are paginated. Fetch until no next cursor remains, and remove missing IDs only after every page succeeds. Data receipt and rendering completion are separate: retry interrupted generation even for the same data version. Alerts do not advance rendering versions and use a dedicated API ([notifications](../programs/notifications.md)). Alert-driven 3D effects use the same response and distinguish result generations by its epoch number. Random appearances and placements held in Unity survive redisplay of the same generation, but do not carry over to the next generation or another city.

## Terrain retrieval and publication

Build tiles separately from the display, replacing them only when complete. Display, collider, grounding reference, and version switch together before `OnTerrainTileReady`. Tile-fetch lifetime follows cell residency and survives batch cancellation. Departing tiles remain hidden for reuse if their versions still match. Nearby nonresident cells are prefetched when idle.

Objects compare `TerrainManager.GetTerrainSignature` against the value used during generation to decide whether to rebuild, rather than relying on notifications alone.

## Dependent rendering order

1. Obtain terrain versions, citywide current elevation, and background, then establish resident cells. Tile retrieval and publication proceed concurrently from here.
2. Once the camera stops, fetch roads, districts, resources, and props. Fetch and apply parcels before buildings, because water-parcel buildings use parcel reference elevation. While moving, defer object retrieval and use the citywide overview.
3. Start buildings and roads at Far on citywide elevation; create detail only after their grounding or traversed resident tiles are published.
4. On each tile publication, managers adjust objects in and around that cell to the new terrain.

## Rendering lifetime and time budget

Generation and retirement progress within the shared [frame budget](E_lod.md). Departing roads and similar objects are hidden and retained, restoring when their rendering key matches. Buildings retain outlines nearby even after detail is retired. During road regeneration, keep the old road visible until all new parts are ready, then replace them together.

## Session switching

Stop camera-driven retrieval from `session:reset` until `session:reload`, while the core prepares the next city. Discard terrain, rendering caches, spatial indices, and pending loads per session. Matching version numbers in another city do not permit reuse. Master data and the asset pack can differ per city, so reload them before redrawing after a city switch ([asset packs](X_asset-packs.md)).

## Initial-load completion

Unity sends `game:loaded` once the initial resident view's terrain, parcels, buildings, roads, districts, resources, and props are ready, along with citywide terrain and overview, and the saved camera has been restored. It does not wait for detailed models across the entire city. Keep the camera used for LOD evaluation moving during preparation while disabling input only.

