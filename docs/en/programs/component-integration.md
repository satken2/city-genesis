# Component integration

City Genesis consists of a core owning city state and economic calculations, Tauri for the UI, and Unity for 3D rendering and spatial input. The front end displays core results and sends player actions to the core. It does not reimplement economic calculations or edit eligibility.

| Route | Transport | Responsibility |
| --- | --- | --- |
| Tauri / Unity → core | HTTP | Fetch city data, confirm edits, and run epochs |
| Tauri ⇔ Unity | [IPC](ipc.md) | Synchronize selection, editing state, display, and loading progress |

## HTTP and shared state

Treat the core as a service addressed by URL, without assuming it runs on the same PC. Access its files through APIs. Tauri measures client resources such as memory and GPU information itself.

Use each module's common HTTP entry point for consistent version headers and endpoint handling: `fetchApi` in Tauri WebViews, the version-checked common client in Rust, and `ApiClient` in Unity. Tauri retrieves and shares core version and session state centrally; individual screens do not create independent monitors. See [sessions and saves](sessions-and-saves.md).

The locally distributed core listens on `127.0.0.1`; other programs on the same PC can use ordinary APIs. Use the production core for front-end development too. For individual paths, fields, and errors, use Swagger (`/docs`) or OpenAPI (`/openapi.json`) in the distribution.

WebView CORS and CSP are browser restrictions, distinct from Rust and Unity HTTP behavior. The distributed UI does not permit runtime code generation. Development-server and distribution restrictions differ, so verify communication and dependency changes in distributed builds too.

## Process lifetime

On desktop, Tauri is the entry point and launches and monitors the bundled core and Unity as children. It establishes the core connection before launching Unity and checks [compatibility](compatibility.md) before gameplay communication. Development can also connect to an externally launched core or Unity Editor.

The launcher supplies endpoints. `GENESIS_API_PORT` specifies the core's listening port; `--api-base-url` specifies Unity's endpoint; `GENESIS_UI_API_BASE_URL` directs Tauri to an external core.

Actions stop during connection loss. Restarted Unity reloads the city from the core. Tauri centralizes application shutdown regardless of its origin and requests shutdown of processes it launched. The core is given time to finish ongoing work and saving.

Viewer-only Unity (`--viewer`) is a separate rendering client that does not join ordinary Tauri–Unity IPC. It does not edit or save camera positions and can observe the same city as the working client.

## Data ownership

| Data | Owner |
| --- | --- |
| Cities, saves, custom scenarios, player profile | Core |
| UI settings such as language and volume; terrain editor working copy | Tauri |
| 3D display destination and resolution | Unity |
| Placement of subscribed scenarios and DLC | Store |

The profile spans cities; saves belong to individual cities. Session-only state such as editing displays is separate from persistent data. Tauri terrain editing also submits its working copy through the core API to confirm changes.

The shared user data directory is `UrbanGenesis/`. On Windows it is under `%LOCALAPPDATA%`, falling back to `%APPDATA%`; on macOS, `~/Library/Application Support`; on Linux, `$XDG_DATA_HOME`, falling back to `~/.local/share`. Resolve it consistently so the UI opens the same directories the core uses.

Store integration belongs to Tauri, which passes authentication tickets and store-managed directories to the core. Use the core's ownership and loading eligibility results.

## Browser version

The browser version runs the same UI and Unity on one page. UI iframes overlay Unity's canvas; a web shell takes over Rust's window management and session monitoring responsibilities. The core remains an external URL-addressed service.

The UI replaces Tauri APIs with browser implementations at build time; Unity and the UI communicate through JavaScript instead of MMF. Keep environment differences within common entry points rather than creating per-screen state or communication rules.

