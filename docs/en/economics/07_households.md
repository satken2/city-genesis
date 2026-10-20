# 7. Households

This page covers citizens and households, population changes, immigration, commuting, education-related travel, and daytime population. See [employment and education](08_employment-and-education.md) for work, wages, and learning.

## 7.1. Households and citizens

Residents are citizens belonging to household agents (`TYPE_HOUSEHOLD`). Income, assets, consumption, and housing belong to households; age, employment, school attendance, skills, and health belong to citizens. Marital status is either single (`MEMBER_SINGLE`) or married (`MEMBER_MARRIED`); relationships such as parent and child are not represented.

Population counts established residents. It excludes immigrants who have not yet obtained housing and [tourists](20_tourism.md), but includes households awaiting housing after independence, marriage, or eviction.

## 7.2. Population changes

Births, deaths, and immigration change population; established residents do not leave the city. Births depend on the age distribution of women of childbearing age and a fixed birth rate. The rate does not rise when population declines, so generational waves occur. Death depends on age and health. Nutrition, sanitation, air pollution, working hours, and medical care affect health ([scores and achievements](16_scores-and-achievements.md)). Independence and marriage create new households, which begin awaiting housing.

Death statistics distinguish causes such as poor sanitation, disease and old age, and accidents.

## 7.3. Immigration and settlement

Immigrants arrive from outside the city as households. The mayor sets admission policy, including whether to accept them and requirements for age, household composition, and skills. Applicants apply while admission is open, but vacancies, rents, and regulations can prevent them from finding homes. Permission alone does not guarantee population growth.

Applicants search for homes and become residents when they move in. Immigrants awaiting housing are excluded from population; resident social security and similar benefits apply after settlement. Applications are withdrawn after a prolonged unsuccessful search.

Housing demand encourages private residential construction. Immigration also requires jobs, transport, and facilities supporting daily life ([real estate](06_real-estate.md)).

Immigrants generally enter the uneducated or standard workforce tiers. Admission of highly skilled workers depends on city settings, so developing them through local higher education is the usual approach ([employment and education](08_employment-and-education.md)).

## 7.4. Commuting and daytime population

Workers and students make one daily round trip between home and work or school, generating road and public transport traffic. Households with cars drive; others use walking and public transport ([consumption](11_consumption.md)).

Daytime population describes the distribution of workplaces, schools, and homes where residents mainly spend the day. Comparing it with residential population shows movement between areas for work and education.

## 7.5. Stationary population mode

Stationary population mode is a city parameter for scenario authors who want to freeze an established population and focus on urban development. While enabled, population, household composition, and citizen skills remain unchanged, including no deaths from accidents or collapse. Other systems, including the economy, run normally.

