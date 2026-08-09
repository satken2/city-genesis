using System;
using System.Collections.Generic;

namespace GeneratedModels
{
    /// <summary>
    /// 環境ヒートマップ(GET /api/v1/city/noise・/pollution・/crime・/sunshine)のレスポンス。
    /// バックエンドは get_schema_object(endpoints.yaml) を使わずプレーンJSONを返すため、
    /// GeneratedModels.cs の自動生成対象ではなく本ファイルで手動定義する。
    ///
    /// values は parcel_ids と同じ並びで、区画中心でサンプリング済みの「悪さスコア」の
    /// 0〜65535 整数。noise/pollution/crime はその時点の min〜max を両端へ引き伸ばした
    /// 相対値、sunshine は絶対値(min=0, max=1 の固定スケール。影時間割合ベース)。
    /// 実スコアはどちらも min + values[i] / 65535 * (max - min) で復元できる。
    /// エポック未実行でヒートマップが未計算のときは両配列が空で min/max も返らない。
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
