using System;
using System.Collections.Generic;

namespace GeneratedModels
{
    /// <summary>
    /// マッシュアップ建物（MODEL_MASHUP）のサブ建物1棟分の配置情報。
    /// 8m×8mセルへの分割と棟の割り付けはバックエンドが解決済みで、
    /// Unity側には配置結果の座標だけが届く（セル分割ロジックは持たない）。
    /// </summary>
    [Serializable]
    public class SubBuildingPlacement
    {
        public int subbuilding_id;
        public string model_prefix;     // パーツ名 mashup-{model_prefix}-{base|mid|top}-prebuilt の解決に使う
        public float[] position;        // center_point基準の相対XZ座標 [x, z]。専有セル矩形の中心
        public float rotation;          // XZ平面上の角度（度）。prebuiltの前面辺（頂点0→1）と同じ規約
        public int floors;              // 積み上げ階数（1以上）
        public float? height_per_floor; // サブ建物固有の階高（m）。nullなら建物本体の階高を使う
    }

    /// <summary>
    /// single_agentスキーマにpolygon, center_point, visual_variant等を追加した拡張版。
    /// 建物一覧API（GET /buildings/）はsingle_agentスキーマを使用するが、
    /// メッシュ生成や配置座標の決定にpolygon, center_point, visual_variantが必要なため
    /// これらをAgentDataに統合して扱う。
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
        /// マッシュアップ建物（MODEL_MASHUP）のサブ建物配置リスト。
        /// multi_agent スキーマ解決後にバックエンドが手動注入するプロパティで、
        /// MODEL_MASHUP 以外の建物や未登録の建物では null。
        /// </summary>
        public List<SubBuildingPlacement> sub_buildings;

        /// <summary>
        /// 建物が持つスペース種別のビットマスク。
        /// ビット位置がスペース種別IDと一致する（bit0=OTHER, bit1=RESIDENCE, ...）。
        /// multi_agent スキーマの method: other で算出された値が設定される。
        /// </summary>
        public int space_types;

        private float[][] _polygonArrayCache;

        /// <summary>
        /// ポリゴン座標を float[][] 形式で取得する（遅延キャッシュ付き）。
        /// メッシュ生成などで配列アクセスが必要な箇所で使用する。
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

        private float[] _centerPointArrayCache;

        /// <summary>
        /// 中心座標を float[] 形式で取得する（遅延キャッシュ付き）。
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
