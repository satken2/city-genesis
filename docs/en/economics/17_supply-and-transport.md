# 17. Supply and transport

Natural resources, infrastructure goods, and public transport vehicles are supplied differently from ordinary market products. Market-traded materials also require roads, on-site logistics, or dedicated networks. See [product market](09_product-market.md) for trading and pricing.

## 17.1. Natural resources

Natural resources are deposits, oil fields, water sources, and similar features distributed across the map. Extraction facilities placed on resource-bearing land obtain production inputs. Scenarios define resource distribution, influencing viable industries and locations.

### Extraction, depletion, and recovery

Resources have reserves that decrease with extraction. Deposits and oil fields do not recover; resources such as water sources do. Falling reserves reduce extraction efficiency, and depletion prevents continued production there. Renewable resources also become scarce when extraction exceeds recovery. Extracted materials enter the product market or dedicated infrastructure networks ([product market](09_product-market.md)).

### Open-pit mining and terrain

Open-pit extraction lowers the land and changes terrain. Pits remain after facilities disappear, affecting the landscape and future land use.

## 17.2. Infrastructure networks

Infrastructure networks deliver electricity, water, and similar products along dedicated connections between facilities. These products are infrastructure goods (`GOODS_INFRASTRUCTURE`). They flow between connected public facilities outside the ordinary market. Nearby roads alone are insufficient; facilities must connect to the dedicated network.

### From production to consumers

Power plants send electricity through transmission networks to substations, which deliver electricity services to households and facilities.

```mermaid
graph LR
powerPlant["Power plant"] -->|High-voltage electricity| substation["Substation"]
substation -->|Electricity service| consumers["Households and facilities"]
```

Water similarly moves from intake facilities through conduits to water pumps, then to consumers. Production, transport networks, and local delivery facilities all support the city. See [product market](09_product-market.md) for electricity service delivery to consumers.

### Paths and connections

| Path | Constant | Purpose |
| --- | --- | --- |
| Power line | `PATH_ELECTRICITY` | Electricity transmission |
| Water pipe | `PATH_WATER` | Water transport |
| Pipeline | `PATH_PIPELINE` | Crude oil and similar products |

Facilities have terminals for compatible paths. Paths form separate networks according to the goods they carry. Electrified tracks carry both trains and electricity; at rail depots, these connections support vehicle supply and power reception.

### Capacity and shortages

Intermediate path capacities and network connectivity constrain delivery as well as production capacity. Spare generation cannot prevent outages if transmission routes are too narrow or disconnected. Weather-dependent solar and wind generation serve a different role from demand-following facilities ([production](10_production.md)). Dedicated crude oil and ore networks use the same connectivity and capacity principles.

## 17.3. Freight

Materials and products move by road, on-site logistics between nearby facilities, or dedicated networks such as pipelines and freight rail lines.

| Mode | Purpose |
| --- | --- |
| Trucks | Road freight associated with market transactions |
| On-site logistics | Transfers between nearby facilities without using roads |
| Dedicated networks | Bulk transport through pipelines or freight lines |
| Ports and international freight stations | Gateways for external trade |

Products use individual units such as kilograms, barrels, or vehicles, but transport scale is measured by weight. Congestion, network capacity, and hub handling capacity constrain logistics.

### On-site logistics and dedicated networks

Nearby facilities can transfer materials through pipes, conveyors, and similar equipment without transport costs. Clustering factories, mines, and ports reduces road loads and freight burdens.

Dedicated networks carry bulk crude oil and ore. Goods enter at loading facilities and return to the product market at unloading facilities. Connections and capacities follow infrastructure network principles.

### Freight lines and loading

Freight trains require line configuration and lease vehicles from depots, just as passenger trains do. They share tracks with passenger services and require track capacity ([public transport](18_public-transport.md)). Dedicated platforms compatible with the freight type work together with loading and unloading facilities.

```mermaid
graph LR
mine["Mine"] -->|Ore| loadingStation["Loading station"]
loadingStation -->|Freight train| unloadingStation["Unloading station"]
unloadingStation -->|Ore| steelworks["Steelworks"]
```

Loading and unloading stations are public facilities connecting local industry with long-distance logistics.

### External trade

Seaports, international freight stations, and road freight hubs support trade beyond the city. International freight lines connect scenario-provided border freight stations outside the city with local international freight stations. Station handling capacity and line transport capacity limit trade volume. See [product market](09_product-market.md).

## 17.4. Public transport vehicle procurement

Depots lease public transport vehicles (`GOODS_TRANSIT`) to lines, which pay lease fees. Passenger and freight lines use the same procurement system. Depots need labor, materials, and road or track connections to lines. Depot capacity supports the number of vehicles that can operate in the city. See [public transport](18_public-transport.md) for headways, train lengths, and fares.

