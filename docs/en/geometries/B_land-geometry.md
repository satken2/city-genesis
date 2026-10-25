# B. Land geometry

This page defines parcel geometry, parcel types, frontage edges, and road access. See [districts and parcels](../economics/04_districts-and-parcels.md) for economic meaning and creation categories, [path geometry](A_path-geometry.md) for paths, and [building geometry](C_building-geometry.md) for footprints.

## B.1. Purpose

A parcel is an arbitrary polygon of ground on which buildings stand. It bounds building footprints and provides the denominator for building coverage. It also supports frontage and natural-resource overlap checks. Roads and tracks have no parcels and are laid where parcels are absent.

## B.2. Geometric properties

The boundary (`polygon`) is a closed `[[x, z], ...]` ring, counterclockwise from above, repeating the first point at the end. Nearly collinear vertices may occur. Reference elevation (`altitude`) is absolute: the access road surface height when there is road access, otherwise terrain height at the parcel center. Water parcels follow the separate rule below. This value is not used to ground land-based buildings ([building meshes](F_building-meshes.md)).

## B.3. Coordinates

Parcel coordinates are world-horizontal `[x, z]` with no height ([terrain](D_terrain.md)). Parcel overlaps, path overlaps, coastline overlaps, and road-access tests all use this plane.

## B.4. Creation

The core creates parcel shapes. See [districts and parcels](../economics/04_districts-and-parcels.md) for creation categories. Identical city state and input produce identical geometry, including irregular subdivision whose randomness is derived from inputs. Preview geometry therefore matches confirmed parcels.

## B.5. Coastlines and parcel types

Creation determines type from polygon position and the current [coastline](D_terrain.md). A boundary touching a sea, lake, or river shore produces a shore parcel. A polygon with almost no land overlap over sea, lake, or river produces a water parcel. Others are land. Maps without coastlines and areas outside the city are treated as land. Polygons are not clipped to coastlines, and existing parcel types do not change when reclamation extends land. Each building variant's `agent_master.parcel_type` determines compatible types, defaulting to land.

Shore parcels carry the quay line where ships berth and the water depth just outside it, `water_depth`. Both are derived values the core computes from the current coastline, reclaimed land, and terrain; the quay line is returned as `berth_line` on the building standing on the parcel. Port variants where ships berth have `agent_master.berth_depth` and stand only on parcels whose `water_depth` reaches it. Unity's buildable-parcel highlight and click check look at both the parcel type and this depth.

Water parcels are sites for offshore equipment. They have no road access because people and goods do not enter by land. Their reference elevation is sea level, zero; Unity places their buildings and parcel top surfaces there. Following terrain would sink them to the seabed. They belong to districts but do not contribute to district outlines.

## B.6. Existing parcel overlap

Parcels do not overlap. New parcels exclude portions occupied by existing ones. If the remainder splits, each part becomes a separate parcel. See [paths and nodes](../economics/03_paths-and-nodes.md) for construction and land acquisition.

## B.7. Frontage edges and road access

`facade_edge_index` identifies edges physically facing a road. Index i is the edge from vertex i to the next vertex, excluding the repeated closing point. Tests use edge-midpoint distance to road centerlines, so perpendicular side edges do not qualify, while corner parcels can have multiple frontage edges.

Road access (`front_path_id`) prefers a street facing a parcel edge. Otherwise it selects a nearby street within a looser distance than the frontage test. If road access exists without a frontage edge, the nearest edge becomes frontage, ensuring at least one. Without nearby streets, frontage is empty and road access is `VALUE_NONE`.

The core recalculates frontage, access, and reference elevation when parcels are created or reshaped or nearby roads change. Only streets supporting roadside parcels qualify, excluding bridges and similar variants. District membership comes from the access road's side facing the parcel ([districts and parcels](../economics/04_districts-and-parcels.md)).

## B.8. Natural resources

`resource_id` stores overlapping resources as `{resource type: resource ID}` and is reassigned when geometry changes ([supply and transport](../economics/17_supply-and-transport.md)).

## B.9. Deletion

Parcels with buildings cannot be deleted, except that a demolished `STATUS_EMPTY` building and its parcel can be deleted together. Deletion is city government expropriation; non-city owners receive the land price as compensation ([budgets](../economics/15_budgets.md)).

