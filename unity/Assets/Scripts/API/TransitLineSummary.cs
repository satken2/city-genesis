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
        public int cars_per_train = 1;
        public int train_count;
        public float length_per_vehicle;
        public double round_trip_seconds;
        public List<TransitJourney> journeys;
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

        /// <summary>
        /// 路線カラー("#rrggbb"。プレイヤー設定)。TransitOverlayManager の路線色帯はこれを使い、
        /// 空・解析不能なときだけ種別ごとの自動色へフォールバックする。
        /// </summary>
        public string line_color;

        /// <summary>
        /// 路線の車両外観(プレイヤー設定)。vehicle_model_config.yaml の候補番号。
        /// 候補に無い値(未設定の-1を含む)は路線IDと編成番号による既定の選択へ戻す。
        /// </summary>
        public int vehicle_model = -1;

        /// <summary>
        /// 停留所ごとの停止位置(停留所順)。コアが停留所から直接計算するので、運休中で journeys が空でも返る。
        /// LRT停留所のホーム(LrtStopPlatforms)の位置の正。詳細APIの stops(路線図の表示用)とは別物。
        /// </summary>
        public List<TransitStopPoint> stop_points;
    }

    [Serializable]
    public class TransitStopPoint
    {
        public int node_id;

        /// <summary>
        /// 停留所の下地パス。ノードの記録が無効(交差点分割でIDが変わった等)ならコアがジオメトリ探索で
        /// 解決した現在のパス。解決できなければ null(fraction も null)。
        /// </summary>
        public int? station_path_id;

        /// <summary>
        /// 下地パス上の停止中心(編成の中心)の距離割合。車両の停止位置(journeys の to_fraction)と同じ関数・
        /// 同じクランプで求めた値で、Unity では未切断の3D点列の長さを掛けて距離にする(TransitRouteGeometry と同じ写像)。
        /// </summary>
        public float? fraction;
    }

    /// <summary>GET /api/v1/transit/lines のレスポンス。バックエンドは {"lines": [...]} を返す。</summary>
    [Serializable]
    public class TransitLinesResponse
    {
        public List<TransitLineSummary> lines;
    }

    [Serializable]
    public class TransitJourney
    {
        public int from_stop_id, to_stop_id, transport_path_id;
        public bool returning;
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
