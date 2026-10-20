# 16. Scores and achievements

## 16.1. Household quality of life

Household quality of life (`qol_score`) describes residents' lives along four dimensions.

| Dimension | Meaning |
| --- | --- |
| Material well-being | Access to clothing, appliances, luxuries, and other goods enriching daily life |
| Entertainment and leisure | Enjoyment of dining out, entertainment, and free time |
| Health | Healthy lives supported by nutrition, sanitation, and medical care |
| Comfort | Home size and quality, quiet, sunlight, and public safety |

Satisfaction depends on income, available products and services, and living conditions ([consumption](11_consumption.md), [environment](13_environment.md)). Longer working hours increase labor but reduce leisure and burden health. Both economic output and daily life matter.

## 16.2. Governance score

Governance score (`city_score`) evaluates residents' lives across the city. It reflects household quality of life and city size, while accident casualties lower it. Because scale affects the total, also consider the per-household evaluation when comparing living standards. Tourists' satisfaction with their trips is treated separately ([tourism](20_tourism.md)).

## 16.3. Quests

Quests are objectives presented by scenarios and tutorials. They can specify targets such as population or finances, require sustaining a state, impose deadlines, or unlock after other objectives. Scenario authors define them; the core judges completion, and the front end displays status and progress.

| Status | Meaning |
| --- | --- |
| `locked` | Awaiting completion of prerequisite objectives |
| `active` | Available to pursue |
| `completed` | Achieved |
| `failed` | Not achieved before the deadline |

Objectives can award grants. These enter city government funds from outside the city and support development ([finance](12_finance.md)).

Completing all supplied quests clears a scenario or tutorial. Clearance records remain as player achievements. See [sessions and saves](../programs/sessions-and-saves.md) for scenarios and [UI foundations](../programs/ui-foundations.md) for statistics and text display.

