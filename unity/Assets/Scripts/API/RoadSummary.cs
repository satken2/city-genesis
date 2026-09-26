using System;
using System.Collections.Generic;

namespace GeneratedModels
{
    /// <summary>
    /// 道路(名前付き道路)の1件(GET /api/v1/roads/ の各要素)。
    /// 道路は交差点で分かれた複数のパス(区間)をまとめる名札で、所属の正は区間側の road_id。
    /// バックエンドは endpoints.yaml を使わずプレーンJSONを返すため、GeneratedModels.cs の
    /// 自動生成対象ではなく本ファイルで手動定義する(Tauri 側の型は components/road/roadTypes.ts)。
    /// </summary>
    [Serializable]
    public class RoadSummary
    {
        public int road_id;
        /// <summary>空文字は未命名(UIは「道路 #id」を補うが、Unityは多言語リソースを持たないのでラベルを出さない)</summary>
        public string name;
        /// <summary>"#rrggbb"</summary>
        public string color;
        /// <summary>所属区間のパスID(昇順。計画中・工事中も含む)</summary>
        public List<int> path_ids;
        public int segment_count;
        public float length_m;
        /// <summary>[minx, minz, maxx, maxz]。形状が取れない場合は null</summary>
        public float[] bbox;
        public List<RoadLabelPoint> label_points;
    }

    /// <summary>道路名を置く位置。angle_deg はワールドXZ平面での道路の向き(atan2(dz, dx) を度で表したもの)。</summary>
    [Serializable]
    public class RoadLabelPoint
    {
        public float x;
        public float z;
        public float angle_deg;
    }

    /// <summary>
    /// GET /api/v1/roads/ のレスポンス。version は道路の記録・所属・区間の形状や状態から作る署名で、
    /// 前回と同じなら再構築を省略できる(2^48 未満の整数なので long で受ける)。
    /// </summary>
    [Serializable]
    public class RoadListResponse
    {
        public long version;
        public List<RoadSummary> roads;
    }
}
