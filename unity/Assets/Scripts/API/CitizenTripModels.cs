using System;
using System.Collections.Generic;

namespace GeneratedModels
{
    /// <summary>
    /// 市民の追跡の応答(POST /api/v1/citizens/pick と GET /api/v1/citizens/{id}/itinerary)。
    /// コアが保存されない推定値をプレーンJSONで返すため、GeneratedModels.cs の自動生成対象ではなく本ファイルで手動定義する。
    /// </summary>
    [Serializable]
    public class CitizenTripStep
    {
        public int path_id;
        public bool forward;   // パスを始点→終点の向きに進むか
        public int leg;        // legs の添字。建物の接道パスとして経路の端に足した要素は -1
        public int type;       // path_type。車・歩行者として描けないパス(駅の通路など)はカメラだけが進む
    }

    [Serializable]
    public class CitizenTripLeg
    {
        public string kind;               // walk / drive / transit / conveyor / access / turn
        public List<List<float>> points;  // ワールド座標の [x, z] 列
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
        public float per_day;  // 自宅との往復の1日あたりの回数
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
