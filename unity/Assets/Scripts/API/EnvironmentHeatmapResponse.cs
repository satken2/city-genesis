using System;
using System.Collections.Generic;

namespace GeneratedModels
{
    /// <summary>
    /// 環境ヒートマップ(GET /api/v1/city/noise・/pollution・/crime・/sunshine)のレスポンス。
    /// バックエンドは get_schema_object(endpoints.yaml) を使わずプレーンJSONを返すため、
    /// GeneratedModels.cs の自動生成対象ではなく本ファイルで手動定義する。
    ///
    /// values は parcel_ids と同じ並びで、区画中心でサンプリング済みの「悪さスコア」を
    /// その時点の min〜max で 0〜65535 へ引き伸ばした相対値。絶対水準が要る場合は
    /// min + values[i] / 65535 * (max - min) で復元する。
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
