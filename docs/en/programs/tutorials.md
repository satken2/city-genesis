# Tutorial foundations

Tutorials provide shared guidance and interaction restrictions over lesson cities. Individual screens do not know lesson content; they expose the interfaces the tutorial system observes and guides.

## Component responsibilities

| Component | Responsibility |
| --- | --- |
| Tauri | Lesson definitions, progression, completion checks, guidance, and UI restrictions |
| Unity | Requested markers, camera guidance, and 3D interaction restrictions |
| Core | Lesson city, city-related evaluation results, and explicitly requested delegated actions |

Tauri owns progression. Unity applies current instructions without independently judging progress or success. Tutorial cities can continue in normal play ([sessions and saves](sessions-and-saves.md)).

## Definitions and evaluation

Lessons use declarative combinations of defined conditions and actions. They do not execute arbitrary code or URLs; validate structure and meaning before use.

Guidance stages derive from current screen and city state. Closing panels or canceling actions must leave guidance aligned with actual interaction state.

Conditions are read-only and separate from city-changing actions. Definitions explicitly specify action timing; progression management prevents repeated creation actions.

## Restrictions and guidance

UI components expose stable action names; a shared guidance layer applies permission and highlighting. Components do not embed lesson-specific conditions or separate highlighting. Restrictions must retain paths for exiting and recovering.

Synchronize complete current instructions to Unity so reconnection can restore them. Guidance markers are rendering objects, not city data. Follow [IPC](ipc.md) for synchronization and the schema and source in `tauri/src/tutorial/` for definition formats.

