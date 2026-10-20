# 11. Consumption

Households obtain goods and services for daily life through the product market. Consumption depends on household funds, prices, and the city's supply capacity.

## 11.1. Consumption budget

Discretionary income (`discretionary_income`) represents the budget a household can spend in the market. Households reserve money for rent and basic security, then spend the remainder. Savings mean unemployment does not immediately make daily life unsustainable. Self-provision covers some minimum needs that cannot be purchased ([product market](09_product-market.md)).

## 11.2. Demand categories

Demand has three tiers according to priority in daily life.

| Tier | Meaning |
| --- | --- |
| `DEMAND_TIER1` | Necessities |
| `DEMAND_TIER2` | Ordinary consumer goods |
| `DEMAND_TIER3` | Entertainment and luxuries |

Households prioritize basic needs and expand consumption as resources permit. Demand depends on household size, age, health, and available leisure.

## 11.3. Satisfaction

Satisfaction measures how well needs for goods and services were met.

| Measure | Meaning |
| --- | --- |
| Nutrition (`satis_nutrition`) | Whether sufficient food is available |
| Healthcare (`satis_healthcare`) | Whether necessary medical care is received |
| Tourism experience (`satis_tourism`) | Whether tourism experience demand is met |
| Entertainment and leisure (`satis_leisure`) | Whether entertainment and leisure can be enjoyed |
| Material well-being (`satis_wealth`) | Whether goods enriching daily life are available |

Nutrition and healthcare affect health; entertainment and material well-being affect quality of life ([scores and achievements](16_scores-and-achievements.md)).

## 11.4. Car use

Cars are treated as rentals with recurring costs. Households choose them by considering convenience and expense. Use requires funds and vehicle supply, and public transport convenience also affects the choice. Households with cars use them for work, school, and shopping; others use walking and [public transport](18_public-transport.md).

