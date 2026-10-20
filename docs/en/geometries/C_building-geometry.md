# C. Building geometry

The core determines a building's horizontal footprint from parcel shape, building coverage, and the master shape category. The front end renders the returned `polygon` rather than reproducing these rules. Placement previews also receive footprints from the API. Floor counts and per-floor uses are separate values; footprint area, floor count, and floor heights determine total floor area and building height. Choosing variants and parcels belongs to the economy ([agents](../economics/02_agents.md)).

## Footprints received by the front end

`polygon` is a counterclockwise sequence of XZ vertices relative to `center_point`, normally contained in the parcel. Edge 0→1 is ordered to be closest to the parcel's frontage edge (`facade_edge_index`; [land geometry](B_land-geometry.md)). It defines the building front for entrances and prebuilt-part orientation ([building meshes](F_building-meshes.md)). Footprints need not have right angles or four vertices: L-shapes, trapezoids, and parcel-shaped polygons are possible.

## Shape categories (`building_shape`)

The master field `building_shape` selects each variant's footprint method. Each aims toward the area limit determined by coverage.

| Value | Footprint |
| --- | --- |
| `SHAPE_FULL` | The parcel itself |
| `SHAPE_OFFICE` | A rectangle aligned with the parcel |
| `SHAPE_HOUSE` | A near-square rectangle set back from the frontage |
| `SHAPE_COMMERCIAL` | A strip-like rectangle along the frontage |
| `SHAPE_SETBACK` | A footprint toward the rear, leaving open space at the front |

