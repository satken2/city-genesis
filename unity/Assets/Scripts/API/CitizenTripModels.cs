using System;
using System.Collections.Generic;

namespace GeneratedModels
{
    /// <summary>
    /// Citizen tracking responses (POST /api/v1/citizens/pick and GET /api/v1/citizens/{id}/itinerary).
    /// The core returns non-persisted estimates as plain JSON, so these are defined manually here rather than generated in GeneratedModels.cs.
    /// </summary>
    [Serializable]
    public class CitizenTripStep
    {
        public int path_id;
        public bool forward;   // Whether to traverse the path from start to end.
        public int leg;        // Index into legs; -1 for elements added at route ends as building road-access paths.
        public int type;       // path_type; only the camera advances on paths that cannot render cars or pedestrians, such as station passages.
    }

    [Serializable]
    public class CitizenTripLeg
    {
        public string kind;               // walk / drive / transit / conveyor / access / turn
        public List<List<float>> points;  // Sequence of world-space [x, z] coordinates.
        public List<int> path_ids;
        public float minutes;
        public float distance_m;
    }

    [Serializable]
    public class CitizenTrip
    {
        public string status;
        public int from_visit;
        public int to_visit;
        public float minutes;
        public float distance_m;
        public List<CitizenTripLeg> legs;
        public List<CitizenTripStep> steps;
    }

    [Serializable]
    public class CitizenVisit
    {
        public string kind;    // home / work / school / shop
        public int agent_id;
        public string name;
        public int parcel_id;
        public float per_day;  // Number of daily round trips from home.
        public float? x;
        public float? z;
    }

    [Serializable]
    public class CitizenItinerary
    {
        public string status;
        public int citizen_id;
        public string snapshot;
        public string mode;    // automobile / pedestrian
        public List<CitizenVisit> visits;
        public List<CitizenTrip> trips;
    }

    [Serializable]
    public class CitizenPickResult
    {
        public string status;
        public int citizen_id;
        public string snapshot;
        public int from_agent_id;
        public int to_agent_id;
        public int? step_index;
        public CitizenTrip trip;
    }
}
