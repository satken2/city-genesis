using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// RiverSurfaceManager:
/// 川の水面を3Dで描画する常設マネージャ。WaterSurfaceManager(湖)の川版。
///
/// バックエンドの <c>GET /api/v1/terrain/rivers</c> から川(範囲ポリゴン+水面プロファイル線)を
/// 取得して <see cref="RiverStore"/> を構築し、川ごとに「範囲を覆う格子面」の水面メッシュを作る。
/// 各頂点のYは「その地点のプロファイル標高(プロファイル線上の最近点)+微小な持ち上げ」で、
/// 湖の水平面と違い流下方向に沿って下る。ポリゴンを頂点ごとに三角形分割しないのは、細長く
/// 蛇行する川では外周頂点を結ぶ三角形が流下方向に長く伸び、頂点間の線形補間が実際の水面勾配
/// から乖離するため(地形エディタの3Dプレビュー riverGeometry.ts buildRiverSurfaceMesh と同構成)。
///
/// セルは「4隅のいずれかが範囲内」なら採用するため水面は岸線から最大1セル分はみ出すが、
/// はみ出し部分は岸(水面より高い地形)に埋まって見えない。
///
/// マテリアルは海面(SeaObject)の material を複製し、流れモード(_FlowEnabled)を有効にして使う。
/// 頂点の UV2 に流下方向(XZ単位ベクトル)を載せ、Custom/WaterLit がさざ波・泡をその向きへ流す。
///
/// 川はマップ固有の静的データ(init_game 時に確定)なので、海岸線・湖と同じセッション初期化
/// タイミング(<see cref="TerrainManager.FetchCoastline"/>)で一度だけ取得・生成する。
/// </summary>
public class RiverSurfaceManager : MonoBehaviour
{
    public static RiverSurfaceManager Instance { get; private set; }

    /// <summary>川床とのZ-fighting回避用に水面へ加える持ち上げ量(m)。</summary>
    private const float WATER_LIFT = 0.05f;
    /// <summary>さざ波・泡を流下方向へ流す速度(m/s)。海面材質の複製へ設定する。</summary>
    [SerializeField] private float flowSpeed = 1.5f;
    /// <summary>海面 material を借用できなかったときのフォールバック水面色(半透明の青)。</summary>
    [SerializeField] private Color fallbackWaterColor = new Color(0.20f, 0.45f, 0.65f, 0.75f);

    // --- 川床・川岸の地表表現(Assets/Shaders/TerrainRiverBank.hlsl が参照する全域マスク) ---
    [Header("River Bank Appearance")]
    [Tooltip("カーブ内岸の砂地の色")]
    [SerializeField] private Color sandColor = new Color(0.74f, 0.68f, 0.54f, 1f);
    [Tooltip("砂地のスムースネス")]
    [SerializeField] private float sandSmoothness = 0.12f;
    [Tooltip("濡れた川床の乗算色(rgb)とスムースネス(a)")]
    [SerializeField] private Color bedTint = new Color(0.62f, 0.62f, 0.60f, 0.45f);
    [Tooltip("砂地を置く岸帯の幅(m)。川の範囲の外側へこの距離まで、距離で減衰しながら砂にする")]
    [SerializeField] private float sandBandMeters = 16f;
    [Tooltip("砂地が最大濃度になる曲率(1/m)。半径 1/値 のカーブで内岸が完全な砂地になる")]
    [SerializeField] private float sandCurvatureRef = 0.005f;
    /// <summary>マスクテクスチャの一辺の上限(テクセル)。超えるときはテクセルを粗くする。</summary>
    private const int MASK_MAX_DIM = 4096;
    /// <summary>川の範囲の外側1テクセルに与える川床(濡れ)値(0..255)。岸ぎわの濡れ表現。</summary>
    private const byte WET_EDGE_VALUE = 96;

    private static readonly int RiverMaskId = Shader.PropertyToID("_RiverMask");
    private static readonly int RiverMaskParamsId = Shader.PropertyToID("_RiverMaskParams");
    private static readonly int RiverMaskSizeId = Shader.PropertyToID("_RiverMaskSize");
    private static readonly int RiverSandColorId = Shader.PropertyToID("_RiverSandColor");
    private static readonly int RiverBedTintId = Shader.PropertyToID("_RiverBedTint");
    private static readonly int RiverSandSmoothnessId = Shader.PropertyToID("_RiverSandSmoothness");
    private Texture2D riverMaskTex;

    private readonly List<GameObject> riverObjects = new List<GameObject>();
    private readonly List<Mesh> riverMeshes = new List<Mesh>();
    private Material riverMaterial;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance == null)
        {
            var go = new GameObject("RiverSurfaceManager");
            go.AddComponent<RiverSurfaceManager>();
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        ClearRiverObjects();
        ClearRiverMask();
        if (riverMaterial != null) Destroy(riverMaterial);
        riverMaterial = null;
    }

    /// <summary>
    /// バックエンドから川を取得し、RiverStore と水面メッシュを再生成する(冪等)。
    /// セッション開始時・全量リロード時に FetchCoastline から呼ばれる。
    /// </summary>
    public IEnumerator LoadAndBuild()
    {
        ClearRiverObjects();
        ClearRiverMask();
        RiverStore.Clear();

        RiversResponse response = null;
        string error = null;
        yield return ApiClient.Get<RiversResponse>(
            "/terrain/rivers",
            res => response = res,
            err => error = err
        );
        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogWarning($"RiverSurfaceManager: 川の取得に失敗: {error}");
            yield break;
        }
        if (response?.rivers == null) yield break;

        RiverStore.SetFromJson(response.rivers);
        if (!RiverStore.HasRivers) yield break;

        Material mat = ResolveRiverMaterial();
        foreach (var river in RiverStore.Rivers)
        {
            BuildOneRiver(river, mat);
        }
        BuildRiverMask();
        Debug.Log($"RiverSurfaceManager: 川の水面を{riverObjects.Count}本生成しました。");
    }

    /// <summary>セッション終了処理などから全ての水面オブジェクトを破棄する。</summary>
    public void Clear()
    {
        ClearRiverObjects();
        ClearRiverMask();
        RiverStore.Clear();
    }

    /// <summary>
    /// 川床・川岸の全域マスク(RG8: R=川床, G=砂地)を生成し、地形シェーダーのグローバルへ渡す。
    ///
    /// テクセル格子は RiverStore の格子(GRID_STEP)と同じ格子線上に置くため、各川の CornerMask を
    /// 整数オフセットでそのまま転記でき、ポリゴンの内外判定を繰り返さない。川群の外接矩形が
    /// MASK_MAX_DIM を超えるときは格子の整数倍にテクセルを粗くする(格子点を間引いて転記)。
    /// 範囲外側の岸帯は、内側テクセルから4近傍の幅優先で距離(テクセル単位)を広げて求め、
    /// 距離1は濡れた岸(R弱)、砂帯内はカーブ内岸度(RiverRecord.InnerBankFactorAt)×距離減衰を G にする。
    /// </summary>
    private void BuildRiverMask()
    {
        ClearRiverMask();
        if (!RiverStore.HasRivers) return;

        float step = RiverStore.GRID_STEP;
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        foreach (var r in RiverStore.Rivers)
        {
            minX = Mathf.Min(minX, r.GridOriginX); minZ = Mathf.Min(minZ, r.GridOriginZ);
            maxX = Mathf.Max(maxX, r.GridOriginX + (r.GridW - 1) * step);
            maxZ = Mathf.Max(maxZ, r.GridOriginZ + (r.GridH - 1) * step);
        }
        int bandCells = Mathf.Max(1, Mathf.CeilToInt(sandBandMeters / step));
        // 格子の整数倍でテクセルを決め、上限に収まるまで粗くする
        int factor = 1;
        while (Mathf.Max(maxX - minX, maxZ - minZ) / (step * factor) + 2 * bandCells + 2 > MASK_MAX_DIM) factor *= 2;
        float texel = step * factor;
        int bandTexels = Mathf.Max(1, Mathf.CeilToInt(sandBandMeters / texel));
        float ox = minX - bandTexels * texel - texel;
        float oz = minZ - bandTexels * texel - texel;
        int w = Mathf.CeilToInt((maxX - ox) / texel) + bandTexels + 2;
        int h = Mathf.CeilToInt((maxZ - oz) / texel) + bandTexels + 2;
        if (w <= 0 || h <= 0) return;

        // dist: -1=未到達, 0=川の範囲内, n=範囲からの4近傍距離。source: 最寄りの川(範囲内テクセルから伝播)
        var dist = new short[w * h];
        var source = new short[w * h];
        for (int k = 0; k < dist.Length; k++) { dist[k] = -1; source[k] = -1; }
        var queue = new Queue<int>();
        for (int ri = 0; ri < RiverStore.Rivers.Count; ri++)
        {
            var r = RiverStore.Rivers[ri];
            // 川の格子点 (i, j) → マスクテクセル (I, J)。同じ格子線上なので factor で割り切れる点だけ転記する
            int offI = Mathf.RoundToInt((r.GridOriginX - ox) / step);
            int offJ = Mathf.RoundToInt((r.GridOriginZ - oz) / step);
            for (int j = 0; j < r.GridH; j++)
            {
                int gj = j + offJ;
                if (gj % factor != 0) continue;
                int J = gj / factor;
                if (J < 0 || J >= h) continue;
                for (int i = 0; i < r.GridW; i++)
                {
                    if (r.CornerMask[j * r.GridW + i] == 0) continue;
                    int gi = i + offI;
                    if (gi % factor != 0) continue;
                    int I = gi / factor;
                    if (I < 0 || I >= w) continue;
                    int idx = J * w + I;
                    if (dist[idx] == 0) continue;
                    dist[idx] = 0;
                    source[idx] = (short)ri;
                    queue.Enqueue(idx);
                }
            }
        }
        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int d = dist[idx];
            if (d >= bandTexels) continue;
            int I = idx % w, J = idx / w;
            Span<int> neighbors = stackalloc int[4] { idx - 1, idx + 1, idx - w, idx + w };
            for (int n = 0; n < 4; n++)
            {
                if (n == 0 && I == 0) continue;
                if (n == 1 && I == w - 1) continue;
                if (n == 2 && J == 0) continue;
                if (n == 3 && J == h - 1) continue;
                int nidx = neighbors[n];
                if (dist[nidx] >= 0) continue;
                dist[nidx] = (short)(d + 1);
                source[nidx] = source[idx];
                queue.Enqueue(nidx);
            }
        }

        var data = new byte[w * h * 2];
        for (int idx = 0; idx < dist.Length; idx++)
        {
            int d = dist[idx];
            if (d < 0) continue;
            byte bed = 0, sand = 0;
            if (d == 0)
            {
                bed = 255;
            }
            else
            {
                if (d == 1) bed = WET_EDGE_VALUE;
                var river = RiverStore.Rivers[source[idx]];
                float x = ox + (idx % w) * texel;
                float z = oz + (idx / w) * texel;
                float falloff = 1f - (float)d / (bandTexels + 1);
                sand = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(falloff * river.InnerBankFactorAt(x, z, sandCurvatureRef)));
            }
            data[idx * 2] = bed;
            data[idx * 2 + 1] = sand;
        }

        riverMaskTex = new Texture2D(w, h, TextureFormat.RG16, false, true)
        {
            name = "RiverMask",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        riverMaskTex.SetPixelData(data, 0);
        riverMaskTex.Apply(false, true);

        Shader.SetGlobalTexture(RiverMaskId, riverMaskTex);
        Shader.SetGlobalVector(RiverMaskParamsId, new Vector4(ox, oz, 1f / texel, 1f));
        Shader.SetGlobalVector(RiverMaskSizeId, new Vector4(w, h, 0f, 0f));
        Shader.SetGlobalColor(RiverSandColorId, sandColor);
        Shader.SetGlobalColor(RiverBedTintId, bedTint);
        Shader.SetGlobalFloat(RiverSandSmoothnessId, sandSmoothness);
    }

    /// <summary>川マスクを無効化して破棄する(シェーダーは _RiverMaskParams.w=0 で参照を止める)。</summary>
    private void ClearRiverMask()
    {
        Shader.SetGlobalVector(RiverMaskParamsId, Vector4.zero);
        if (riverMaskTex != null) Destroy(riverMaskTex);
        riverMaskTex = null;
    }

    private void ClearRiverObjects()
    {
        foreach (var go in riverObjects) if (go != null) Destroy(go);
        riverObjects.Clear();
        foreach (var m in riverMeshes) if (m != null) Destroy(m);
        riverMeshes.Clear();
    }

    /// <summary>
    /// 海面(SeaObject)の material を複製して流れモードを有効にする。海面が無ければ
    /// Custom/WaterLit(無ければ DepthOffsetLit)でフォールバックを作る。
    /// 複製にするのは、海面・湖面(海面 material を共有)に流れを付けないため。
    /// </summary>
    private Material ResolveRiverMaterial()
    {
        if (riverMaterial != null) return riverMaterial;
        Material source = null;
        GameObject sea = null;
        try { sea = GameObject.FindWithTag("SeaObject"); }
        catch { sea = null; }
        if (sea != null)
        {
            Renderer seaRenderer = sea.GetComponentInChildren<Renderer>();
            if (seaRenderer != null) source = seaRenderer.sharedMaterial;
        }
        if (source != null)
        {
            riverMaterial = new Material(source) { name = "RiverSurface" };
        }
        else
        {
            Shader sh = Shader.Find("Custom/WaterLit") ?? Shader.Find("Custom/DepthOffsetLit");
            riverMaterial = new Material(sh) { name = "RiverSurfaceFallback" };
            if (riverMaterial.HasProperty("_BaseColor")) riverMaterial.SetColor("_BaseColor", fallbackWaterColor);
        }
        if (riverMaterial.HasProperty("_FlowEnabled")) riverMaterial.SetFloat("_FlowEnabled", 1f);
        if (riverMaterial.HasProperty("_FlowSpeed")) riverMaterial.SetFloat("_FlowSpeed", flowSpeed);
        return riverMaterial;
    }

    /// <summary>川1本分の格子面メッシュと GameObject を生成する。</summary>
    private void BuildOneRiver(RiverRecord river, Material mat)
    {
        int w = river.GridW, h = river.GridH;
        float step = river.GridStep;
        var vertexIndex = new int[w * h];
        for (int k = 0; k < vertexIndex.Length; k++) vertexIndex[k] = -1;
        var vertices = new List<Vector3>();
        var uv2 = new List<Vector2>();
        var triangles = new List<int>();

        int GetVertex(int i, int j)
        {
            int gi = j * w + i;
            int vi = vertexIndex[gi];
            if (vi >= 0) return vi;
            float x = river.GridOriginX + i * step;
            float z = river.GridOriginZ + j * step;
            vi = vertices.Count;
            vertexIndex[gi] = vi;
            vertices.Add(new Vector3(x, river.WaterLevelAt(x, z) + WATER_LIFT, z));
            uv2.Add(river.FlowDirAt(x, z));
            return vi;
        }

        for (int j = 0; j < h - 1; j++)
        {
            for (int i = 0; i < w - 1; i++)
            {
                if (!river.CellIncluded(i, j)) continue;
                int a = GetVertex(i, j), b = GetVertex(i + 1, j), c = GetVertex(i, j + 1), d = GetVertex(i + 1, j + 1);
                // +Y 法線になる巻き順(Unity は左手系・時計回りが表)
                triangles.Add(a); triangles.Add(c); triangles.Add(b);
                triangles.Add(b); triangles.Add(c); triangles.Add(d);
            }
        }
        if (triangles.Count < 3) return;

        Mesh mesh = new Mesh { name = $"RiverSurfaceMesh_{river.Id}" };
        if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(vertices);
        mesh.SetUVs(1, uv2);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        riverMeshes.Add(mesh);

        GameObject go = new GameObject($"RiverSurface_{river.Id}");
        go.transform.SetParent(transform);
        go.transform.position = Vector3.zero;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        // 海面と同じく、地形の透過・非表示モードに追従して水面も隠せるようにする。
        go.tag = "SeaObject";
        go.AddComponent<VisualSeaObject>();
        riverObjects.Add(go);
    }

    [System.Serializable]
    private class RiversResponse
    {
        // 各要素は {id, kind, polygon(GeoJSONライク), profile[[x,y,z],...]}。構造が入れ子なので JToken で受ける。
        public JToken rivers;
    }
}
