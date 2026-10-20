using System;
using System.Collections.Generic;

namespace GeneratedModels
{
    /// <summary>
    /// A backend-detected interchange (IC/JCT), one element of GET /api/v1/interchanges/.
    /// The backend returns plain JSON without endpoints.yaml, so these types are
    /// defined manually here rather than generated in GeneratedModels.cs. All lists are sorted ascending.
    /// </summary>
    [Serializable]
    public class InterchangeSummary
    {
        public int interchange_id;

        /// <summary>Player-assigned name. An empty string means unnamed; the display supplies a type-and-number abbreviation.</summary>
        public string name;

        /// <summary>Constants.INTERCHANGE_KIND_IC (has ordinary-road arms) / INTERCHANGE_KIND_JCT (all arms are motorways).</summary>
        public int kind;

        /// <summary>Centroid [x, z] of the ramp group's convex hull, including padding.</summary>
        public float[] center;

        /// <summary>Boundary [[x, z], ...] of the ramp group's convex hull, including padding. The first point is not repeated.</summary>
        public float[][] polygon;

        public List<int> path_ids;
        public List<int> ramp_path_ids;
        public List<int> node_ids;
        public List<InterchangeArm> arms;
    }

    /// <summary>Interchange arms: road paths with exactly one end at an internal node, and their internal endpoints (portals).</summary>
    [Serializable]
    public class InterchangeArm
    {
        public int node_id;
        public int path_id;
    }

    /// <summary>
    /// Response from GET /api/v1/interchanges/. version increments whenever the record set, names, or structure change,
    /// so clients can skip rebuilding when it matches the previous value.
    /// </summary>
    [Serializable]
    public class InterchangesListResponse
    {
        public int version;
        public List<InterchangeSummary> interchanges;
    }

    /// <summary>Response from GET/PATCH /api/v1/interchanges/{id}.</summary>
    [Serializable]
    public class InterchangeSingleResponse
    {
        public InterchangeSummary interchange;
    }
}
