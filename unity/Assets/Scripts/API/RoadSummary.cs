using System;
using System.Collections.Generic;

namespace GeneratedModels
{
    /// <summary>
    /// One named road, an element of GET /api/v1/roads/.
    /// A road is a label grouping multiple paths (sections) split at intersections; each section's road_id defines membership.
    /// The backend returns plain JSON without endpoints.yaml, so these types are
    /// defined manually here rather than generated in GeneratedModels.cs (Tauri types: components/road/roadTypes.ts).
    /// </summary>
    [Serializable]
    public class RoadSummary
    {
        public int road_id;
        /// <summary>An empty string means unnamed. The UI supplies "Road #id", but Unity has no multilingual resources and displays no label.</summary>
        public string name;
        /// <summary>"#rrggbb"</summary>
        public string color;
        /// <summary>Member section path IDs, sorted ascending, including planned sections and those under construction.</summary>
        public List<int> path_ids;
        public int segment_count;
        public float length_m;
        /// <summary>[minx, minz, maxx, maxz], or null when geometry is unavailable.</summary>
        public float[] bbox;
        public List<RoadLabelPoint> label_points;
    }

    /// <summary>Road-name placement. angle_deg is the road direction on the world XZ plane, atan2(dz, dx) in degrees.</summary>
    [Serializable]
    public class RoadLabelPoint
    {
        public float x;
        public float z;
        public float angle_deg;
    }

    /// <summary>
    /// Response from GET /api/v1/roads/. version is a signature of road records, membership, section geometry, and status;
    /// an unchanged value allows skipping a rebuild. Received as long because it is an integer below 2^48.
    /// </summary>
    [Serializable]
    public class RoadListResponse
    {
        public long version;
        public List<RoadSummary> roads;
    }
}
