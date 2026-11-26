# Weather and lighting

Unity uses city climate settings to render sky, clouds, sunlight, shadows, haze, sky reflections, precipitation, and time-of-day sun motion. Rendered weather and time belong only to Unity and do not affect simulation or saves. `AppRoot` creates the entry point, `WeatherController`.

## Responsibilities

Core master `/api/v1/master/weather` defines weather IDs. City `climate` from `GET /api/v1/city/` supplies occurrence proportions by weather ID for each of twelve months. Unity stores appearances in `WeatherPreset` and selects weather from the current city month's proportions. Tauri owns cockpit labels and emoji. Weather-ID changes must align all three. Finalized epoch climate affecting production is distinct from rendered weather ([production](../economics/10_production.md)).

Normal weather slowly moves between neighboring states independently of epochs, from increasing cloud through precipitation and clearing. Long-run time shares match monthly proportions.

Visual time doubles as the countdown to the next epoch: one cycle runs from morning to the evening glow. Unity owns the clock; the UI passes only the cycle length and whether the player has stopped time, and displays the remaining time that Unity reports. When a cycle reaches the evening outside editing, Unity sends `epoch:due` and the UI starts the epoch through the same steps as its start button. The sun stops while time is stopped and while visual time is frozen for capture. Pausing traffic animation does not stop time.

Normal rendering continues in the evening glow during epoch computation (an epoch started mid-cycle first moves the sun to the evening). Sunset, blackout, and sunrise mark the period from computation completion until the player finishes reviewing the results, and the next cycle begins with the morning after the results close. This sequence proceeds even when time or traffic is stopped, and weather is reselected using the updated climate and date before the new morning becomes visible. Manually locked weather is preserved. Rendered weather and time are not saved: at session start, Unity also selects weather from that city's climate and date and begins in the morning. City reloads alone refresh proportions and season without starting the sequence.

Saved climate contains monthly weather proportions, average temperatures, precipitation, and sunshine. Finalized epoch climate is a core result; editing climate settings does not rewrite history. Unity uses monthly rendering proportions without recalculating production multipliers.

## Weather selection

Scenario creation mode allows cockpit weather selection and locking. The UI sends a weather ID through `weather:set`, with `null` releasing the lock. On changes and `weather:query`, Unity returns current weather and candidate proportions through `weather:state` ([IPC](../programs/ipc.md)). Unity owns candidates and probabilities; the UI performs no selection draw.

## Rendering

A procedural URP skybox, `Resources/Weather/Atmosphere.shader`, and one scene Directional Light render the sky and sun. Time and weather determine direction, color, intensity, and shadow strength. Weather modifies only its assigned sky, light, fog, and reflection probes, leaving city meshes and materials alone and restoring settings when disabled.

- Global `_SunScatterFraction` passes the cloud-diffused sunlight fraction to water rendering ([LOD](E_lod.md)).
- The darkness from the time of day (0 by day, 1 at night) is passed as the level of the night lights of windows and road surfaces ([material resolution](R_material-resolution.md)). Lights come on as it grows dark after sunset and are out by sunrise. Exposure is fixed, so even a faint glow while the sun is up washes dark glass out to white.
- Sky reflections use a realtime reflection probe. Enable Realtime Reflection Probes at every quality level; otherwise water and buildings reflect default blue sky regardless of weather.
- Rain and snow particles appear only near the camera to keep cost independent of city size. `CameraAmbience` plays rain and environmental audio ([sound](../programs/sound.md)).

