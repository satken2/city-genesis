using System;
using System.Collections.Generic;

namespace GeneratedModels
{
    /// <summary>
    /// バックエンドが自動検出したインターチェンジ(IC/JCT)の1件(GET /api/v1/interchanges/ の各要素)。
    /// バックエンドは endpoints.yaml を使わずプレーンJSONを返すため、GeneratedModels.cs の
    /// 自動生成対象ではなく本ファイルで手動定義する。リストはすべて昇順ソート済み。
    /// </summary>
    [Serializable]
    public class InterchangeSummary
    {
        public int interchange_id;

        /// <summary>プレイヤーが付けた名前。空文字は未命名で、表示側が種別と番号の略号を出す。</summary>
        public string name;

        /// <summary>Constants.INTERCHANGE_KIND_IC(一般道路の腕あり) / INTERCHANGE_KIND_JCT(腕がすべて高速道路)。</summary>
        public int kind;

        /// <summary>ランプ群の凸包(余白付き)の重心 [x, z]。</summary>
        public float[] center;

        /// <summary>ランプ群の凸包(余白付き)の外周 [[x, z], ...]。先頭点の重複はない。</summary>
        public float[][] polygon;

        public List<int> path_ids;
        public List<int> ramp_path_ids;
        public List<int> node_ids;
        public List<InterchangeArm> arms;
    }

    /// <summary>インターチェンジの腕。片端だけが内部ノードに入る道路パスと、その内部側の端点(ポータル)。</summary>
    [Serializable]
    public class InterchangeArm
    {
        public int node_id;
        public int path_id;
    }

    /// <summary>
    /// GET /api/v1/interchanges/ のレスポンス。version はレコード集合・名前・構成が変わるたびに増えるため、
    /// クライアントは前回と同じなら再構築を省略できる。
    /// </summary>
    [Serializable]
    public class InterchangesListResponse
    {
        public int version;
        public List<InterchangeSummary> interchanges;
    }

    /// <summary>GET/PATCH /api/v1/interchanges/{id} のレスポンス。</summary>
    [Serializable]
    public class InterchangeSingleResponse
    {
        public InterchangeSummary interchange;
    }
}
