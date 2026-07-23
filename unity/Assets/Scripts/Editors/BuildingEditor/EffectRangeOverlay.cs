using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;
using GeneratedModels;

/// <summary>
/// インフラ供給施設(変電所・下水処理場・電波塔などサービス財を生産する施設)の効果範囲を
/// 3D空間にオーバーレイ表示するコンポーネント。
///
/// バックエンドの POST /api/v1/buildings/preview-effect-range が返す領域(円 or 不定形ポリゴン)を
/// 受け取り、以下の2層を地形追従で描画する。
///   1. 領域フィル: 効果範囲の面。まだ土地区画の無い原野も含めて範囲全体を半透明で塗る。
///      領域AABBをグリッドに切り、範囲内のセルを小さな四角形で敷き詰めることで、
///      任意形状(凹み・穴あり)でも破綻せず地形にも追従する。
///   2. 境界アウトライン: 効果範囲の外周(および穴)の輪郭線。範囲の端を明示する。
///
/// フィルは地形に埋もれてよいセル単位の平面なので既定の Custom/OverlayLine(LEqul+負の深度オフセット)で
/// 地形に沿わせ、丘に隠れる部分は自然に隠す。境界アウトラインは常に見えるべき補助線なので
/// _ZTest=Always のマテリアルで地形より手前に描く(RoadPlanManager のギズモと同じ方針)。
///
/// カメラ非依存の静的ジオメトリなので、データが変わったとき(SetData)にだけメッシュを再構築し、
/// 毎フレームの再構築は行わない。
/// </summary>
public class EffectRangeOverlay : MonoBehaviour
{
    // 領域フィル(地形追従・LEqul)。丘の裏に回る部分は隠れてよい。
    private OverlayMeshRenderer fillRenderer;
    // 境界線・区画アウトライン(常時手前=ZTest Always)。
    private OverlayMeshRenderer lineRenderer;
    private Camera cam;

    // 効果範囲の面(半透明の緑)。
    private static readonly Color FillColor = new Color(0.30f, 0.78f, 0.45f, 0.20f);
    // 効果範囲の外周・穴の輪郭。
    private static readonly Color BoundaryColor = new Color(0.36f, 0.92f, 0.52f, 0.95f);

    private void Awake()
    {
        cam = Camera.main;
        EnsureRenderers();
    }

    private void EnsureRenderers()
    {
        if (fillRenderer == null)
        {
            var fillObj = new GameObject("EffectRangeFillOverlay");
            fillObj.transform.SetParent(transform, false);
            fillRenderer = fillObj.AddComponent<OverlayMeshRenderer>();
            fillRenderer.Initialize(new Material(Shader.Find("Custom/OverlayLine")));
        }
        if (lineRenderer == null)
        {
            var lineObj = new GameObject("EffectRangeLineOverlay");
            lineObj.transform.SetParent(transform, false);
            lineRenderer = lineObj.AddComponent<OverlayMeshRenderer>();
            Material mat = new Material(Shader.Find("Custom/OverlayLine"));
            mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
            lineRenderer.Initialize(mat);
        }
    }

    /// <summary>効果範囲の表示をすべて消す。</summary>
    public void Clear()
    {
        EnsureRenderers();
        fillRenderer.ClearMesh();
        lineRenderer.ClearMesh();
    }

    /// <summary>
    /// 効果範囲データを受け取り、フィル・境界を構築する。
    /// payload が null またはクリア指示なら表示を消す。
    /// </summary>
    public void SetData(EffectRangePayload payload)
    {
        EnsureRenderers();
        if (cam == null) cam = Camera.main;
        if (payload == null || payload.IsClear)
        {
            Clear();
            return;
        }

        // 地形高さは1mグリッドのフレームキャッシュを参照するため、最新の高さで塗るために無効化する。
        Utils.InvalidateHeightCache();

        fillRenderer.BeginFrame();
        lineRenderer.BeginFrame();

        EffectRangeRegionData region = payload.region;
        if (region.type == "circle")
        {
            BuildCircleFill(region);
            BuildCircleOutline(region);
        }
        else
        {
            BuildPolygonFill(region);
            BuildPolygonOutline(region);
        }

        fillRenderer.EndFrame(cam);
        lineRenderer.EndFrame(cam);
    }

    // =====================================================================
    // 領域フィル(グリッド敷き詰め)
    // =====================================================================

    private void BuildCircleFill(EffectRangeRegionData region)
    {
        float r = region.radius;
        if (r <= 0f) return;
        Vector2 c = region.center;
        FillGrid(c.x - r, c.y - r, c.x + r, c.y + r,
            (x, z) => (x - c.x) * (x - c.x) + (z - c.y) * (z - c.y) <= r * r);
    }

    private void BuildPolygonFill(EffectRangeRegionData region)
    {
        if (region.polygons == null || region.polygons.Count == 0) return;
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var poly in region.polygons)
        {
            foreach (var p in poly.exterior)
            {
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minZ) minZ = p.y;
                if (p.y > maxZ) maxZ = p.y;
            }
        }
        if (minX > maxX) return;
        FillGrid(minX, minZ, maxX, maxZ, (x, z) => PolygonRegionContains(region, x, z));
    }

    /// <summary>
    /// AABB(minX,minZ)-(maxX,maxZ)をグリッドに切り、中心が範囲内のセルを四角形で塗る。
    /// セルは小さいので四隅の地形高さを個別にサンプルすれば起伏に追従する。
    /// </summary>
    private void FillGrid(float minX, float minZ, float maxX, float maxZ, System.Func<float, float, bool> inside)
    {
        float span = Mathf.Max(maxX - minX, maxZ - minZ);
        if (span <= 0f) return;
        // セル数が過大にならないよう、範囲に応じてステップを調整する(4m〜20m)。
        float step = Mathf.Clamp(span / 70f, 4f, 20f);

        var quad = new List<Vector3>(4);
        for (float x = minX; x < maxX; x += step)
        {
            for (float z = minZ; z < maxZ; z += step)
            {
                float cx = x + step * 0.5f;
                float cz = z + step * 0.5f;
                if (!inside(cx, cz)) continue;

                quad.Clear();
                quad.Add(GroundPoint(x, z));
                quad.Add(GroundPoint(x + step, z));
                quad.Add(GroundPoint(x + step, z + step));
                quad.Add(GroundPoint(x, z + step));
                fillRenderer.AddPolygon(quad, FillColor);
            }
        }
    }

    // =====================================================================
    // 境界アウトライン
    // =====================================================================

    private void BuildCircleOutline(EffectRangeRegionData region)
    {
        float r = region.radius;
        if (r <= 0f) return;
        Vector2 c = region.center;
        int segments = Mathf.Clamp(Mathf.CeilToInt(r / 4f), 24, 160);
        var ring = new List<Vector2>(segments);
        for (int i = 0; i < segments; i++)
        {
            float a = (float)i / segments * Mathf.PI * 2f;
            ring.Add(new Vector2(c.x + Mathf.Cos(a) * r, c.y + Mathf.Sin(a) * r));
        }
        DrawWorldRing(lineRenderer, ring, BoundaryColor, 1.6f, 6f);
    }

    private void BuildPolygonOutline(EffectRangeRegionData region)
    {
        foreach (var poly in region.polygons)
        {
            DrawWorldRing(lineRenderer, poly.exterior, BoundaryColor, 1.6f, 8f);
            foreach (var hole in poly.holes)
            {
                DrawWorldRing(lineRenderer, hole, BoundaryColor, 1.6f, 8f);
            }
        }
    }

    // =====================================================================
    // 描画ヘルパー
    // =====================================================================

    /// <summary>
    /// 閉じたリングを、地形追従の実線としてワールド固定幅で描く。
    /// 各辺を subdivideStep 間隔で細分化し、頂点ごとに地形高さをサンプルする。
    /// </summary>
    private void DrawWorldRing(OverlayMeshRenderer renderer, List<Vector2> ring, Color color, float worldThickness, float subdivideStep)
    {
        int count = ring.Count;
        if (count < 2) return;
        for (int i = 0; i < count; i++)
        {
            Vector2 a = ring[i];
            Vector2 b = ring[(i + 1) % count];
            float d = Vector2.Distance(a, b);
            int seg = Mathf.Max(1, Mathf.CeilToInt(d / subdivideStep));
            Vector3 prev = GroundPoint(a.x, a.y);
            for (int k = 1; k <= seg; k++)
            {
                float t = (float)k / seg;
                Vector2 p = Vector2.Lerp(a, b, t);
                Vector3 cur = GroundPoint(p.x, p.y);
                // gap を 0 にして実線として描く(dashLen を十分大きく取る)。
                renderer.AddDashedLineWorld(prev, cur, color, worldThickness, 1000f, 0f);
                prev = cur;
            }
        }
    }

    /// <summary>ワールド座標 (x, z) の地形上の点を返す。バックエンド座標をそのままUnityワールドとして扱う。</summary>
    private Vector3 GroundPoint(float x, float z)
    {
        float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
        return new Vector3(x, y + 0.05f, z);
    }

    /// <summary>ポリゴン領域(外周内かつ穴の外)に点が含まれるか。</summary>
    private static bool PolygonRegionContains(EffectRangeRegionData region, float x, float z)
    {
        foreach (var poly in region.polygons)
        {
            if (!PointInRing(poly.exterior, x, z)) continue;
            bool inHole = false;
            foreach (var hole in poly.holes)
            {
                if (PointInRing(hole, x, z)) { inHole = true; break; }
            }
            if (!inHole) return true;
        }
        return false;
    }

    /// <summary>even-odd 法によるリング内外判定。</summary>
    private static bool PointInRing(List<Vector2> ring, float x, float z)
    {
        int n = ring.Count;
        if (n < 3) return false;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 pi = ring[i];
            Vector2 pj = ring[j];
            if (((pi.y > z) != (pj.y > z)) &&
                (x < (pj.x - pi.x) * (z - pi.y) / (pj.y - pi.y) + pi.x))
            {
                inside = !inside;
            }
        }
        return inside;
    }
}

// =====================================================================
// 効果範囲ペイロード(IPC で受信した JSON をパースした素データ)
// =====================================================================

/// <summary>効果範囲の領域。円 or 不定形ポリゴン。</summary>
public class EffectRangeRegionData
{
    public string type;                                  // "circle" | "polygon"
    public Vector2 center;                               // circle
    public float radius;                                 // circle
    public List<EffectRangePolygonData> polygons = new List<EffectRangePolygonData>(); // polygon
}

/// <summary>不定形領域の1ポリゴン(外周 + 穴)。座標はワールド XZ。</summary>
public class EffectRangePolygonData
{
    public List<Vector2> exterior = new List<Vector2>();
    public List<List<Vector2>> holes = new List<List<Vector2>>();
}

/// <summary>edit_building:update_effect_range のペイロード。</summary>
public class EffectRangePayload
{
    public EffectRangeRegionData region;   // null またはクリア指示なら表示を消す

    public bool IsClear => region == null || string.IsNullOrEmpty(region.type);

    /// <summary>
    /// IPC の region トークン(JObject or null)からペイロードを組み立てる。
    /// </summary>
    public static EffectRangePayload FromJson(JToken regionToken)
    {
        var payload = new EffectRangePayload();
        if (regionToken == null || regionToken.Type == JTokenType.Null) return payload;
        var obj = regionToken as JObject;
        if (obj == null) return payload;

        string type = (string)obj["type"];
        var region = new EffectRangeRegionData { type = type };
        if (type == "circle")
        {
            var c = obj["center"] as JArray;
            if (c != null && c.Count >= 2)
                region.center = new Vector2((float)c[0], (float)c[1]);
            region.radius = obj["radius"] != null ? (float)obj["radius"] : 0f;
        }
        else if (type == "polygon")
        {
            var polys = obj["polygons"] as JArray;
            if (polys != null)
            {
                foreach (var p in polys)
                {
                    var pd = new EffectRangePolygonData
                    {
                        exterior = ParseRing(p["exterior"] as JArray),
                    };
                    var holes = p["holes"] as JArray;
                    if (holes != null)
                    {
                        foreach (var h in holes)
                        {
                            var hr = ParseRing(h as JArray);
                            if (hr.Count >= 3) pd.holes.Add(hr);
                        }
                    }
                    if (pd.exterior.Count >= 3) region.polygons.Add(pd);
                }
            }
        }
        payload.region = region;
        return payload;
    }

    private static List<Vector2> ParseRing(JArray arr)
    {
        var ring = new List<Vector2>();
        if (arr == null) return ring;
        foreach (var pt in arr)
        {
            var pa = pt as JArray;
            if (pa != null && pa.Count >= 2)
                ring.Add(new Vector2((float)pa[0], (float)pa[1]));
        }
        return ring;
    }
}
