using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// WaterSurfaceManager:
/// 海面(Y=0の板)以外の水面(湖など)を3Dで描画する常設マネージャ。
///
/// バックエンドの <c>GET /api/v1/terrain/water</c> から水面ポリゴンの一覧を取得し、
/// 各ポリゴンを water_level(絶対標高Y)の水平面として三角形分割してメッシュ化する。
/// マテリアルはシーンに配置済みの海面(SeaObject)の material を借用して見た目を揃える
/// (借用できない場合は Custom/DepthOffsetLit のフォールバックマテリアルを生成)。
///
/// 海面板と同じ深度オフセット階層(海面/水面 = Custom/DepthOffsetLit, Offset -1,-1 で
/// per-cell 地形より手前)に乗るため、湖底(per-cellタイル)との Z-fighting は
/// 海面と同じ理屈で回避される。岸(水面より高い地形)では per-cell 地形が
/// ワールドYの差で自然に手前に描画される。
///
/// 水面はマップ固有の静的データ(init_game 時に確定)だが、<see cref="TerrainManager.FetchCoastline"/>
/// が海岸線の直後に毎回呼ぶので、初回ロード・強制リロード・session:reload のたびに取得・生成し直す。
///
/// シーンに事前配置する必要はなく、AfterSceneLoad で自己生成する。
/// </summary>
public class WaterSurfaceManager : MonoBehaviour
{
    public static WaterSurfaceManager Instance { get; private set; }

    /// <summary>
    /// 水面フォールバックマテリアル用シェーダー(Custom/DepthOffsetLit)。
    /// 海面 material を借用できたときは使わない。ビルドに確実に含めるため
    /// SerializeField で直接参照する(未設定時は Shader.Find フォールバック)。
    /// </summary>
    [SerializeField] private Shader waterShader;

    /// <summary>海面 material を借用できなかったときのフォールバック水面色(半透明の青)。</summary>
    [SerializeField] private Color fallbackWaterColor = new Color(0.20f, 0.45f, 0.65f, 0.75f);

    // 生成済みの水面 GameObject。リロード時にまとめて破棄する。
    private readonly List<GameObject> waterObjects = new List<GameObject>();
    // 生成したメッシュ。破棄時に Destroy する。
    private readonly List<Mesh> waterMeshes = new List<Mesh>();
    // フォールバックで生成したマテリアル(海面借用時は null)。
    private Material fallbackMaterial;

    /// <summary>シーンに未配置でも動作するよう、シーンロード後に自己生成する。</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance == null)
        {
            var go = new GameObject("WaterSurfaceManager");
            go.AddComponent<WaterSurfaceManager>();
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
        ClearWaterObjects();
    }

    /// <summary>
    /// バックエンドから水面ポリゴンを取得し、水面メッシュを再生成する。
    /// 初回ロード・強制リロード・session:reload の TerrainManager.FetchCoastline から呼ばれる。
    /// 呼び出しのたびに既存の水面を破棄してから作り直す(冪等)。
    /// </summary>
    public IEnumerator LoadAndBuild()
    {
        ClearWaterObjects();

        WaterResponse response = null;
        string error = null;
        yield return ApiClient.Get<WaterResponse>(
            "/terrain/water",
            res => response = res,
            err => error = err
        );

        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogWarning($"WaterSurfaceManager: 水面の取得に失敗: {error}");
            yield break;
        }
        if (response?.water_surfaces == null || response.water_surfaces.Count == 0)
        {
            yield break;
        }

        Material mat = ResolveWaterMaterial();
        foreach (var ws in response.water_surfaces)
        {
            if (ws == null) continue;
            BuildOneWaterSurface(ws, mat);
        }

        Debug.Log($"WaterSurfaceManager: 水面を{waterObjects.Count}枚生成しました。");
    }

    /// <summary>
    /// 外部（セッション終了処理など）からすべての水面オブジェクトを破棄するためのパブリックメソッド。
    /// 単一シーン内でセッションを切り替えるため、古いセッションで生成された水面メッシュやGameObjectを
    /// 確実にシーンから消去する必要がある。
    /// </summary>
    public void Clear()
    {
        ClearWaterObjects();
    }

    /// <summary>生成済みの水面 GameObject / Mesh と判定用ストアをすべて破棄する。</summary>
    private void ClearWaterObjects()
    {
        WaterSurfaceStore.Clear();
        foreach (var go in waterObjects)
        {
            if (go != null) Destroy(go);
        }
        waterObjects.Clear();
        foreach (var m in waterMeshes)
        {
            if (m != null) Destroy(m);
        }
        waterMeshes.Clear();
    }

    /// <summary>
    /// 水面マテリアルを解決する。まずシーンの海面(SeaObject タグ)の material を借用し、
    /// 見た目を海面と完全に揃える。海面が見つからない場合のみ、Custom/DepthOffsetLit の
    /// フォールバックマテリアルを生成して使い回す。
    /// </summary>
    private Material ResolveWaterMaterial()
    {
        // 海面 material の借用を試みる。
        GameObject sea = null;
        try { sea = GameObject.FindWithTag("SeaObject"); }
        catch { sea = null; } // タグ未定義環境でも落とさない
        if (sea != null)
        {
            Renderer seaRenderer = sea.GetComponentInChildren<Renderer>();
            if (seaRenderer != null && seaRenderer.sharedMaterial != null)
            {
                return seaRenderer.sharedMaterial;
            }
        }

        // フォールバック: Custom/DepthOffsetLit のマテリアルを一度だけ生成して使い回す。
        if (fallbackMaterial == null)
        {
            Shader sh = waterShader;
            if (sh == null) sh = Shader.Find("Custom/DepthOffsetLit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
            fallbackMaterial = new Material(sh) { name = "WaterSurfaceFallback" };
            if (fallbackMaterial.HasProperty("_BaseColor"))
                fallbackMaterial.SetColor("_BaseColor", fallbackWaterColor);
            if (fallbackMaterial.HasProperty("_Color"))
                fallbackMaterial.SetColor("_Color", fallbackWaterColor);
        }
        return fallbackMaterial;
    }

    /// <summary>
    /// 水面1枚分の GameObject を生成する。polygon(Polygon/MultiPolygon)を water_level の
    /// 水平面として三角形分割し、water_level の高さにメッシュを置く。
    /// 敷設中パスの横断判定用に、穴も含めた範囲を WaterSurfaceStore へ登録する。
    /// </summary>
    private void BuildOneWaterSurface(WaterSurfaceDto ws, Material mat)
    {
        float level = ws.water_level;

        // polygon から (外周リング, 穴リスト) のリストを取り出す。
        List<(List<Vector2> exterior, List<List<Vector2>> holes)> parts = ParsePolygon(ws.polygon);
        if (parts.Count == 0) return;

        var record = new WaterSurfaceRecord { Id = ws.id, Level = level };
        foreach (var (exterior, holes) in parts)
        {
            record.AddRing(exterior);
            foreach (var hole in holes) record.AddRing(hole);
        }
        WaterSurfaceStore.Add(record);

        var vertices = new List<Vector3>();
        var triangles = new List<int>();

        foreach (var (exterior, holes) in parts)
        {
            AppendRingMesh(exterior, holes, level, vertices, triangles);
        }
        if (vertices.Count < 3 || triangles.Count < 3) return;

        Mesh mesh = new Mesh { name = $"WaterSurfaceMesh_{ws.id}" };
        if (vertices.Count > 65000)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        waterMeshes.Add(mesh);

        GameObject go = new GameObject($"WaterSurface_{ws.id}");
        go.transform.SetParent(transform);
        // 頂点はバックエンドのワールド座標をそのまま持つ(UltraFar 陸地メッシュ・海面板と同じ座標系)。
        // GameObject 自体は原点に置く。
        go.transform.position = Vector3.zero;

        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;

        // 海面と同じく、地形の透過・非表示モードに追従して水面も隠せるようにする。
        go.tag = "SeaObject";
        go.AddComponent<VisualSeaObject>();

        waterObjects.Add(go);
    }

    /// <summary>
    /// 1つの外周リング(+穴)を三角形分割して vertices/triangles に追記する。
    /// 頂点は (x, level, z) のワールド座標。法線が +Y(上向き)になるよう巻き方向を調整する。
    ///
    /// 穴(interior ring)は v1 のエディタでは生成されない想定のため、現状はスキップして
    /// 警告ログを出す(外周のみ三角形分割する)。将来的に穴付きポリゴンを扱う場合は
    /// ここに穴のブリッジ処理を追加する。
    /// </summary>
    private void AppendRingMesh(List<Vector2> exterior, List<List<Vector2>> holes, float level,
                               List<Vector3> vertices, List<int> triangles)
    {
        if (exterior == null || exterior.Count < 3) return;
        if (holes != null && holes.Count > 0)
        {
            Debug.LogWarning($"WaterSurfaceManager: 穴付き水面ポリゴンは現状未対応です(外周のみ描画)。");
        }

        // 末尾が始点と重複していれば取り除く。
        List<Vector2> ring = new List<Vector2>(exterior);
        if (ring.Count >= 2 && (ring[0] - ring[ring.Count - 1]).sqrMagnitude < 1e-6f)
            ring.RemoveAt(ring.Count - 1);
        if (ring.Count < 3) return;

        // 耳切りは CCW 入力を前提とするため、CCW に正規化する(x=X, y=Z の2Dとして符号付き面積で判定)。
        if (PolygonUtils.SignedArea(ring) < 0f) ring.Reverse();

        Vector2[] poly = ring.ToArray();
        List<int> localTris = BuildingMeshBuilder.TriangulateOrFan(poly);
        if (localTris.Count < 3) return;

        int baseIndex = vertices.Count;
        for (int i = 0; i < poly.Length; i++)
        {
            vertices.Add(new Vector3(poly[i].x, level, poly[i].y));
        }
        // CCW(XZ平面)の三角形は法線が -Y(下向き)になるため、巻き方向を反転して +Y(上向き)にする。
        for (int i = 0; i + 2 < localTris.Count; i += 3)
        {
            triangles.Add(baseIndex + localTris[i]);
            triangles.Add(baseIndex + localTris[i + 2]);
            triangles.Add(baseIndex + localTris[i + 1]);
        }
    }

    /// <summary>
    /// GeoJSON ライクな polygon(JToken)を「(外周リング, 穴リスト)」の一覧に変換する。
    /// Polygon は要素1つ、MultiPolygon は複数要素を返す。座標はワールド座標(XZ)。
    /// </summary>
    private static List<(List<Vector2>, List<List<Vector2>>)> ParsePolygon(JToken polygon)
    {
        var result = new List<(List<Vector2>, List<List<Vector2>>)>();
        if (polygon == null) return result;

        string gtype = polygon["type"]?.ToString();
        JToken coords = polygon["coordinates"];
        if (coords == null) return result;

        if (gtype == "Polygon")
        {
            var part = ParseSinglePolygon(coords);
            if (part.HasValue) result.Add(part.Value);
        }
        else if (gtype == "MultiPolygon")
        {
            foreach (JToken polyCoords in coords)
            {
                var part = ParseSinglePolygon(polyCoords);
                if (part.HasValue) result.Add(part.Value);
            }
        }
        return result;
    }

    /// <summary>[外周, 穴1, 穴2, ...] のリング配列を (外周, 穴リスト) に変換する。</summary>
    private static (List<Vector2>, List<List<Vector2>>)? ParseSinglePolygon(JToken rings)
    {
        if (!(rings is JArray ringArray) || ringArray.Count == 0) return null;
        List<Vector2> exterior = GeneratedModelUtils.ParseRing(ringArray[0]);
        if (exterior == null || exterior.Count < 3) return null;

        var holes = new List<List<Vector2>>();
        for (int i = 1; i < ringArray.Count; i++)
        {
            List<Vector2> hole = GeneratedModelUtils.ParseRing(ringArray[i]);
            if (hole != null && hole.Count >= 3) holes.Add(hole);
        }
        return (exterior, holes);
    }


    // === API レスポンス型 ===

    [System.Serializable]
    private class WaterResponse
    {
        public List<WaterSurfaceDto> water_surfaces;
    }

    [System.Serializable]
    private class WaterSurfaceDto
    {
        public string id;
        public float water_level;
        public string kind;
        // GeoJSON ライク(Polygon/MultiPolygon)。ネスト構造が type で変わるため生の JToken で受ける。
        public JToken polygon;
    }
}
