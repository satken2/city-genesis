# Sound system

Sound presents city and interaction state without changing simulation or saves. Unity handles spatial audio; Tauri handles background music and UI sounds.

## Playback responsibilities

Unity handles nearby ambience, weather, construction, and vehicles. Avoid giving every city entity a sound source. Rendering range and shared source management prevent playback cost from scaling directly with city size.

Tauri centralizes background music. Screens and effects submit requests with priorities and release them when finished; windows do not play independent music. One-shot UI sounds also use shared playback.

## Settings synchronization

Tauri owns volume settings and synchronizes Unity's effects-channel volumes on settings changes and reconnection. Unity stays silent until settings arrive and applies them to audio components created later.

Handle volume within each playback channel rather than adding a second control through the overall Unity listener. Follow [IPC](ipc.md) for synchronization.

## Assets and distribution

Unity audio lives in `Assets/Resources/Audio/`; Tauri audio lives in `src/assets/sounds/`. Tauri includes assets through imports instead of relying on development source paths.

Asset directories and processing scripts define formats, import, and processing requirements. Follow [assets and credits](../contributing/asset-credits.md) when importing assets.

