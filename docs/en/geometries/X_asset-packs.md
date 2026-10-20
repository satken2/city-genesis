# Asset packs

An asset pack is a DLC that replaces the appearance of buildings, paths, props, and vehicles, together with materials looked up by name. A city stores the ID of its pack in `asset_pack` (`GET /api/v1/city`); the standard appearance is the shared constant `ASSET_PACK_STANDARD`. The simulation never reads the pack.

## Pack contents

Each DLC is one folder. An asset pack has a display-name `pack.json` (localized text) and, under `assets/windows/`, an AssetBundle named after the pack ID, `index.txt` (the list of logical names), and `model_weights.json` (the model table for buildings and vehicles). Logical names match the standard set: models use `Models/…` (paths relative to `Resources`), and materials looked up by name use `Materials/{name}`. Build bundles from `Assets/Packs/{pack ID}/` with the editor menu `Tools/Asset Packs`, using the same Unity version as the game. Pack sources are not kept in the public repository.

Only packs that are owned and installed are usable; `GET /api/v1/system/asset_packs` returns them. If the city's `asset_pack` is not in that list, the city is drawn with the standard set.

## Lookup rules

As with the standard set, models are loaded when they are first drawn. A unit that the pack contains is searched only in the pack; a unit the pack lacks is searched in the standard set. Units are `Models/{category}/{folder}` (building prefixes, path variants and types, vehicle types, station buildings), `Models/{category}/{name}` (props and similar, without `_close`, `_far`, `_right`, or `_left`), and material names. A unit never mixes pack and standard assets.

- Building prefixes (landmark → variant → building type) are searched in the pack first, and in the standard set only when the pack has none of them.
- A path variant is drawn from the pack when the pack contains its variant folder or its type folder.
- Vehicles try the order vehicle type and appearance → vehicle type → passenger car in the pack first.
- A unit found in the pack never falls back to the standard set, so an empty model in the pack hides things that do not exist in its era (such as airliners).

## Appearance models and weights

Buildings and vehicles can have several numbered models per unit (a building prefix folder or a vehicle type): buildings use `{prefix}/{number}/` folders and vehicles use `{vehicle type}/{number}` models. A unit's model numbers and standard weights come from the table `model_weights.json` (`{unit: {number: weight}}`; the standard one is in `Resources/Config/`) on the side that holds the unit (the pack or the standard set); numbers missing from the table are not used. Rebuild the tables from the existing models with the editor menu `Tools/Asset Packs` (existing weights are kept, and new models get weight 100).

Weights the player changes in the asset manager are stored in `asset_weights.json` in the user folder (the same place as the core's saves) as `{source: {unit: {number: weight}}}`, where the source is `standard` or a pack ID; they take precedence over the standard weights. Unity rereads them whenever it opens a city.

A model is chosen by stacking the weights in number order and taking the one whose range contains a drawn value. The value comes from `visual_variant` for buildings and from the drawing seed for vehicles, so the same value always gives the same model and saved values never change. If the weights add up to 0, the model with the smallest number is used.

## City switches

When the city changes, reload the master data and the asset pack, discard resolved models and materials, and then redraw ([data loading](H_data-loading.md)).
