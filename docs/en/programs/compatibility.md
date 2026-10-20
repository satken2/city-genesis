# Versioning and compatibility

The core, front end, and IPC use separate versions. Connection compatibility, save compatibility, and identical game rules are distinct concerns.

| Version | Represents |
| --- | --- |
| `core_version` | The core together with its master data and standard parameters |
| `frontend_version` | The combined Tauri and Unity distribution |
| `ipc_contract_version` | The Tauri–Unity communication contract |

## Compatibility between programs

Core and front-end versions independently use `MAJOR.MINOR.PATCH`, compared numerically. Use a front end whose MAJOR is at least the core's MAJOR, and matching Tauri and Unity IPC contract numbers. Do not assume compatibility when versions cannot be retrieved or interpreted.

The public repository's `version.json` is authoritative for the front-end version and IPC contract number; distribution settings and Unity constants are synchronized from it. Retrieve the core version at runtime rather than fixing it in the front end.

The IPC contract includes field types and ID meanings as well as event names. Increment its number when a change requires the other implementation to change. Follow [IPC](ipc.md) for startup checks.

## Meaning of version increments

Core PATCH updates preserve save compatibility and game rules. MINOR updates preserve save compatibility but may change rules or balance. Saves cannot cross MAJOR versions through ordinary loading.

For the front end, use PATCH for fixes, MINOR for added features, and MAJOR for incompatible controls or saved settings, or to follow a core MAJOR update. The distributed UI and graphics pair share one version even when only one changes.

## Saves and rule changes

Ordinary loading accepts saves written by a core version no newer than the running core and with the same MAJOR. Loaded cities use the current rules, so load compatibility does not guarantee identical outcomes.

Rule-change checks use the version that last calculated an epoch, rather than the version that last saved. Merely loading and saving must not bypass confirmation. The UI displays the core's `balance_changed` result and obtains player consent when required. See [sessions and saves](sessions-and-saves.md).

