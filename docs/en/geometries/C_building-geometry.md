# C. Building geometry

Building rendering uses the `polygon` returned by the core as the horizontal footprint. Placement previews also receive footprints from the API. The front end does not regenerate the footprint; it combines it with rendering inputs such as floor counts and floor heights. See [agents](../economics/02_agents.md) for the economic meaning of buildings.

## Footprints received by the front end

`polygon` is a counterclockwise sequence of XZ vertices relative to `center_point`, normally contained in the parcel. Edge 0→1 defines the building front for entrances and prebuilt-part orientation ([building meshes](F_building-meshes.md)). Footprints need not have right angles or four vertices: L-shapes, trapezoids, and parcel-shaped polygons are possible. See [land geometry](B_land-geometry.md) for parcel shape data.

## Shape categories (`building_shape`)

Master `building_shape` describes the footprint category, separately from `model_type`, which describes mesh assembly. `SHAPE_FULL` uses the entire parcel as its footprint and also affects Unity rendering ([building mesh methods](F1_building-mesh-methods.md)). Do not infer a specific footprint from the category; use the returned `polygon` for every category.
