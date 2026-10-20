# T. Traffic simulation and rendering

## Responsibilities

The core determines traffic volumes, running times, dwell times, and headways; Unity reproduces those results as movement on roads and tracks. The rendering clock is independent of economic time. Vehicles move during planning without changing city state. Camera position, rendering limits, and frame rate must not affect economic outcomes. Visual queues and positions never write back to the core.

Consistency is measured by passage counts and mean travel times over the same sections and time units, rather than instantaneous speed. Core mean times already include acceleration, curve slowing, following, and stops. Adding stops on top of movement at mean speed would count delay twice.

## Time and position contracts

Exchange time in seconds and distance in meters. Separate free-flow time, congestion delay, and stop dwell time. Passenger waiting and fare-to-time conversions are route-choice costs, not additions to vehicle motion.

Line `journeys` describes section direction, initial and final speed, travel time, and dwell time between stops. Travel times already include curves and gradients. Core ride times, required vehicles, service frequency, and Unity motion use the same values. Trains never depart more frequently than target headway; spare circuit time is spent at the first stop until the next departure. Core frequency and track load include the same wait as Unity motion.

Positions use path ID, direction, and a fraction of horizontal centerline distance. Stops retain positions within paths rather than snapping to endpoints. Respect one-way rules, reversal constraints, and distinct outbound and return routes. Unity path geometry determines visual height; the core determines underlying routes.

## Flow and vehicle counts

In steady state, mean section occupancy equals arrivals per second multiplied by mean residence time. Flow, speed, and displayed counts must remain related. Convert daily traffic to per-second flow using core `active_hours`.

Rendering-limit thinning samples the display without changing economic traffic. Faster playback scales running, dwell, and departure intervals equally. Increasing counts alone and strengthening following would create congestion absent from the core.

## Rendering approximations

Ordinary cars follow preceding cars in the same road, direction, and lane, capped by mean free-flow speed. Congestion delay appears as waiting before intersections instead of uniform slowing. Cars travel at free-flow speed and wait before the next intersection while ahead of cumulative congested travel time across roads. More congested roads therefore show longer queues, with overflow extending into intersections. Cars still unable to move after their allotted time are removed to break mutual waiting across intersections.

Boundary spawning and removal and following delays mean exact passage counts and mean times are not guaranteed. Intersection conflicts, railway block signaling, passing on single tracks, and mutual following between buses and ordinary cars are not rendered.

See [vehicle rendering](O_vehicle-rendering.md) for routes, trains, and clocks, and [public transport](../economics/18_public-transport.md) for economic operating conditions.

