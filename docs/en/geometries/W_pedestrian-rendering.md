# W. Pedestrian rendering

Unity renders pedestrians only near the camera as visual effects without influence on simulation or persistent core data. Counts derive from daily passages in `path_traffic` under `VEHICLE_PEDESTRIAN`.

As with ordinary [vehicles](O_vehicle-rendering.md), motion and count adjustment are separate. Pedestrians share `RoadsideAccess` entrances and the managed-range center with cars. Differences are bidirectional walking on `PATH_STREET` and `PATH_WALKWAY` sidewalk strips even on one-way roads, constant speeds averaging the core walking speed without travel-time retrieval, and no signals or collisions, allowing people and cars to pass through each other. `PedestrianManager` implements this.

Newly managed sections distribute people across their entire length once road-data scanning completes, rather than waiting for normal entrance or boundary inflow.

## Station and stop users

These are also visual pedestrians. Counts use daily `ridership` in line `stop_points`. Alighting is assumed equal to boarding because return journeys make daily totals approximately equal per stop. The same display multiplier and cap ratio as ordinary pedestrians apply. Individuals represent neither specific citizens nor persistent individuals across scenes.

Platform waiting counts equal boardings per second multiplied by time since the previous departure. They use the vehicle line clock. When a train stops, alighting passengers emerge from doors and waiting passengers enter them.

Bus stops and station entrances connect to road pedestrians. Passersby enter or queue with probability equal to boardings divided by road pedestrian traffic. Alighting and exiting people join road pedestrians; normal count adjustment preserves total traffic density. Station entrances lie at perpendicular projections from building ends to the nearest pedestrian road. Underground stops have no pedestrian rendering.

## Rendering and model names

Pedestrians have no GameObjects and use GPU instancing. Limbs animate without bones by switching three pose models according to distance walked. Fixed-name models live in `Assets/Models/Props/`. Unity reads consecutively from `pedestrian_0` until a number is missing, falling back to humanoid boxes if none exist. Originals are in `blender/props.blend`.

| Name | Pose |
| --- | --- |
| `pedestrian_{n}` | Feet together |
| `pedestrian_{n}_right` | Right foot forward |
| `pedestrian_{n}_left` | Left foot forward |

