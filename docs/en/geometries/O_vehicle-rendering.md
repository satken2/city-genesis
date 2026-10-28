# O. Vehicle rendering

Unity renders ordinary road traffic, street parking, line-operated buses and trains, and ropeway cabins only around the camera. These are visual objects and never write back to the economy, persistent core data, or saves. The core owns traffic, travel times, and operations; Unity reproduces them as motion on roads and tracks. See [traffic and rendering](T_traffic-and-rendering.md) for time and flow contracts.

## Ordinary vehicles

Inputs are path `path_traffic` and the free-flow and congested passage seconds and `length_m` from `POST /api/v1/paths/motion`. `path_traffic` gives passages per day by vehicle ID using string keys, without directional or turning breakdowns. `length_m` is horizontal centerline length for the same journey as the two times, rather than endpoint distance.

Target vehicle counts approximate daily traffic divided by activity hours, multiplied by passage time within the managed range. Round the total across roads, rather than rounding up each road. Newly managed sections from initial display, camera motion, or added roads receive vehicles already dispersed at target density. Existing managed vehicles are retained. New sections are populated even while the traffic clock is paused, with body creation following the frame budget.

Movement and count adjustment are separate. Vehicles continue through intersections; ordinary count differences are corrected through building entrances and managed-range boundaries. Rendering-only entrances (`RoadsideAccess`, shared with pedestrians) come from road-facing parcel edges, rather than model doors.

The managed area is a circle around the camera focus, or the ride position when aboard, or the followed target when tracking a citizen. Parking and pedestrians use the same center. Lane bounds share `RoadLaneLayout` with markings; driving positions stay within those lanes, while parking positions follow the carriageway edge. At intersections, next roads are sampled by same-type traffic volume. Signals, yielding, and collisions are not modeled. These visual rules do not alter routing or core travel times.

Ordinary vehicles and pedestrians represent traffic rather than individual citizens one-to-one. Selection requests a core estimate; tracking visualizes that itinerary. Neither estimation nor tracking changes city state.

## Street parking

`parked_cars` is the simultaneous parked count, including legal and excess parking, and needs no daily-traffic conversion. `ParkedVehicleManager` places stationary cars on streets with positive `street_parking_capacity`, capping display at capacity. Road-ID-based layouts remain stable as the camera moves.

## Line-operated vehicles

`TransitVehicleManager` renders only trains that active lines can form from procured vehicles. Displayed train count is integer `vehicle_count / cars_per_train`, rather than `train_count`, the number required for operation. Time-indexed core `journeys` profiles and dwell times determine positions, advancing off-screen on the same clock without accumulated error. Stop positions refer to train centers. Track choice and lateral position share [track mesh](N_track-meshes.md) definitions. A train is hidden if any required route geometry is unloaded; gaps are not bridged with straight lines.

Player-selected `vehicle_model` affects appearance and running sound only. `line_color` colors body bands and similar parts. `Resources/Config/` files map line types, vehicle types, appearance choices, colored parts, and running sounds. Appearance IDs match `tauri/src/data/transitVehicleModels.json`.

Public transport highlights use the same running geometry rather than straight lines between stops.

## Model names

Models live in `Assets/Models/Vehicles/{vehicle_type}/`, with originals in `blender/vehicles.blend`. Unity searches `{vehicle_type}/{appearance}`, then `{vehicle_type}`, passenger car `VEHICLE_STANDARD`, then a box, so missing models do not prevent operation.

| Name | Role |
| --- | --- |
| `0`, `1`, … | Appearance-specific bodies; train leading cars, reversed for the last car |
| `middle`, `middle_{appearance}` | Intermediate train cars; appearance zero uses `middle` |
| `wagon`, `wagon_{n}` | Freight wagons |
| `{model}_close`, `{model}_far` | Optional Close and Far models |

Numeric aliases of the same geometry are registered as [terrain props](M_terrain-props.md) for free placement.

## Traffic clock

Ordinary vehicles, public transport, ropeways, aircraft, ships, and pedestrians use `TrafficClock`, rather than Unity-wide `Time.timeScale`. Traffic pause stops only this clock. Vehicles report positions and speeds to shared audio management instead of playing independent sounds ([sound](../programs/sound.md)).

## Drive mode

`DriveMode` lets players drive cars, ride trains, or watch moving vehicles from aboard. It reuses existing visual vehicles and moves the managed-area center to the ride position. Ride state is neither core data nor saved. Startup uses a UI round trip ([IPC](../programs/ipc.md)).

## Ropeway circulation

`RopewayCabinManager` circulates cabins on completed `PATH_CONVEYOR` sections connecting at least two stations. This visual effect has no headway, vehicle procurement, or capacity and is independent of traffic volume and epochs. Cabin and terminal models live in `Models/Paths/`.

