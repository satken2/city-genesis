# 20. Tourism

## 20.1. Tourists

Tourists are agents of variant `VARIANT_TOURIST`, with each agent representing a group. They occupy hotels and consume at restaurants, entertainment facilities, and attractions. They share the product market, real estate market, and transport network with residents. Individual travelers' arrivals and departures are not tracked; tourists are represented as continuously staying groups.

## 20.2. Tourism facilities

Attracting tourists requires both attractions and gateways. Accommodation demand also encourages private hotel construction.

| Facility | Role |
| --- | --- |
| Attraction | Supplies tourism experiences; includes mayor-built facilities and scenario-provided landmarks |
| Gateway | Supplies external passenger transport through passenger ship terminals and other external passenger transport hubs |
| Hotel | Supplies accommodation space that tourists can occupy |

## 20.3. Reputation and city income

Attraction and gateway capacity and hotel vacancies limit visitor numbers. Experiences and accommodation costs affect satisfaction, reputation, and changes in tourist numbers. Tourists' travel funds are income from outside the city. Tourism both increases local demand and brings external money into the economy ([finance](12_finance.md)).

