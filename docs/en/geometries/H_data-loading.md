# Backend data loading

Unity renders city data from the core's HTTP API within resident cells. It keeps a whole-city copy (`CityDataCache`) of buildings, roads, nodes, parcels, props, and ordinary-car travel times, and draws resident ranges from that copy. Terrain tiles are also built locally from core elevations ([terrain](D_terrain.md)). Camera movement therefore never queries the core, and the camera can explore the city while an epoch runs. `CellDataLoader` owns load triggers and ordering; `TerrainManager` publishes terrain tiles; each `*Manager` owns its data and rendering lifetimes. See [LOD](E_lod.md) for residency and quality and [terrain](D_terrain.md) for formats.

## Update triggers

| Trigger | Update |
| --- | --- |
| Camera movement or projection change | Draw resident-cell differences from the copy and retire departing cells |
| Epoch completion, confirmed edits, `session:reload` | Synchronize the copy with the core, then mark resident ranges stale and redraw by diff |
| District detail save (`zone:updated`) | Reload that district only |
| Forced reload | Remove all rendering objects and reload |
| Session switch or game exit | Clear pending work and all data |

Requests are grouped into load batches; a new batch cancels the older one. Stale markers are set once synchronization finishes and cleared only after successful retrieval and application, allowing the next batch to inherit interrupted work. The copy synchronizes only at the triggers above, never on a timer. An epoch starts only after the copy matches the core. At session start and every `session:reload`, reload coastlines, lakes, rivers, and reclaimed land before parcels and buildings, which depend on quays and reclamation-band grounding.

## Retrieval and diff criteria

| Data | Retrieval unit | Redraw criterion |
| --- | --- | --- |
| Terrain | Original terrain and rewritten cells (resident tiles are built locally), citywide current elevation, background | Cell and citywide versions |
| Buildings, roads, nodes, parcels, props | Whole-city copy | Entity `version`, or placement and obstacle signatures for props, plus rendering-relevant shape, site, and terrain |
| Districts, interchanges/junctions, road names, resources | Citywide lists | `version` and content |
| Citywide overview buildings and roads | Citywide overview list | Content per terrain cell |

To synchronize the copy, receive every building, road, and parcel ID with its `version` from the sync lists, refetch only changed versions through the list APIs' ID query, and drop IDs absent from the sync lists. Values that change each epoch without a `version` bump (utilization, land price, traffic, and similar) come as whole-city columns in the sync lists. Nodes, props, and travel times are taken whole each time. Until the first synchronization completes, ranges missing from the copy are fetched first through range lists, so the initial view appears before the whole city arrives. Rendering removes objects for IDs missing from a range only after reading the whole range. Data receipt and rendering completion are separate: retry interrupted generation even for the same data version. Alerts do not advance rendering versions and use a dedicated API ([notifications](../programs/notifications.md)). Alert-driven 3D effects use the same response and distinguish result generations by its epoch number. Random appearances and placements held in Unity survive redisplay of the same generation, but do not carry over to the next generation or another city.

## Terrain retrieval and publication

Build tiles separately from the display, replacing them only when complete. Display, collider, grounding reference, and version switch together before `OnTerrainTileReady`. Tile-building lifetime follows cell residency and survives batch cancellation. Departing tiles remain hidden for reuse if their versions still match. Tiles for nearby nonresident cells are built ahead when idle.

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

