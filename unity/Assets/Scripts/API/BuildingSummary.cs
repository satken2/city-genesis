using System;
using System.Collections.Generic;

namespace GeneratedModels
{
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
