# B. Land geometry

This page defines parcel geometry and the meanings of values Unity and Tauri use for rendering and interaction. See [districts and parcels](../economics/04_districts-and-parcels.md) for economic meaning, [path geometry](A_path-geometry.md) for paths, and [building geometry](C_building-geometry.md) for footprints.

## Parcel shapes and coordinates

A parcel is a polygonal area of ground on which buildings stand. The front end uses shapes received from APIs for both previews and confirmed parcels. Roads and tracks do not have parcels.

The boundary `polygon` is a sequence of world-horizontal `[x, z]` vertices, counterclockwise from above and repeating the first point at the end to close the ring. It has no height and may include nearly collinear vertices. Coordinate conventions follow [terrain](D_terrain.md).

Reference elevation `altitude` is absolute. It is not used to ground land-based buildings; follow [building meshes](F_building-meshes.md) for the relationship between buildings and ground.

## Parcel types and waterfronts

Parcel types are land, shore, and water. The front end uses the type returned by the API rather than reclassifying it from the current coastline. Parcel polygons are not necessarily clipped to coastlines.

A shore parcel's `water_depth` is the depth used to indicate candidate port sites. Its quay line is returned as `berth_line` on the building. Unity's buildable-parcel highlights and click checks confirm that the parcel type matches `agent_master.parcel_type` and the water depth meets the required depth in `agent_master.berth_depth`. Final construction eligibility follows the core response.

Water parcels are sites for offshore equipment and have no road access. Unity places their buildings and parcel top surfaces at their reference elevation, sea level zero. Following terrain would sink them to the seabed.

## Frontage edges and road access

`facade_edge_index` identifies which parcel edges face roads. Index i is the edge from vertex i to the next vertex, excluding the repeated closing point. There may be multiple frontage edges or none.

`front_path_id` identifies the street providing road access, with `VALUE_NONE` meaning unset. Frontage edges and road access serve different purposes. Use their respective retrieved values for rendering and interaction rather than selecting them again from nearby roads in the front end.

## Natural-resource references

`resource_id` represents resources associated with the parcel as `{resource type: resource ID}`. Use the retrieved values for these relationships; see [supply and transport](../economics/17_supply-and-transport.md) for their meaning.
