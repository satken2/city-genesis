using System;
using System.Collections.Generic;

namespace GeneratedModels
{
    /// <summary>
    /// 公共交通路線のサマリ(GET /api/v1/transit/lines の各要素)。
    /// バックエンドは get_schema_object(endpoints.yaml) を使わずプレーンJSONを返すため、
    /// GeneratedModels.cs の自動生成対象ではなく本ファイルで手動定義する。
    /// 収支・稼働率・車両数などの集計値と、グラフィック側が実走行経路描画に使う
    /// route_path_ids(TransitRoutePhase が毎エポック算出する下地パスID列)を含む。
    /// </summary>
    [Serializable]
    public class TransitLineSummary
    {
        public int line_agent_id;
        public string name;
        // 種別のキー=transit_master の transit_path_id(=区間パス PATH_TRANSPORT の path_variant)。
        public int path_variant;
        public int status;
        public float fare_price;
        public float target_headway;
        public int vehicle_count;
        public float required_vehicle_count;
        public float revenues;
        public float expenses;
        public float ridership;
        public float avg_utilization;
        public int path_count;

        /// <summary>
        /// 車両が走行する下地の道路/線路パスID列(往路+復路)。
        /// TransitOverlayManager が各パスの実ジオメトリを連結して路線色帯を描くために使う。
        /// </summary>
        public List<int> route_path_ids;

        /// <summary>運休理由コード(None / "route_disconnected" / "return_leg_blocked" / "no_underlay")。</summary>
        public string pause_reason;
    }

    /// <summary>GET /api/v1/transit/lines のレスポンス。バックエンドは {"lines": [...]} を返す。</summary>
    [Serializable]
    public class TransitLinesResponse
    {
        public List<TransitLineSummary> lines;
    }
}
