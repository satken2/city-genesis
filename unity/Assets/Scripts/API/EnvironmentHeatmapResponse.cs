using System;
using System.Collections.Generic;

namespace GeneratedModels
{
    /// <summary>
    /// Environmental heatmap response (GET /api/v1/city/noise, /pollution, /crime, /sunshine).
    /// The backend returns plain JSON without get_schema_object(endpoints.yaml),
    /// so these types are defined manually here rather than generated in GeneratedModels.cs.
    ///
    /// values follows the same order as parcel_ids and contains integer "badness scores"
    /// from 0 to 65535, sampled at parcel centers. noise/pollution/crime are relative values scaled from the current min to max;
    /// sunshine is absolute (fixed min=0, max=1 scale based on the fraction of time in shadow).
    /// Recover either actual score with min + values[i] / 65535 * (max - min).
    /// If no epoch has run and heatmaps have not been calculated, both arrays are empty and min/max are omitted.
    /// </summary>
    [Serializable]
    public class EnvironmentHeatmapResponse
    {
        public List<int> parcel_ids;
        public List<int> values;
        public float min;
        public float max;
    }
}
