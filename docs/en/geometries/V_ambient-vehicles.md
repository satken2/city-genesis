# V. Ambient vehicles

Unity alone renders aircraft crossing the sky and ships moving at sea. They are decorative, are not saved, and never write back to the economy.

Aircraft and offshore ships appear at fixed frequencies regardless of city state, allowing high-altitude airliners without nearby airports. Port calls alone depend on city state: ships travel from open sea to active ports, berth alongside quays, and remain for handling. Each port draws once per epoch, changing when the epoch advances.

Ships use saved coastlines as authoritative land boundaries and navigate only open sea. Reclamation expands coastlines; a changed reloaded version redistributes offshore ships. Vehicles render every frame without GameObjects and advance on `TrafficClock`, stopping during traffic pause ([vehicle rendering](O_vehicle-rendering.md)). Ship wakes are transparent sea overlays sharing foam noise through `Assets/Shaders/WaterNoise.hlsl`.

Entry points are `AmbientVehicleManager` for spawning, movement, and rendering, `AmbientVehicleManager.PortCalls.cs` for port calls, `SeaRoute` for approach routes, and `ShipWake` for wakes.

## Model names

Fixed-name models live in `Assets/Models/Props/`. Ships have `_close` and `_far` models selected by projected size; aircraft use ordinary models only. Numeric aliases of the same geometry support manual placement through `prop_master` ([terrain props](M_terrain-props.md)). Originals live in `blender/props.blend`.

| Name | Vehicle |
| --- | --- |
| `ambient_airliner`, `ambient_propeller`, `ambient_helicopter` | Airliner, propeller aircraft, helicopter |
| `ambient_container_ship`, `ambient_tanker`, `ambient_cruise_ship`, `ambient_fishing_boat` | Offshore ships; all except fishing boats also serve matching ports |
| `ambient_bulk_carrier` | Bulk carrier used only for port calls, without an alias |

