# Fire effects

Building fire (`ALERT_AGENT_ON_FIRE`) is shown as 3D flames, embers, black smoke, and flickering point lights rather than an icon. Buildings burned down during the epoch (`STATUS_COLLAPSED`) emit thin white smoke from smoldering rubble. Ignition (`ALERT_AGENT_ACCIDENTAL_FIRE`) has no 3D effect. Fires are epoch events; flames and smoke disappear when the bit clears in the next epoch. See [notifications and alerts](../programs/notifications.md) for bit meanings and delivery.

## Data and display

There is no separate API. `FireEffectManager` receives alert-list results from `AlertManager`. Shared `ALERT_AGENT_VFX_BITS` separates fire bits from icons, and the display panel's alert toggle also controls flames and smoke.

Effects sit at building bases independently of building GameObjects, sized from footprint and height. Textures are generated in code at startup without image assets.

## One-shot demolition explosion

After a successful building explosion API call, the UI sends `building:exploded` to Unity ([IPC](../programs/ipc.md)). Unity reloads the building, switches to [rubble](F1_building-mesh-methods.md), and plays one-shot flames, smoke, and explosion audio at the same position through `VisualEffectManager`.

