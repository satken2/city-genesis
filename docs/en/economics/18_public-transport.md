# 18. Public transport

## 18.1. Public transport

Households without cars combine public transport with walking for work, school, and shopping. Buses, metro, LRT, monorail, regional rail, and ferries run on roads, tracks, or ferry lanes.
Ferries carry cars as well as people. They also form routes for private cars and trucks, supporting commuting and freight between islands that roads do not connect. Ferries charge no fare to people or cars.

| Element | Role |
| --- | --- |
| Stop | A boarding and alighting point |
| Waypoint | A point passed without stopping to specify a route |
| Section path | An operating section between adjacent stops (`PATH_TRANSPORT`) |
| Line | An agent that leases vehicles and receives fares |
| Station | A path with platforms for rail stops |
| Depot | A public facility leasing vehicles to lines |

Equipment such as ropeways without line operation is treated as a people mover ([paths and nodes](03_paths-and-nodes.md)).

## 18.2. Lines and networks

Lines follow ordered stops and waypoints. Even out-and-back service is represented as one complete circuit through outbound and return stops. Residents combine walking, riding, and transfers, choosing routes by travel time, waiting time, and fares.

A line's operating sections are distinct from the underlying roads or tracks. Losing infrastructure permits a detour if another route exists; losing connectivity between stops suspends the entire line. Rail also has direction and reversal constraints.

Stations consist of platforms and station buildings, which group platforms and entrances. See [stations](19_stations.md).

## 18.3. Vehicles and capacity

Lines lease vehicles from depots ([supply and transport](17_supply-and-transport.md)). Players set headways and vehicles per train. Insufficient vehicles reduce frequency; no operable train suspends service. Longer trains need platforms that can accommodate them.

Insufficient capacity for passengers causes crowding and increases travel burdens. Lines share road or track capacity, so adding vehicles alone cannot increase capacity indefinitely. Buses and street-running LRT are also affected by car congestion.

## 18.4. Fares and operation

Each line has a flat fare; transfers count as separate boardings. High fares and waiting times discourage use. Lines receive fares and pay depot lease fees. Both lines and depots are public facilities whose balances enter city government finances. Insufficient transport budgets affect depot operation and service frequency ([budgets](15_budgets.md)).

## 18.5. Simulation and rendering

Economic simulation and 3D public transport rendering share routes and travel times. Operating capacity, journey times, and on-screen motion use this common information. See [traffic and rendering](../geometries/T_traffic-and-rendering.md).

Freight trains also use lines and depots and share tracks with passenger trains. See [supply and transport](17_supply-and-transport.md) for loading, unloading, and external trade.

