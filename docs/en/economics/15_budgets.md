# 15. Budget management

## 15.1. City finances

City government receives taxes and public enterprise revenue and pays for public facility operation, construction, maintenance, and social security. Public facility balances are consolidated in city accounts, including their losses.

Taxes include consumption tax, import tariffs, income tax, and corporate tax. See [finance](12_finance.md) for land and central bank money flows.

## 15.2. Budget categories

Players set each category's budget as a proportion of its required amount. As city growth changes requirements, the same ratio produces a different budget amount.

| Category | Constant | Purpose |
| --- | --- | --- |
| Administration | `LEDGER_CITYHALL` | City Hall operation |
| Healthcare | `LEDGER_MEDICAL` | Hospitals and similar facilities |
| Education | `LEDGER_EDUCATION` | Schools |
| Public safety | `LEDGER_SECURITY` | Police |
| Firefighting | `LEDGER_FIRE` | Fire services |
| Infrastructure | `LEDGER_INFRASTRUCTURE` | Infrastructure facilities |
| Other | `LEDGER_OTHER` | Other public facilities |
| Public transport | `LEDGER_TRANSPORT` | Depots and lines |
| Construction | `LEDGER_CONSTRUCTION` | Construction and demolition of public buildings and paths |
| Maintenance | `LEDGER_MAINTENANCE` | Repairs to public buildings and paths, and daily upkeep of paths |
| Social security | `LEDGER_WELFARE` | Household benefits |
| Land acquisition | `LEDGER_LAND_PURCHASE` | Land for public works |
| Reserves | `LEDGER_RESERVE` | Cash retained without budget allocation |

## 15.3. Budgets and city activity

Operating budgets support public facility employment, production, and procurement. Lower budgets reduce capacity and the services residents receive. The daily upkeep of a public facility's building is also paid from that facility's operating budget and does not shrink when production is cut.

Reduced construction or maintenance budgets limit work and delay completion. Paths such as roads and tracks also receive daily upkeep from the maintenance budget and wear out faster when it falls short. Land acquisition and social security shortfalls restrict purchases and benefits.

When funds are insufficient, budgets shrink according to the maintenance priorities set by the player. Reserves provide for spending fluctuations and future projects.

## 15.4. Social security

Social security provides cash benefits to established resident households: unemployment benefits, pensions for older citizens, and assistance for minimum living standards. Benefit levels and retirement age are city policies. Unemployment, aging, and hardship increase required spending.

## 15.5. Public works

Ordering public buildings, roads, or earthworks requires securing funds for the project. Ongoing construction and land acquisition use city funds alongside new plans.

