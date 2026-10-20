# IPC

IPC connects Tauri and Unity interaction state. Both obtain city data from the core's HTTP API; IPC carries target IDs, commands, and editing state. See [component integration](component-integration.md) for process relationships.

## Communication structure

Desktop uses an independent MemoryMappedFile (MMF) for each direction; browsers use JavaScript within the page. Both use a common Envelope for sender, recipient, event name, and payload, hiding transport differences from higher-level actions.

MMF is a finite buffer that accumulates messages even without a reader. Each direction has exactly one reader. Do not attach another Unity to the ordinary UI–Unity pair or periodically send state while the peer is absent. Establish the connection and check [compatibility](compatibility.md) before interaction.

Event names follow `target:action`. UI commands are wrapped in `ui:action` and dispatched by Unity. Rust or the web shell receives Unity notifications and forwards them to UI windows. Forwarding is also part of the contract.

| Area | Contract source |
| --- | --- |
| UI sending and receiving | `tauri/src/utils/ipc.ts` |
| Desktop forwarding and Envelope | `tauri/src-tauri/src/ipc.rs`, `messages.rs` |
| Unity sending, receiving, and Envelope | `unity/Assets/Scripts/IPC/` |
| Browser forwarding | `tauri/web/shell/` |

Source comments are authoritative for individual event semantics and payloads; this page does not duplicate their inventory.

## State synchronization principles

- Give state one owner. Unity owns editing geometry and display; the UI sends intentions and displays reported results.
- State notifications contain the current whole state; commands contain the fields to change. Do not echo notifications back into command–notification loops.
- Synchronize on changes and queries. A newly opened window subscribes before querying current state and does not rely on an earlier one-time notification.
- Match asynchronous results to the request generation so old validation or loading results cannot affect a newer edit or city.
- Answer requests that expect responses even on failure or cancellation. Resend current state after reconnection.

## Editing state and confirmation

Unity has one editing state at a time, combining target-specific [input modes](unity-input.md) and [visual modes](unity-visual-modes.md). The UI specifies the editing purpose and initial conditions; Unity configures input, display, and editing state.

The side presenting the confirmation action submits to the core: Tauri for panel confirmation buttons, Unity for edits completed entirely through 3D interaction. Distinguish previews from confirmation and defer final eligibility to the core response.

A shared completion path handles cleanup and data reload together. Do not run asynchronous reload and state switching independently. Cancellation ends only the corresponding edit and must not disrupt a newer operation. [Window closing](ui-foundations.md) joins the same completion path.

## Connection versus loading

Unity being ready to communicate is distinct from finishing city rendering. Reconnection restarts connection establishment, synchronizes settings, and loads the city. See [data loading](../geometries/H_data-loading.md) for rendering completion conditions.

