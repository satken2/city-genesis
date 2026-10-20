using System;
using System.Collections.Generic;

namespace GeneratedModels
{
    /// <summary>
    /// Public transport line summary, one element of GET /api/v1/transit/lines.
    /// The backend returns plain JSON without get_schema_object(endpoints.yaml),
    /// so these types are defined manually here rather than generated in GeneratedModels.cs.
    /// Includes aggregates such as financial balance, utilization, and vehicle count, plus
    /// route_path_ids (underlying path IDs computed each epoch by TransitRoutePhase) used for route rendering.
    /// </summary>
    [Serializable]
    public class TransitLineSummary
    {
        public int line_agent_id;
        public string name;
        // Type key = transit_path_id in transit_master (= path_variant of the PATH_TRANSPORT section path).
        public int path_variant;
        public int status;
        public float fare_price;
        public float target_headway;
        public int vehicle_count;
        public int cars_per_train = 1;
        public int train_count;
        public float length_per_vehicle;
        public double round_trip_seconds;
        public List<TransitJourney> journeys;
        public float required_vehicle_count;
        public float revenues;
        public float expenses;
        public float ridership;
        public float max_utilization;
        public int path_count;

        /// <summary>
        /// Underlying road/track path IDs traversed in one circuit. Line color bands and vehicle rendering use journeys instead.
        /// </summary>
        public List<int> route_path_ids;

        /// <summary>Suspension reason code (None / "route_disconnected" / "no_underlay" / "train_too_long").</summary>
        public string pause_reason;

        /// <summary>
        /// Player-selected line color ("#rrggbb"). TransitOverlayManager uses it for line color bands,
        /// falling back to the automatic type-specific color only if it is empty or invalid.
        /// </summary>
        public string line_color;

        /// <summary>
        /// Player-selected vehicle appearance for the line: an index in the vehicle type's appearance options (ModelWeights table).
        /// Values absent from the options, including unset -1, fall back to the default selection based on line ID and train index.
        /// </summary>
        public int vehicle_model = -1;

        /// <summary>
        /// Stop positions, once per stop in circuit order. Computed directly from stops by the core, so returned even when suspended with empty journeys.
        /// Authoritative platform positions for LRT stops (LrtStopPlatforms), distinct from the detail API's stops used in the line diagram.
        /// </summary>
        public List<TransitStopPoint> stop_points;
    }

    [Serializable]
    public class TransitStopPoint
    {
        public int node_id;

        /// <summary>
        /// Underlying path for the stop. If its node record is invalid (e.g. IDs changed after splitting an intersection),
        /// the core resolves the current path through a geometry search. null if unresolved (fraction is also null).
        /// </summary>
        public int? station_path_id;

        /// <summary>
        /// Distance fraction of the stopping center (train center) along the underlying path, computed with the same function and clamp
        /// as vehicle stopping positions (journeys to_fraction). Unity multiplies it by the uncut 3D polyline length, as in TransitRouteGeometry.
        /// </summary>
        public float? fraction;

        /// <summary>Daily boardings from this stop in the latest epoch, or null if unrecorded. Used to render station and stop passengers.</summary>
        public float? ridership;
    }

    /// <summary>Response from GET /api/v1/transit/lines. The backend returns {"lines": [...]}.</summary>
    [Serializable]
    public class TransitLinesResponse
    {
        public List<TransitLineSummary> lines;
    }

    /// <summary>
    /// One section from a stop to the next. A line follows sections in circuit order, returning to the first after the last.
    /// turnback indicates reversal at the departure stop; reversed indicates whether a guided train faces opposite to the first section
    /// (toggled on each reversal; always false for buses, which turn their bodies to travel).
    /// </summary>
    [Serializable]
    public class TransitJourney
    {
        public int from_stop_id, to_stop_id, transport_path_id;
        public bool turnback, reversed;
        public List<TransitMotionStep> steps;
    }

    [Serializable]
    public class TransitMotionStep
    {
        public int path_id;
        public float from_fraction, to_fraction, length_m;
        public float start_speed_mps, end_speed_mps;
        public double duration_seconds, dwell_seconds, free_duration_seconds, congestion_delay_seconds;
    }
}
