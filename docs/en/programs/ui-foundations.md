# UI foundations

The Tauri UI consists of multiple WebViews. Rust manages window lifetimes, placement, and shared state; React renders content. In browsers, the web shell takes over the same responsibilities ([component integration](component-integration.md)).

## Window lifetime

Common management defines window types, placement, and which windows can coexist. Initial data is passed through window context rather than events sent before subscriptions exist.

Closed popups can remain hidden and be reused, so React unmounting does not mark the end of interaction. Reset targets and editing state on reopening, and prevent hidden-window events from causing side effects such as Unity actions. Window management ends editing and clears highlighting on close.

Transparent WebViews also receive input, so persistent UI windows must fit their actual panel area. Common management handles placement across monitors and DPI settings; screens do not correct positions independently.

## Shared state and data display

Use common session, version, and play-mode state without duplicating retrieval or checks per screen. Follow [IPC principles](ipc.md) for editing and display synchronization. After a successful change affecting multiple windows, the changing side emits a global event and recipients refresh their displays.

Statistics use a common cache keyed to endpoint and epoch and display finalized API values. Distinguish missing values from zero and do not substitute current-period values into historical breakdowns.

City and facility diagnostics display trees of factors investigated by the core from observed values; the UI does not reimplement economic checks. Factors and suggested actions arrive as identifiers, with wording translated in the UI. Suggestions lead to existing operation screens, where the player chooses policies or actions affecting citizens. Defer diagnostics during epoch execution and investigate again after completion.

2D maps use `CityMapCanvas`, with each screen supplying layers and overlays. Coordinates and orientation follow [terrain](../geometries/D_terrain.md); screens do not create separate conventions. Route guidance reflects the core's calculation-time results. Do not assume immediate road-edit reflection or exact shortest routes between arbitrary points. Distinguish travel times from route-selection weights.

## Common styles

Use shared tokens for colors, shadows, and state colors, and shared components for forms and tabs. Align body text, ordinary fields, and action labels to the common base size; vary headings, aggregate values, and supplementary text according to information roles.

Font sizes follow root settings, and dimensions tied to body text generally use rem. Treat physical coordinates, such as OS window placement, separately. Adapt to narrow windows according to width rather than runtime environment. Consult shared styles and component sources for concrete tokens and dimensions.

## Languages and author text

UI text belongs in i18n resources. Keep keys and [game terminology](../contributing/glossary.md) aligned across supported languages, using common language and locale definitions.

Player-facing explanations should give concrete causes, consequences of actions, and things to check when making decisions. Treat loading-screen Tips as part of the manual, explaining what city-building choices lead to without exposing internal formulas or processing steps. Each item should introduce the context needed to understand it on its own, without familiarity with other Tips or earlier wording. When expanding an explanation, preserve its subject and original humor instead of padding it with obvious cautions.

Scenario and quest descriptions are author-entered multilingual data, separate from UI translation resources. Use common input and resolution mechanisms, falling back to mandatory English when the display language is missing. The language of city proper names is also independent of UI language.

