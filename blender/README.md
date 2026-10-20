English | [Japanese (original)](README.ja.md)

# blender/ — 3D model sources

Original Blender libraries for City Genesis buildings, roads, props, vehicles, and station buildings. FBX files under `unity/Assets/Models/` are generated exports; edit the libraries rather than FBX directly.

| File | Contents | Export destination |
| --- | --- | --- |
| `buildings.blend` | Walls, corners, prebuilt buildings, open-space decorations, and other building parts | `unity/Assets/Models/Buildings/` |
| `paths.blend` | Road, track, ropeway, and roadside equipment parts | `unity/Assets/Models/Paths/` |
| `props.blend` | Terrain props, pedestrians, construction vehicles, and ambient vehicles | `unity/Assets/Models/Props/` |
| `vehicles.blend` | Cars and trains for traffic rendering | `unity/Assets/Models/Vehicles/` |
| `stations.blend` | Station buildings | `unity/Assets/Models/Stations/` |

- One file per category. Collection names are full FBX paths, such as `a200/0/base-w1`. Material instances are shared within each file.
- Blender 5.2 format; Blender 4.x cannot open these files.
- Textures use relative references to `unity/Assets/Models/*/Textures/`.
- The separate `genesis-gallery` pipeline handles FBX export, convention validation, and the viewing gallery.
- Do not commit Blender automatic backups (`*.blend1`).
