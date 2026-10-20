# Sessions and saves

A session is the currently open city and its interaction context; a save records that city for later continuation. The core owns saving and loading, accessed through APIs by the front end.

## Session identity

`session_id` changes whenever a city is opened and identifies city switches. The overwrite destination `current_save_id` also changes on Save As, so it does not identify a city session. Do not carry IDs, caches, or pending requests across city switches. Master data can also differ per city (the core reloads it whenever a game starts), so fetch it again after a switch.

The UI uses shared session state to distinguish startup from an operable city. The core rejects unavailable operations, such as city APIs before opening a city, another epoch during execution, or scenario terrain editing during ordinary play.

## Ordinary saves and scenarios

Ordinary saves continue cities; scenarios provide starting points for new games. Both store cities including terrain as `.cg` files. Starting from a scenario creates a new save rather than overwriting the original. This differs from editing the scenario itself.

Appearing in a list does not guarantee a file can be loaded. Display the core's compatibility, ownership, and editing eligibility results without reimplementing them. Save timestamps are real-world times, distinct from city dates. See [compatibility](compatibility.md).

A scenario's management source, official status, and required DLC are separate attributes. Store-managed scenarios are read-only; custom scenarios can be edited. Do not infer official status from author names or locations, and use the core's DLC checks. Follow [UI foundations](ui-foundations.md) for multilingual descriptions.

## Play modes

| Mode | Context |
| --- | --- |
| Normal | Manage a city |
| Scenario | Create or edit cities and starting conditions |
| Tutorial | Follow lesson guidance |
| Read-only | Observe without changing the city |

Play mode belongs to the session rather than being fixed in the save. The UI uses shared mode state to expose controls; final permission checks belong to the core. A city can continue in normal play after its tutorial ends.

## Saved state and display state

Construction project tags and citizen tracking lists belong to city records. Camera following, individually hidden objects, and rendered weather belong to presentation and interaction state. See [Unity visual modes](unity-visual-modes.md) for display-state lifetimes.

Unity or the author supplies save images; the UI coordinates them with core saving. The front end never constructs `.cg` files directly. Loading a city does not guarantee all statistics are calculated; heatmaps can remain empty until the next epoch. Display missing values distinctly from zero.

