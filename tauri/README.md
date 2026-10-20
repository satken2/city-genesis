English | [Japanese (original)](README.ja.md)

# City Genesis UI

The Tauri, React, and TypeScript interface overlaid on Unity. React renders screen content; Rust manages windows and processes. The browser version uses the same UI, with a web shell taking over Rust's responsibilities. See [component integration](../docs/en/programs/component-integration.md).

## Run

Install dependencies with `npm ci`, then run:

```powershell
npm run tauri dev
```

For X11 under GNOME on Linux, use `GDK_BACKEND=x11 npm run tauri dev`.

Start the development mock parent with `npm run mock-parent`. Open its Web UI at [http://127.0.0.1:18765](http://127.0.0.1:18765) and select “Debug Launcher”.

Start the browser version with:

```powershell
npm run dev:web
```

Open [http://localhost:1420/](http://localhost:1420/). `VITE_CORE_API_URL` in `web/.env` sets the core URL; place local overrides in `web/.env.local`. It uses port 1420, permitted by core CORS, so it cannot run alongside `npm run tauri dev`. It loads a Unity Web build from `unity/Builds/Web` if available and otherwise runs without graphics.

## Build

```powershell
npm run tauri build
```

The browser build outputs to `dist-web/`. Creating Unity's Web build through Tools/Build/Web first copies it to `dist-web/unity/`.

```powershell
npm run build:web
```

Steam builds enable store integration (`build.ps1`). Bundle the core as `Core/` and Unity as `Graphics/` beside the Tauri executable, along with `steam_api64.dll` generated under `src-tauri/target/release/build/steamworks-sys-*/out/`.

```powershell
npm run tauri build -- --features steam --no-bundle
```

`--no-bundle` builds the executable for ZIP and Steam distribution without generating MSI or NSIS installers.

## Tests

```powershell
npm test
```

This runs all tests under `tools/`. Use commands such as `npm run test:tutorial` for an individual group.

## Synchronize front-end versions

Edit the front-end version and IPC contract number in repository-root `version.json`, then run `npm run sync:version` to update distribution settings and Unity. Do not edit generated version definitions manually.
