# 4. Districts and parcels

## 4.1. Districts

Districts divide the city into units for aggregating economic activity and applying building regulations. They are defined by groups of roads; roadside parcels, their buildings, and occupants belong to the same district. Opposite sides of a road can belong to different districts, making that road a boundary. Districts follow road connections and split as the city grows. Players can also select roads to edit district boundaries.

The district outline on the map represents its roads and parcels. Membership depends on relationships to roads, rather than whether something lies inside the outline. Each district has a representative agent that obtains construction services for road building and maintenance, and a relocation agent that receives households and facilities awaiting premises.

## 4.2. Parcels

Parcels are units of land ownership and use. Every building occupies a parcel. Parcels have owners and land prices, and using private land for public works involves acquisition and compensation. Land prices reflect demand for the land and nearby rents.

Parcels have three categories based on their relationship to the coastline. Buildings require compatible parcel types: ports use shore parcels, and offshore structures use water parcels.

| Type | Constant | Meaning |
| --- | --- | --- |
| Land | `PARCEL_LAND` | Land onshore |
| Shore | `PARCEL_SHORE` | Land touching the coastline |
| Water | `PARCEL_WATER` | Sites for offshore structures |

Ports where ships berth stand only on shore parcels whose water just outside the quay reaches the depth each port type requires. The seabed extends the slope of the land into the sea, so steep coasts have deep water nearby and suit ports (natural harbors), while on shallow, flat coasts the land must first be reclaimed out to deep water before a port can be built.

See [land geometry](../geometries/B_land-geometry.md) for shapes and [buildings](05_buildings.md) for their relationship to buildings.

## 4.3. Regulations and location

Building regulations restrict building types, height, volume ratio, and building coverage. District regulations provide the baseline, with parcel-specific overrides. Regulations apply to new construction; changing them leaves existing buildings in place.

Slopes, road access, and surrounding land uses affect how suitable a location is. Slopes increase construction burdens, while distance from roads increases travel and freight burdens.

The location multiplier (`agent_multiplier`) expresses how suitable a parcel is for an industry or household. Shops depend on foot traffic and clusters of similar businesses, factories on clusters of the same and related industries, extractive facilities on natural resources, and households on the living environment. A location's suitability depends on its use.

## 4.4. Earthworks

Earthworks are public projects that reshape terrain within an area selected by the player.

| Type | Purpose |
| --- | --- |
| Land grading | Cut and fill inland terrain to a target elevation |
| Land reclamation | Fill a coastal area to extend the land |

Earthworks create buildable land or new sites, but require funds and construction services.

Projects are represented by temporary public facilities and use the building construction system. The project area is reserved during construction and cannot be used for other construction or land edits. Terrain changes only on completion. Canceling leaves the terrain unchanged. Completed projects remove their temporary facilities and parcels so the land can be used again. See [budgets](15_budgets.md).

Grading changes relief; reclamation extends the coastline. Reclaimed land has quay walls along the sea and creates sites for roads, buildings, and port facilities. The finished terrain remains as the basis for subsequent development. See [terrain](../geometries/D_terrain.md) for geometry and rendering.

