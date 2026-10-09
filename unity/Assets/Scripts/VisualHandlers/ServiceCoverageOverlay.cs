using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// 表示パネルの効果範囲レイヤー(view:change_view の layer=coverage、sub_mode=サービス財の製品ID)を描く常駐オーバーレイ。
///
/// 選んだサービス財について、施設ごとの効果範囲の輪郭を地形に沿って描き、稼働中の建物を
/// 範囲内(青)・範囲外(赤)で塗る。範囲内かどうかの判定はコア(GET /buildings/service-coverage/{product_id})が
/// 敷地の土地区画で行い、ここでは返された建物IDの集合を引くだけにする。
/// 稼働していない施設(計画中・建設中など)はまだ供給しないので、範囲を破線で示すだけで判定には含まれない。
///
/// 建物の色は VisualModeObject が <see cref="TryGetBuildingColor"/> を引き、建物レイヤーの色モードより優先する。
/// 遠景の建物は結合描画で個別の色を持てないため、塗られるのは近景・通常の建物だけになる。
/// 輪郭はGPUでカメラへ向ける共有材質の線なので、カメラが動いてもCPUで作り直さない。
/// </summary>
public class ServiceCoverageOverlay : MonoBehaviour
{
    public static ServiceCoverageOverlay Instance { get; private set; }

    /// <summary>範囲内・範囲外の建物の色。UIの凡例(coverageColors.ts)と一致させる。</summary>
    public static readonly Color InRangeColor = new Color(0.231f, 0.510f, 0.965f);
    public static readonly Color OutOfRangeColor = new Color(0.937f, 0.267f, 0.267f);

    private static readonly Color OutlineColor = new Color(0.376f, 0.647f, 0.980f, 0.95f);
    private const float OutlinePixelThickness = 2.5f;

    /// <summary>輪郭を地形に沿わせるための細分化の間隔(m)。破線はこの区切り2つおきに描く。</summary>
    private const float OutlineStep = 8f;

    /// <summary>表示中の製品ID。0は非表示。</summary>
    private int productId;
    private int requestSerial;
    private readonly HashSet<int> coveredBuildings = new HashSet<int>();
    private readonly HashSet<int> uncoveredBuildings = new HashSet<int>();
    private OverlayMeshRenderer outline;

    /// <summary>常駐オブジェクトを用意する。シーン側の事前配置を必要とせず、多重生成もしない。</summary>
    public static void Ensure()
    {
        if (Instance != null) return;
        var go = new GameObject("ServiceCoverageOverlay");
        DontDestroyOnLoad(go);
        go.AddComponent<ServiceCoverageOverlay>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        outline = OverlayMeshRenderer.CreateStatic("ServiceCoverageOutline", true, transform);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// 効果範囲を表示中なら、その建物の色を返す。稼働中でない建物や、表示していないときは false。
    /// </summary>
    public static bool TryGetBuildingColor(int agentId, out Color color)
    {
        color = default;
        var overlay = Instance;
        if (overlay == null || overlay.productId <= 0) return false;
        if (overlay.coveredBuildings.Contains(agentId))
        {
            color = InRangeColor;
            return true;
        }
        if (overlay.uncoveredBuildings.Contains(agentId))
        {
            color = OutOfRangeColor;
            return true;
        }
        return false;
    }

    /// <summary>表示するサービス財を切り替える。0以下で非表示。表示中に同じ財を選び直したときも取り直す。</summary>
    public void SetProduct(int newProductId)
    {
        if (newProductId <= 0)
        {
            if (productId <= 0) return;
            productId = 0;
            Clear();
            return;
        }
        productId = newProductId;
        Refresh();
    }

    /// <summary>表示中なら、施設の追加やエポックの実行を反映するために範囲を取り直す。</summary>
    public void Refresh()
    {
        if (productId <= 0) return;
        int serial = ++requestSerial;
        int requested = productId;
        StartCoroutine(ApiClient.GetBulk<ServiceCoverageResponse>(
            $"/buildings/service-coverage/{requested}",
            response =>
            {
                if (serial != requestSerial || requested != productId) return;
                Apply(response);
            },
            error => Debug.LogError($"ServiceCoverageOverlay: 効果範囲の取得に失敗しました ({error})"),
            "ServiceCoverageOverlay"));
    }

    /// <summary>描画と建物の色を消す。表示中の財の選択は残し、次の <see cref="Refresh"/> で描き直す。</summary>
    public void Clear()
    {
        requestSerial++;
        coveredBuildings.Clear();
        uncoveredBuildings.Clear();
        outline.ClearMesh();
        VisualModeHandler.Instance?.RefreshObjects(InputObjectType.BUILDING);
    }

    private void Apply(ServiceCoverageResponse response)
    {
        coveredBuildings.Clear();
        uncoveredBuildings.Clear();
        if (response != null)
        {
            coveredBuildings.UnionWith(response.covered_building_ids);
            uncoveredBuildings.UnionWith(response.uncovered_building_ids);
        }

        // 地形高さは1mグリッドのフレームキャッシュを参照するため、最新の高さで描くために無効化する。
        Utils.InvalidateHeightCache();
        outline.ClearMesh();
        foreach (var supplier in response?.suppliers ?? new List<ServiceCoverageSupplier>())
        {
            var region = EffectRangeRegionData.Parse(supplier.region);
            if (region == null) continue;
            bool dashed = supplier.status != Constants.STATUS_ACTIVE;
            foreach (var ring in region.Rings())
            {
                AddRing(ring, dashed);
            }
        }
        outline.EndStaticFrame();

        VisualModeHandler.Instance?.RefreshObjects(InputObjectType.BUILDING);
    }

    /// <summary>閉じたリングを地形に沿う線として加える。破線は細分化した区切りを2つ描いて2つ空ける。</summary>
    private void AddRing(List<Vector2> ring, bool dashed)
    {
        int count = ring.Count;
        if (count < 2) return;
        int step = 0;
        for (int i = 0; i < count; i++)
        {
            Vector2 a = ring[i];
            Vector2 b = ring[(i + 1) % count];
            int segments = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / OutlineStep));
            Vector3 prev = GroundPoint(a);
            for (int k = 1; k <= segments; k++, step++)
            {
                Vector3 cur = GroundPoint(Vector2.Lerp(a, b, (float)k / segments));
                if (!dashed || (step / 2) % 2 == 0)
                {
                    outline.AddSolidLine(prev, cur, OutlineColor, OutlinePixelThickness);
                }
                prev = cur;
            }
        }
    }

    private static Vector3 GroundPoint(Vector2 xz)
    {
        float y = Utils.GetGroundHeight(new Vector3(xz.x, 1000f, xz.y));
        return new Vector3(xz.x, y + 0.5f, xz.y);
    }
}

/// <summary>GET /buildings/service-coverage/{product_id} の応答。</summary>
public class ServiceCoverageResponse
{
    public int product_id;
    public List<ServiceCoverageSupplier> suppliers = new List<ServiceCoverageSupplier>();
    public List<int> covered_building_ids = new List<int>();
    public List<int> uncovered_building_ids = new List<int>();
}

/// <summary>効果範囲を持つ供給施設1つ。region の形式は建設パネルの効果範囲と同じ。</summary>
public class ServiceCoverageSupplier
{
    public int agent_id;
    public int status;
    public JToken region;
}
