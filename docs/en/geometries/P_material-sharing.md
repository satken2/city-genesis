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

Identically named but separate Unity Material instances are not shared and cannot benefit from batching faces using the same material. FBX and glTF files each carry their own copies of materials, so building and path generators pool instances by name regardless of where a model comes from (see [material resolution](R_material-resolution.md)). The first one read is used, so a material name must have the same values in every model file. Buildings pool by name and shader together, so a model whose import assigns a `.mat` with a different shader gets an instance separate from same-named copies. Only these two generators pool; to share one instance among other models such as props and vehicles, assign the same `.mat` asset through FBX import remapping.

## FBX and wrapper prefabs

Original libraries live in `blender/`, exported FBX in `Assets/Models/`, and runtime wrapper prefabs at matching relative paths under `Assets/Resources/Models/`. Editor extension `ModelPrefabGenerator` keeps wrappers aligned with FBX. Runtime lookup uses resource paths, preferring glTF at the same relative path under `persistentDataPath` when present.

Generators read mesh vertices to bake placement, so meshes in `Assets/Models` require Read/Write Enabled, which the editor extension enables on import. `_close` and far models use the same import path.

