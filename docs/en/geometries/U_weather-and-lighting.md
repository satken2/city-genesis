# Weather and lighting

Unity uses city climate settings to render sky, clouds, sunlight, shadows, haze, sky reflections, precipitation, and time-of-day sun motion. Rendered weather and time belong only to Unity and do not affect simulation or saves. `AppRoot` creates the entry point, `WeatherController`.

## Responsibilities

Core master `/api/v1/master/weather` defines weather IDs. City `climate` from `GET /api/v1/city/` supplies occurrence proportions by weather ID for each of twelve months. Unity stores appearances in `WeatherPreset` and selects weather from the current city month's proportions. Tauri owns cockpit labels and emoji. Weather-ID changes must align all three. Finalized epoch climate affecting production is distinct from rendered weather ([production](../economics/10_production.md)).

Independently of epochs, weather slowly moves between neighboring states, from increasing cloud through precipitation and clearing. Long-run time shares match monthly proportions. Visual time also advances independently at a constant rate, skipping night by passing from sunset through brief twilight to sunrise. Neither is saved.

Saved climate contains monthly weather proportions, average temperatures, precipitation, and sunshine. Finalized epoch climate is a core result; editing climate settings does not rewrite history. Unity uses monthly rendering proportions without recalculating production multipliers.

## Epoch coordination

Epoch execution darkens the screen and advances the date behind it. Effect IDs match coordination messages.

1. On confirmed execution, the UI sends `epoch:prepare` alongside the HTTP request. Unity advances the sun to sunset and covers the screen with black.
2. On the frame after drawing black, Unity returns `epoch:darkened`. The UI waits before showing results, displaying tips if processing remains incomplete. It stops waiting if Unity is disconnected or unresponsive.
3. Closing results sends `epoch:executing` with `executing: false`. Unity reloads the city, removes black, and moves to morning. If the epoch advanced, it selects new weather behind the curtain.

Weather is reselected only at session start and when epochs advance. Edit-triggered reloads preserve weather and visual time.

## Weather selection

Scenario creation mode allows cockpit weather selection and locking. The UI sends a weather ID through `weather:set`, with `null` releasing the lock. On changes and `weather:query`, Unity returns current weather and candidate proportions through `weather:state` ([IPC](../programs/ipc.md)). Unity owns candidates and probabilities; the UI performs no selection draw.

## Rendering

A procedural URP skybox, `Resources/Weather/Atmosphere.shader`, and one scene Directional Light render the sky and sun. Time and weather determine direction, color, intensity, and shadow strength. Weather modifies only its assigned sky, light, fog, and reflection probes, leaving city meshes and materials alone and restoring settings when disabled.

- Global `_SunScatterFraction` passes the cloud-diffused sunlight fraction to water rendering ([LOD](E_lod.md)).
- Sky reflections use a realtime reflection probe. Enable Realtime Reflection Probes at every quality level; otherwise water and buildings reflect default blue sky regardless of weather.
- Rain and snow particles appear only near the camera to keep cost independent of city size. `CameraAmbience` plays rain and environmental audio ([sound](../programs/sound.md)).

