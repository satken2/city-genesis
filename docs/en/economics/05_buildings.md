# 5. Buildings

## 5.1. Buildings

Buildings are agents occupying parcels. Shared buildings rent space to households and facilities; dedicated buildings are used by their own facilities, such as factories and power plants. See [real estate](06_real-estate.md) for renting space.

Building type (`bd_type`) classifies uses for building regulations.

| Building type | Constant |
| --- | --- |
| Apartments | `BUILDING_APARTMENT` |
| Detached house | `BUILDING_HOUSE` |
| Retail | `BUILDING_RETAIL` |
| Factory | `BUILDING_FACTORY` |
| Office | `BUILDING_OFFICE` |
| Farmland | `BUILDING_FARM` |
| Mine | `BUILDING_MINE` |
| Port | `BUILDING_PORT` |
| Dedicated building | `BUILDING_OTHER` |
| Public facility | `BUILDING_PUBLIC` |

Buildings must also suit the parcel type. Ports occupy shore parcels; offshore facilities occupy water parcels ([districts and parcels](04_districts-and-parcels.md)).

## 5.2. Construction and city growth

Players plan public facilities; private buildings arise in response to demand for space and profitability. Low rents tend to support simple low-rise buildings, while rising land prices and rents encourage taller buildings and redevelopment.

Construction requires land and construction services. Existing buildings may require relocation of occupants and demolition. Insufficient funds or construction-sector capacity delay completion. Building scale, height, and slope affect construction burdens. See [budgets](15_budgets.md) for public works funding.

## 5.3. Operation, aging, and renewal

Completed buildings allow facilities to produce and households or tenants to move in. Private buildings with low profitability or utilization pause and return to use when demand recovers.

Buildings deteriorate with time and are maintained through repairs. Unused or unmaintainable buildings move toward demolition or rebuilding. Collapse caused by deterioration or fire leaves ruins and can harm people and the surrounding environment ([accidents and disasters](14_accidents-and-disasters.md)).

See [agents](02_agents.md) for status definitions and transitions.

## 5.4. Distinctive facilities

City Hall represents city government on the map. Placing it begins city development. Landmarks attract tourists and give the city its identity. Border checkpoints connect passenger and freight movement to the outside world. These facilities are supplied by scenarios ([tourism](20_tourism.md), [supply and transport](17_supply-and-transport.md)).

## 5.5. Parking

Buildings have `parking_capacity`. Cars gather at workplaces and shops during the day and homes at night, so parking demand varies with time and land use. Cars exceeding building capacity overflow into nearby parking facilities and onto streets. Parking facilities are a land use that accommodates this demand. Buildings that rent floors to tenants must provide the parking area required by the parking requirement (a share of total floor area) when built. Whatever the open space on the site cannot hold is taken from the rentable floors, so the rentable floors can total less than the total floor area. Unlike the district-level building regulations, the parking requirement is citywide, and changing it affects only buildings built afterward.

