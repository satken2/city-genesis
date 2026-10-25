using System;
using System.Collections.Generic;

namespace GeneratedModels
{
    /// <summary>
    /// Placement data for one sub-building in a mashup building (MODEL_MASHUP).
    /// The backend has already divided the site into 8m x 8m cells and assigned buildings;
    /// Unity receives only the resulting placement coordinates and has no cell-partitioning logic.
    /// </summary>
    [Serializable]
    public class SubBuildingPlacement
    {
        public int subbuilding_id;
        public string model_prefix;     // Used to resolve part names of the form mashup-{model_prefix}-{base|mid|top}-prebuilt.
        public float[] position;        // XZ coordinates [x, z] relative to center_point; center of the occupied cell rectangle.
        public float rotation;          // Angle in degrees on the XZ plane, following the prebuilt front-edge convention (vertex 0 -> 1).
        public int floors;              // Number of stacked floors (at least 1).
        public float? height_per_floor; // Floor height for this sub-building in meters; null uses the main building's floor height.
    }

    /// <summary>
    /// One element of GET /buildings/ and the response from GET /buildings/{id}.
    /// Both use the backend's multi_agent schema plus sub-building placements. This adds fields absent from AgentData(single_agent),
    /// such as polygon, center_point, and visual_variant, used for mesh generation and placement coordinates.
    /// </summary>
    [Serializable]
    public class BuildingSummary : AgentData
    {
        public List<object> polygon;
        public List<object> center_point;
        public float visual_variant;
        public float visual_variant2;
        public float visual_variant3;

        /// <summary>
        /// Sub-building placements for a mashup building (MODEL_MASHUP).
        /// The backend manually injects this property after resolving the multi_agent schema;
        /// null for buildings other than MODEL_MASHUP or unregistered buildings.
        /// </summary>
        public List<SubBuildingPlacement> sub_buildings;

        /// <summary>
        /// Bitmask of the building's space types.
        /// Bit positions match space type IDs (bit0=OTHER, bit1=RESIDENCE, ...).
        /// Set to the value calculated by method: other in the multi_agent schema.
        /// </summary>
        public int space_types;

        /// <summary>
        /// Natural-coast quay line of the building's parcel, world XZ [A'x, A'z, B'x, B'z] with the sea on the left of A'→B'
        /// (multi_agent schema, method: other). The backend decides it and judges port water depth along it;
        /// PortBuildingMeshBuilder cuts the quay slab on it. Null off the coast or when the parcel faces reclaimed quays.
        /// </summary>
        public List<object> berth_line;

        private float[][] _polygonArrayCache;

        /// <summary>
        /// Get polygon coordinates as float[][], with lazy caching.
        /// Used where array access is required, such as mesh generation.
        /// </summary>
        public float[][] polygonArray
        {
            get
            {
                if (_polygonArrayCache == null && polygon != null)
                    _polygonArrayCache = GeneratedModelUtils.ToFloatJaggedArray(polygon);
                return _polygonArrayCache;
            }
        }

        private float[] _berthLineArrayCache;

        /// <summary>Get berth_line as float[] (lazily cached); null when absent.</summary>
        public float[] berthLineArray
        {
            get
            {
                if (_berthLineArrayCache == null && berth_line != null)
                    _berthLineArrayCache = GeneratedModelUtils.ToFloatArray(berth_line);
                return _berthLineArrayCache;
            }
        }

        private float[] _centerPointArrayCache;

        /// <summary>
        /// Get center coordinates as float[], with lazy caching.
        /// </summary>
        public float[] centerPointArray
        {
            get
            {
                if (_centerPointArrayCache == null && center_point != null)
                    _centerPointArrayCache = GeneratedModelUtils.ToFloatArray(center_point);
                return _centerPointArrayCache;
            }
        }
    }
}
