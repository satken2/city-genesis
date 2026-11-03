# 8. Employment and education

## 8.1. Employment

Employment belongs to individual citizens; each citizen has one workplace. Wages are paid by the workplace to the citizen's household. Facilities seek the labor needed for production, and citizens choose jobs considering wages and commuting burdens. Labor shortages and job openings also affect wages.

## 8.2. Skills and wages

Citizens have six skills. Required skills differ by industry.

| Constant | Skill |
| --- | --- |
| `SKILL_BASE` | General education |
| `SKILL_PHYSICAL` | Physical labor |
| `SKILL_OFFICE` | Office work |
| `SKILL_RETAIL` | Sales |
| `SKILL_TECHNOLOGY` | Science and technology |
| `SKILL_MEDICAL` | Medicine |

Wages are proportional to a worker's effective labor (the productive capacity determined by skill and working hours). Each facility has a single wage per unit of effective labor (wage rate), so within the same facility, workers with higher skill in what the job requires earn more. Skills developed through education or work experience show up directly in the worker's wage.

Skills have boundaries that only education can cross, and skills place workers in uneducated, standard, or highly skilled tiers. Work experience raises skills only up to the next boundary; advancing to a higher tier requires education. These tiers are used, among other things, to identify highly skilled workers.

## 8.3. Headcount and effective labor

A facility's employment quota (`quota`) and employed labor (`total_labor`) measure effective labor, rather than people. Skills and working hours change the productive contribution of the same headcount. Education therefore improves the city's productive capacity.

Public facility employment is also constrained by city budgets. Longer scheduled hours increase labor but reduce leisure and burden health ([budgets](15_budgets.md), [scores and achievements](16_scores-and-achievements.md)).

## 8.4. Education

Education develops skills and broadens the kinds of work citizens can perform. Basic education provides foundations for many jobs. Higher education develops specialist skills in citizens with basic education, allowing them to become highly skilled. Work experience has limits, so schools and universities are needed at their respective stages.

Schools are standard facilities (`TYPE_STANDARD`) that supply education (`PRODUCT_EDUCATION`); they employ teachers and use electricity and water. Citizens need an age-appropriate school that they can reach and that has capacity. The number of students a school can accept is set by the size of its buildings, but it falls when teachers or electricity are short or the education budget is cut. In a school operating below its capacity, enrolled students learn more slowly. Players allocate university admissions by major, choosing which specialist fields the city develops.

Highly skilled workers are normally developed through local higher education. Universities take time to produce graduates, so healthcare and advanced industries need education planned ahead. See [households](07_households.md) for immigration.

## 8.5. Highly skilled workers

A highly skilled worker has at least 1.5 in a skill other than general education. Citizens reach this level by studying a specialty at university after basic education; work experience alone is insufficient. Some facilities, including medical facilities and advanced factories, can operate only to the extent that they employ highly skilled workers in the relevant field. Adding general workers cannot replace those specialists. The requirement is counted in people: a facility too small to need one whole specialist operates without any.

