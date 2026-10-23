# 9. Product market

## 9.1. Products

Products are the goods and services agents produce and consume. There are six categories.

| Category | Constant | Meaning |
| --- | --- | --- |
| Natural resources | `GOODS_RESOURCE` | Raw materials extracted from land ([supply and transport](17_supply-and-transport.md)) |
| Industrial materials | `GOODS_MATERIAL` | Intermediate inputs, transported by trucks and similar means |
| Retail goods | `GOODS_RETAIL` | Goods consumed by households; buyers visit shops |
| Services | `GOODS_SERVICE` | Services such as healthcare and firefighting; some have limited reach |
| Infrastructure goods | `GOODS_INFRASTRUCTURE` | Electricity, water, and similar products delivered through dedicated networks ([supply and transport](17_supply-and-transport.md)) |
| Public transport vehicles | `GOODS_TRANSIT` | Vehicles leased by depots to lines ([supply and transport](17_supply-and-transport.md)) |

Industrial materials, retail goods, and services trade in the product market. Natural resources and infrastructure goods are supplied outside it; public transport vehicles are leased.

## 9.2. Supply and demand

Agents seek products for production and daily life; standard facilities and trade hubs supply them.

| Term | Meaning |
| --- | --- |
| Demand (`demand_amount`) | Amount an agent seeks to obtain |
| Supply (`supply_amount`) | Amount a seller offers to the market |
| Received (`received_amount`) | Amount obtained during the epoch |
| Inventory (`remain_amount`) | Amount left after consumption |

Receipts and inventory differ. Some materials can be stored until the next production cycle, while retail goods and services are treated as consumption within the epoch. Some basic needs can also be met through self-provision outside the market ([consumption](11_consumption.md)).

## 9.3. Transactions and movement

Buyers consider price, travel or freight burdens, and seller attractiveness. Nearby sellers and good road and public transport access make trading easier.

Industrial materials mainly move as freight; retail buyers travel to shops. These movements generate traffic. See [supply and transport](17_supply-and-transport.md) for on-site logistics and dedicated networks.

Some services reach the entire city; others reach only nearby areas. Arrival time matters for services such as firefighting, while distance from the facility or terrain determines other service areas. Even within reach, insufficient capacity can leave demand unmet.

## 9.4. Prices and taxes

Selling prices reflect production costs and supply and demand. The mayor sets some public service prices ([production](10_production.md)). Retail transactions incur consumption tax, and imports incur tariffs, providing city revenue.

## 9.5. Imports and exports

Trade hubs import products the city lacks and export products that cannot be sold locally. Ports and international freight stations act as gateways, each with a handling capacity.

Trade uses external prices and is affected by overseas freight costs and tariffs. Heavy, inexpensive goods are costly to transport and tend to favor local production; light, expensive goods can trade over greater distances. See [finance](12_finance.md) for trade-related money flows.

