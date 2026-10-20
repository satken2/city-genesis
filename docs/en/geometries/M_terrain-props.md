# M. Terrain props

Forests, shrubs, rocks, and street objects are decoration only, unrelated to economic simulation, costs, or natural resources. The core saves placement definitions; Unity determines individual positions, grounding, visibility, and draw LOD.

## Saved data

Area scattering distributes objects using regions and density; individual placement stores one object per entry. Both have `prop_type` registered in `prop_master`. Scattering `region` uses only the coordinates portion of GeoJSON MultiPolygon, supporting holes and multiple parts. `density` is objects per hectare; `edge_fade_width` is boundary fading in meters, disabled at zero. Individually placed `y` is meters above ground.

Scattering stores no individual coordinates. Unity generates candidates from a world-origin-fixed grid and `seed`, producing identical positions, rotations, and scales regardless of camera position or load order.

## Overlap

Creating or reshaping parcels, paths, or buildings does not change props in the core. Unity hides scattered candidates overlapping parcels, surface or elevated paths, and water when `allow_water` is false. Removing obstacles restores the same individuals. Intentional individual placements remain visible despite overlap. Fetch obstacles with `include_obstacles` and rebuild exclusions only when their geometric `signature` changes; economic updates do not change it.

Saved placement changes only through player placement, editing or removal, full replacement through scenario terrain APIs, or the start of building/path demolition. Demolition deletes individual placements inside the affected area so attached decoration does not float after structures disappear. Area scattering is retained.

## Rendering

`PropManager` subdivides resident terrain cells and creates candidates, exclusions, and rendering batches per subarea. `ForestRenderer` scatters forest trees on the GPU using the same grid and `seed`. Neither creates GameObjects or Colliders per prop. Shared models use GPU instancing and screen-size-based Close, Normal, or Far without reducing counts. Scatter previews and bulldozer selection reproduce the same candidates, preserving positions across confirmation. See [LOD](E_lod.md).

## Model names

Models live in `Assets/Models/Props/`; originals are in `blender/props.blend`.

| Name | Role |
| --- | --- |
| `{prop_type}` | Ordinary model, sufficient on its own |
| `{prop_type}_close`, `{prop_type}_far` | Optional Close and Far models; Unity simplifies the ordinary model if Far is absent |
| `construction_*`, `traffic_signal_*`, `bus_stop`, `ambient_*`, `pedestrian_*` | Fixed names consumed by rendering for construction vehicles, signals, stops, [ambient vehicles](V_ambient-vehicles.md), and [pedestrians](W_pedestrian-rendering.md); not registered in `prop_master` |

To allow manual placement of vehicles and similar objects, export the same geometry under numeric-ID aliases registered in `prop_master`. Moving-vehicle aliases share geometry with [vehicle rendering](O_vehicle-rendering.md) models.

## Scenarios

Scenarios contain initial placement; imported elevation files do not. The terrain editor passes received props back unchanged ([terrain](D_terrain.md)). The scenario terrain API returns scattering as the outer ring of the largest part only, so saving through the terrain editor replaces regions containing holes or multiple parts with that shape.

