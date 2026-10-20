# Notifications and alerts

City problems are communicated according to urgency and the importance of location. Temporary interaction feedback and ongoing city problems use separate mechanisms.

## Presentation roles

| Level | Communicates | Presentation |
| --- | --- | --- |
| L1 | Problems and major events that must remain visible | Persistent icons, counts, and notification list |
| L2 | Trends that worsen if ignored | Epoch-result statistics |
| L3 | Events whose location matters | Alerts in 3D space |

Spatial displays alone cannot reveal off-camera problems, so citywide crises also use L1. Severity can change how the same event is presented.

An L1 `warning` means a city function is deteriorating and the player can act soon. Distinguishing it from `caution` avoids making strong warnings routine for nonurgent issues. The core determines severity.

## Notification data and text

The front end displays current notifications returned by the core without recalculating triggers. Distinguish ongoing problems from one-epoch events, and discard old notifications when switching cities.

The core supplies notification types and interpolation values; the UI resolves localized text. Unknown types use generic wording so core updates cannot disable the notification display. Temporary interaction feedback uses the UI's shared notification mechanism.

## Alerts in 3D space

`status` describes lifecycle stages such as construction, operation, and demolition. `alert` describes observed problems such as shortages or accidents. Alerts represent the latest epoch; rendering must not independently create or clear them.

Alert-only changes do not change mesh versions. Unity fetches them through a dedicated API and updates their display independently of mesh diffs. Building and road bits have separate namespaces and are interpreted using shared constants.

Important location-based notifications can also appear spatially, but remain distinct from entity alerts. Shared constants and rendering sources define targets, filters, and effects. Follow [Unity visual modes](unity-visual-modes.md) for display management.

