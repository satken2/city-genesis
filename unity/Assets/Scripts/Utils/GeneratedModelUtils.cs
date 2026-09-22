using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// GeneratedModelsの座標データ(List&lt;object&gt;)をフロントエンドで使いやすい型に変換するユーティリティ。
/// バックエンドのAPIレスポンスでは座標データが List&lt;object&gt; 型として送信されるため、
/// ここで float[] や float[][] に変換してから利用する。
/// Newtonsoft.Json で List&lt;object&gt; に落とすと、ネスト配列や数値が JArray / JValue のまま残ることがあるため、
/// それらもここで扱う。
/// </summary>
public static class GeneratedModelUtils
{
    /// <summary>
    /// GeoJSON 形式のリング [[x, z], [x, z], ...] を Vector2(x=X, y=Z) の点列に変換する。
    /// 配列でない・null のときは空リストを返す(点数の妥当性は呼び出し側が判定する)。
    /// 水面ポリゴンや効果範囲ポリゴンなど、生の JSON で届く多角形の共通入口。
    /// </summary>
    public static List<Vector2> ParseRing(JToken ring)
    {
        if (!(ring is JArray arr)) return new List<Vector2>();
        var pts = new List<Vector2>(arr.Count);
        foreach (JToken p in arr)
        {
            if (p is JArray pa && pa.Count >= 2) pts.Add(new Vector2((float)pa[0], (float)pa[1]));
        }
        return pts;
    }

    /// <summary>
    /// List&lt;object&gt; を float[] に変換する。
    /// 座標1点分 [x, y] や [x, y, z] などの1次元配列に使用する。
    /// </summary>
    public static float[] ToFloatArray(List<object> list)
    {
        if (list == null) return null;
        float[] result = new float[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            result[i] = ToFloat(list[i]);
        }
        return result;
    }

    /// <summary>
    /// Json.NET がネスト配列に対して返す JArray を float[] に変換する。
    /// center_point など、要素が数値トークンだけの1次元配列向け。
    /// </summary>
    public static float[] ToFloatArray(JArray arr)
    {
        if (arr == null) return null;
        float[] result = new float[arr.Count];
        for (int i = 0; i < arr.Count; i++)
        {
            result[i] = ToFloat(arr[i]);
        }
        return result;
    }

    /// <summary>
    /// List&lt;object&gt; を List&lt;float&gt; に変換する。
    /// start_point, end_point など List&lt;float&gt; を期待する箇所に使用する。
    /// </summary>
    public static List<float> ToFloatList(List<object> list)
    {
        if (list == null) return null;
        List<float> result = new List<float>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            result.Add(ToFloat(list[i]));
        }
        return result;
    }

    /// <summary>
    /// ポリゴンデータ(List&lt;object&gt; の各要素が List&lt;object&gt;)を float[][] に変換する。
    /// polygon = [[x1,y1], [x2,y2], ...] のような2次元配列に使用する。
    /// </summary>
    public static float[][] ToFloatJaggedArray(List<object> list)
    {
        if (list == null) return null;
        float[][] result = new float[list.Count][];
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is JArray jArr)
            {
                result[i] = ToFloatArray(jArr);
            }
            else if (list[i] is List<object> inner)
            {
                result[i] = ToFloatArray(inner);
            }
            else if (list[i] is IEnumerable<object> enumerable)
            {
                var tempList = new List<float>();
                foreach (var item in enumerable)
                {
                    tempList.Add(ToFloat(item));
                }
                result[i] = tempList.ToArray();
            }
            else
            {
                result[i] = new float[0];
            }
        }
        return result;
    }

    /// <summary>
    /// ウェイポイントリストを PathWaypoint のリストに変換する。
    /// バックエンドは coordinate_utils.normalize_waypoints で正規化した制御点付き辞書
    ///   [{"position":[x,y,z], "control_in":[x,y,z], "control_out":[x,y,z]}, ...]
    /// だけを返す。要素の実体は JSON 経由なら JObject、msgpack 経由なら
    /// Dictionary&lt;string, object&gt; になるため、その2形だけを受ける。
    /// </summary>
    public static List<PathWaypoint> ToWaypointList(List<object> list)
    {
        if (list == null) return null;
        List<PathWaypoint> result = new List<PathWaypoint>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is JObject wpObj)
            {
                var wp = new PathWaypoint();

                JToken posToken = wpObj["position"];
                if (posToken is JArray posArr)
                    wp.position = ToFloatArray(posArr);
                else if (posToken != null)
                    wp.position = ToFloatArray(posToken.ToObject<List<object>>());

                JToken ciToken = wpObj["control_in"];
                if (ciToken is JArray ciArr && ciArr.Count >= 2)
                    wp.control_in = ToFloatArray(ciArr);

                JToken coToken = wpObj["control_out"];
                if (coToken is JArray coArr && coArr.Count >= 2)
                    wp.control_out = ToFloatArray(coArr);

                result.Add(wp);
            }
            else if (list[i] is Dictionary<string, object> dict)
            {
                // JsonConvert.DeserializeObject で Dictionary<string, object> になった場合
                var wp = new PathWaypoint();

                if (dict.TryGetValue("position", out object posObj))
                {
                    wp.position = ToFloatArrayFromObject(posObj);
                }
                if (dict.TryGetValue("control_in", out object ciObj) && ciObj != null)
                {
                    wp.control_in = ToFloatArrayFromObject(ciObj);
                }
                if (dict.TryGetValue("control_out", out object coObj) && coObj != null)
                {
                    wp.control_out = ToFloatArrayFromObject(coObj);
                }

                result.Add(wp);
            }
        }
        return result;
    }

    /// <summary>
    /// object を float[] に変換する。
    /// List&lt;object&gt;, JArray, その他の IEnumerable を受け取る。
    /// </summary>
    private static float[] ToFloatArrayFromObject(object obj)
    {
        if (obj == null) return null;
        if (obj is List<object> list) return ToFloatArray(list);
        if (obj is JArray jarr) return ToFloatArray(jarr);
        if (obj is IEnumerable<object> enumerable)
        {
            List<float> result = new List<float>();
            foreach (var item in enumerable)
            {
                result.Add(ToFloat(item));
            }
            return result.ToArray();
        }
        return null;
    }

    /// <summary>
    /// object を float に変換する。JSONデシリアライズ後の数値は
    /// long, double, int, float などの型になり得るため、全パターンに対応する。
    /// さらに Json.NET の JValue / JToken も、内部の数値として解釈する。
    /// </summary>
    public static float ToFloat(object obj)
    {
        if (obj == null) return 0f;
        if (obj is JToken jt && jt.Type != JTokenType.Null && jt.Type != JTokenType.Undefined)
        {
            switch (jt.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return jt.Value<float>();
                case JTokenType.String:
                    if (float.TryParse(jt.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out float jParsed))
                        return jParsed;
                    break;
            }
        }
        if (obj is long l) return (float)l;
        if (obj is double d) return (float)d;
        if (obj is float f) return f;
        if (obj is int i) return (float)i;
        if (obj is string s && float.TryParse(s, out float parsed)) return parsed;
        Debug.LogWarning($"GeneratedModelUtils: floatへの変換に失敗: {obj} ({obj.GetType().Name})");
        return 0f;
    }

    /// <summary>
    /// allowed_bd_mask のビットフラグから、有効な建物タイプIDのリストを抽出する。
    /// ゾーンの許可建物タイプがビットマスクで管理されているため、
    /// 各ビットが立っている位置のインデックスをリストとして返す。
    /// </summary>
    /// <summary>
    /// allowed_bd_mask のビットフラグから、有効な建物タイプIDのリストを抽出する。
    /// ゾーンの許可建物タイプがビットマスクで管理されているため、
    /// 各ビットが立っている位置のインデックスをリストとして返す。
    /// 建物タイプIDの有効範囲は 1 ～ 512（ビット0～9）までであるため、
    /// それ以降のビットは無視して不要なアイコン参照を防ぐ。
    /// </summary>
    public static List<int> ExtractBuildingTypes(int mask)
    {
        List<int> types = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                types.Add(1 << i);
            }
        }
        return types;
    }
}

/// <summary>
/// GeneratedModels.PathSummary の拡張メソッド。
/// 自動生成ファイルに変更を加えずに、座標データの変換機能を追加する。
/// </summary>
public static class PathSummaryExtensions
{
    /// <summary>
    /// start_point を List&lt;float&gt; に変換する。
    /// </summary>
    public static List<float> GetStartPointList(this GeneratedModels.PathSummary p)
    {
        return GeneratedModelUtils.ToFloatList(p.start_point);
    }

    /// <summary>
    /// end_point を List&lt;float&gt; に変換する。
    /// </summary>
    public static List<float> GetEndPointList(this GeneratedModels.PathSummary p)
    {
        return GeneratedModelUtils.ToFloatList(p.end_point);
    }

    /// <summary>
    /// waypoints を PathWaypoint のリストに変換する。
    /// </summary>
    public static List<PathWaypoint> GetWaypointList(this GeneratedModels.PathSummary p)
    {
        return GeneratedModelUtils.ToWaypointList(p.waypoints);
    }

    /// <summary>
    /// パス中心線上の指定XZ位置における相対高さ(地形からの高さ)を返す。
    /// アンカー列(始点→ウェイポイント→終点)の相対Yを、XZ最近傍のアンカー区間上で線形補間する。
    /// 保存設計標高がある場合はその基準線を優先し、Utilsの地形標高との差を返す。
    /// 吸着でパスの標高を引き継ぐ際は必ずこのメソッドを使うこと。
    /// 「描画ポリラインの絶対Y − 地形サンプル」の逆算は、折れ線チャード中間の地形起伏・
    /// 描画用オフセット・高さ源(CPU配列/Terrain.SampleHeight)の不一致がすべて標高ノイズとして
    /// 混入し、地面のパス(相対Y=0)へ吸着しただけで端点が地下/空中扱いになるため禁止。
    /// base_height を持つバリアント(送電線など)でも、描画Yではなくバックエンドの相対Yを返す。
    /// </summary>
    public static float GetRelativeHeightAtPoint(this GeneratedModels.PathSummary p, float x, float z)
    {
        if (p.station_grade_profile != null && p.station_grade_profile.Count >= 2)
        {
            var profile = Newtonsoft.Json.Linq.JArray.FromObject(p.station_grade_profile).ToObject<List<List<float>>>();
            return PathManager.EvaluateStationGrade(profile, new Vector3(x, 0f, z)) - Utils.GetGroundHeight(new Vector3(x, 0f, z));
        }

        var anchorX = new List<float>();
        var anchorZ = new List<float>();
        var anchorRel = new List<float>();

        void AddAnchor(List<float> pt)
        {
            if (pt == null || pt.Count < 2) return;
            anchorX.Add(pt[0]);
            anchorZ.Add(pt[pt.Count - 1]);
            anchorRel.Add(pt.Count >= 3 ? pt[1] : 0f);
        }

        AddAnchor(p.GetStartPointList());
        var waypoints = p.GetWaypointList();
        if (waypoints != null)
        {
            foreach (var wp in waypoints)
            {
                if (wp?.position == null || wp.position.Length < 2) continue;
                anchorX.Add(wp.position[0]);
                anchorZ.Add(wp.position[wp.position.Length - 1]);
                anchorRel.Add(wp.GetRelativeHeight());
            }
        }
        AddAnchor(p.GetEndPointList());

        if (anchorRel.Count == 0) return 0f;
        if (anchorRel.Count == 1) return anchorRel[0];

        var pXZ = new Vector2(x, z);
        float bestDistSqr = float.MaxValue;
        float bestRel = anchorRel[0];
        for (int i = 0; i < anchorRel.Count - 1; i++)
        {
            var a = new Vector2(anchorX[i], anchorZ[i]);
            var b = new Vector2(anchorX[i + 1], anchorZ[i + 1]);
            float t = PolylineUtils.ProjectOnSegment(pXZ, a, b);
            Vector2 closest = Vector2.Lerp(a, b, t);
            float distSqr = (pXZ - closest).sqrMagnitude;
            if (distSqr < bestDistSqr)
            {
                bestDistSqr = distSqr;
                bestRel = Mathf.Lerp(anchorRel[i], anchorRel[i + 1], t);
            }
        }
        return bestRel;
    }

    /// <summary>
    /// center_point を float[] に変換する。
    /// </summary>
    public static float[] GetCenterPointArray(this GeneratedModels.PathSummary p)
    {
        return GeneratedModelUtils.ToFloatArray(p.center_point);
    }

    /// <summary>
    /// polygon を float[][] に変換する。
    /// </summary>
    public static float[][] GetPolygonArray(this GeneratedModels.PathSummary p)
    {
        return GeneratedModelUtils.ToFloatJaggedArray(p.polygon);
    }

    /// <summary>
    /// platform_side（駅パスのプラットフォーム設置側。Constants.PLATFORM_SIDE_*）を
    /// リフレクションで読む。endpoint_schemas シート未更新（=GeneratedModels.cs に
    /// フィールドが無い）でもコンパイル・実行が落ちないための措置で、
    /// VehicleManager の path_traffic と同じパターン。フィールドが無い間は
    /// 既定値（PLATFORM_SIDE_OUTSIDE=外側）を返す。
    /// スキーマ再生成後は自動的に実データを返すようになる（直接参照への置き換えも可）。
    /// </summary>
    private static readonly System.Reflection.FieldInfo PlatformSideField =
        typeof(GeneratedModels.PathSummary).GetField("platform_side");

    public static int GetPlatformSide(this GeneratedModels.PathSummary p)
    {
        if (p == null || PlatformSideField == null) return Constants.PLATFORM_SIDE_OUTSIDE;
        object value = PlatformSideField.GetValue(p);
        if (value is int i) return i;
        if (value is long l) return (int)l;
        return Constants.PLATFORM_SIDE_OUTSIDE;
    }

    /// <summary>
    /// start_control / end_control（端点のベジエ制御点 [x, 相対高さ, z]）を
    /// リフレクションで読む。GetPlatformSide と同じく、endpoint_schemas シート
    /// 未更新（=GeneratedModels.cs にフィールドが無い）でもコンパイル・実行が
    /// 落ちないための措置。フィールドが無い間・値が null の間は null を返し、
    /// 呼び出し側（PathManager.CalculatePathPoints 等）は従来どおり
    /// バックエンド _auto_fill_controls と同一式の自前計算にフォールバックする。
    /// </summary>
    private static readonly System.Reflection.FieldInfo StartControlField =
        typeof(GeneratedModels.PathSummary).GetField("start_control");
    private static readonly System.Reflection.FieldInfo EndControlField =
        typeof(GeneratedModels.PathSummary).GetField("end_control");

    public static List<float> GetStartControlList(this GeneratedModels.PathSummary p)
    {
        if (p == null || StartControlField == null) return null;
        return GeneratedModelUtils.ToFloatList(StartControlField.GetValue(p) as List<object>);
    }

    public static List<float> GetEndControlList(this GeneratedModels.PathSummary p)
    {
        if (p == null || EndControlField == null) return null;
        return GeneratedModelUtils.ToFloatList(EndControlField.GetValue(p) as List<object>);
    }

    /// <summary>
    /// follow_terrain（地形追従の個別設定。-1=未設定[バリアント既定]/0=非追従/1=追従）を
    /// リフレクションで読む。GetPlatformSide と同じく、endpoint_schemas シート未更新
    /// （=GeneratedModels.cs にフィールドが無い）でもコンパイル・実行が落ちないための措置。
    /// フィールドが無い間は VALUE_NONE（未設定＝バリアント既定に従う）を返す。
    /// スキーマ再生成後は自動的に実データを返すようになる。
    /// </summary>
    private static readonly System.Reflection.FieldInfo FollowTerrainField =
        typeof(GeneratedModels.PathSummary).GetField("follow_terrain");

    public static int GetFollowTerrain(this GeneratedModels.PathSummary p)
    {
        if (p == null || FollowTerrainField == null) return Constants.VALUE_NONE;
        object value = FollowTerrainField.GetValue(p);
        if (value is int i) return i;
        if (value is long l) return (int)l;
        return Constants.VALUE_NONE;
    }
}

/// <summary>
/// GeneratedModels.ParcelSummary の拡張メソッド。
/// 自動生成ファイルに変更を加えずに、座標データの変換機能を追加する。
/// </summary>
public static class ParcelSummaryExtensions
{
    /// <summary>
    /// center_point を float[] に変換する。
    /// </summary>
    public static float[] GetCenterPointArray(this GeneratedModels.ParcelSummary p)
    {
        return GeneratedModelUtils.ToFloatArray(p.center_point);
    }

    /// <summary>
    /// polygon を float[][] に変換する。
    /// </summary>
    public static float[][] GetPolygonArray(this GeneratedModels.ParcelSummary p)
    {
        return GeneratedModelUtils.ToFloatJaggedArray(p.polygon);
    }

    /// <summary>
    /// front_path_id（接道している街路のID。0=接道なし）をリフレクションで読む。
    /// PathSummaryExtensions.GetPlatformSide と同じく、endpoint_schemas シートの
    /// multi_parcel 未更新（=GeneratedModels.cs の ParcelSummary にフィールドが無い）でも
    /// コンパイル・実行が落ちないための措置。フィールドが無い間・値が無い間は 0 を返し、
    /// 呼び出し側（接道街路→面区画の逆引き）は「面区画なし」として振る舞う。
    /// スキーマ再生成後は自動的に実データを返すようになる（直接参照への置き換えも可）。
    /// </summary>
    private static readonly System.Reflection.FieldInfo FrontPathIdField =
        typeof(GeneratedModels.ParcelSummary).GetField("front_path_id");

    public static int GetFrontPathId(this GeneratedModels.ParcelSummary p)
    {
        if (p == null || FrontPathIdField == null) return 0;
        object value = FrontPathIdField.GetValue(p);
        if (value is int i) return i;
        if (value is long l) return (int)l;
        return 0;
    }
}

/// <summary>
/// GeneratedModels.ZoneData の拡張メソッド。
/// 自動生成ファイルに変更を加えずに、座標データの変換機能を追加する。
/// </summary>
public static class ZoneDataExtensions
{
    /// <summary>
    /// polygon を float[][] に変換する。
    /// </summary>
    public static float[][] GetPolygonArray(this GeneratedModels.ZoneData z)
    {
        return GeneratedModelUtils.ToFloatJaggedArray(z.polygon);
    }

    /// <summary>
    /// center_point を float[] に変換する。
    /// </summary>
    public static float[] GetCenterPointArray(this GeneratedModels.ZoneData z)
    {
        return GeneratedModelUtils.ToFloatArray(z.center_point);
    }
}
