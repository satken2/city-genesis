# Unity visual modes

Visual modes change city rendering by layer. They are independent of [input modes](unity-input.md), with both applied together on entry to an editing state.

## Display dimensions

| Dimension | Role |
| --- | --- |
| Color mode | Select normal, hidden, data coloring, or similar display for each layer |
| Display flags | Show planned entities or supplementary information independently of color modes |
| Individual filters | Hide specific buildings or roads by ID |

Unity's common management owns display state. The UI requests changes and displays results. Rendered objects do not choose state independently; newly generated or restored objects receive current settings. Hidden objects are also removed from click targets.

Individual visibility filters last only for the session and are not saved. Construction project tags are core-persisted city data. The UI resolves tags to IDs; Unity handles them as rendering targets.

## Normal and temporary display

Distinguish the user's ordinary display choices from temporary views for editing or inspection. Ending editing restores normal display without overwriting user choices.

Ordinary changes use `view:change_view`; temporary views from screens without editing states use presets. Bypassing common management can make the UI and actual rendering disagree. See [IPC](ipc.md) for synchronization.

## Display and city data

Underground display makes terrain and surface objects transparent to reveal subsurface structures. It does not change terrain height or colliders, preserving camera, placement, and editing references.

Heatmaps, highlights, and [alerts](notifications.md) also present core-provided state. Display changes never alter city state or simulation results.

