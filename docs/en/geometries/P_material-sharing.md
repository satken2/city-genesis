# Material sharing and export

These conventions define material names and sharing across imported models. See [material resolution](R_material-resolution.md) for which materials generators select.

## Material names

| Name | Purpose |
| --- | --- |
| `BD_*` / `PATH_*` / `PROP_*` | Building, path, and prop materials; materials may be reused across categories |
| `MAT_{material}_{color}` | New shared PBR materials |
| `SLOT_{role}` | Materials whose values change at runtime, such as line colors |

Use identical names for identical materials. Runtime-colored materials need role-specific names, since sharing a name with another use can recolor that use too.

## Sharing identity

Identically named but separate Unity Material instances are not shared and cannot benefit from batching faces using the same material. Models must reference the same `.mat` asset, assigned through FBX import remapping, to share one instance. Path generators uniquely pool by name, so matching path-part names share instances. Runtime glTF files create separate Material and Texture instances even for matching names in different files.

## FBX and wrapper prefabs

Original libraries live in `blender/`, exported FBX in `Assets/Models/`, and runtime wrapper prefabs at matching relative paths under `Assets/Resources/Models/`. Editor extension `ModelPrefabGenerator` keeps wrappers aligned with FBX. Runtime lookup uses resource paths, preferring glTF at the same relative path under `persistentDataPath` when present.

Generators read mesh vertices to bake placement, so meshes in `Assets/Models` require Read/Write Enabled, which the editor extension enables on import. `_close` and far models use the same import path.

