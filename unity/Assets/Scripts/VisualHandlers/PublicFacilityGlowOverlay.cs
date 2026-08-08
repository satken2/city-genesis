using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// 公共施設ハイライト(VisualFlag.HIGHLIGHT_PUBLIC)の描画を担う常駐オーバーレイ。
///
/// 建物本体のマテリアルは一切触らず、公共施設が立つ「土地区画の形状」に沿って
/// 地面フィル + 上方向へ立ち上がる光の柱(上端で透明になるグラデーション)を描く。
/// 建物のマテリアルを差し替えないため、地区別色塗り・耐久度色分けなどの
/// どの色モードと同時でも共存できる(旧実装は差し替え方式で色モードを潰していた)。
///
/// 対象は稼働中(STATUS_ACTIVE)かつ public_facility が真の建物のみ。
/// 敷地の解決は建物の中心座標からの空間判定(ParcelManager.GetParcelIdAtPoint)で行う
/// (GET /buildings/ の multi_agent スキーマには parcel_id が含まれないため、
///  建物データの parcel_id は当てにできない)。区画が未ロードで解決できない場合は
/// 建物の底面ポリゴンにフォールバックする。
///
/// メッシュはカメラに依存しないワールド固定ジオメトリなので毎フレーム作り直さず、
/// 一定間隔(RebuildInterval)でのみ再構築する。明滅アニメーションはメッシュではなく
/// マテリアルの _Tint を毎フレーム更新して表現する。
/// </summary>
public class PublicFacilityGlowOverlay : MonoBehaviour
{
    public static PublicFacilityGlowOverlay Instance { get; private set; }

    /// <summary>光の基本色(黄色)。</summary>
    private static readonly Color GlowColor = new Color(1f, 0.85f, 0.30f);

    /// <summary>地面フィルのアルファ。</summary>
    private const float GroundAlpha = 0.30f;

    /// <summary>光の柱の地面側アルファ。上端では 0 になる。</summary>
    private const float ColumnBaseAlpha = 0.42f;

    /// <summary>光の柱の高さ範囲(メートル)。区画の大きさに比例させた上でこの範囲へ収める。</summary>
    private const float ColumnHeightMin = 10f;
    private const float ColumnHeightMax = 34f;

    /// <summary>地面フィルを地形から浮かせるオフセット(メートル)。</summary>
    private const float GroundOffset = 0.08f;

    /// <summary>光の柱の辺を細分化する間隔(メートル)。起伏地形に裾を追従させるために分割する。</summary>
    private const float EdgeSubdivideStep = 6f;

    /// <summary>この距離より遠い公共施設は描画しない(メートル)。</summary>
    private const float CullDistance = 900f;
    private const float CullDistanceSq = CullDistance * CullDistance;

    /// <summary>メッシュ再構築の間隔(秒)。建物・区画のストリーミング追従はこの周期で行う。</summary>
    private const float RebuildInterval = 0.4f;

    /// <summary>明滅の周期(秒)と振幅。光が呼吸しているように見せる。</summary>
    private const float PulsePeriod = 2.0f;
    private const float PulseAmplitude = 0.18f;

    private OverlayMeshRenderer glowRenderer;
    private ParcelManager cachedParcelManager;
    private float nextRebuildTime;

    /// <summary>現在ハイライトを表示すべきか。表示フラグと色モードから毎フレーム解決する。</summary>
    private bool isActive;

    /// <summary>
    /// オーバーレイの常駐オブジェクトを用意する。BuildingManager の初期化から呼ばれる。
    /// シーン側の事前配置を必要とせず、多重生成もしない。
    /// </summary>
    public static void Ensure()
    {
        if (Instance != null) return;
        var go = new GameObject("PublicFacilityGlowOverlay");
        DontDestroyOnLoad(go);
        go.AddComponent<PublicFacilityGlowOverlay>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        var meshObj = new GameObject("PublicFacilityGlowMesh");
        meshObj.transform.SetParent(transform, false);
        glowRenderer = meshObj.AddComponent<OverlayMeshRenderer>();

        Material mat = new Material(Shader.Find("Custom/OverlayLine"));
        // 起伏地形で光が地面に埋もれないよう深度テストを無効化する
        // (ParcelManager のハイライトと同じ採用済みトレードオフ: 丘の裏でも透けて見える)。
        mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
        // 「塗り」ではなく「光」に見せるため加算合成にする(DstBlend=One)。
        mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
        glowRenderer.Initialize(mat);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        bool nextActive = ResolveActive();

        if (nextActive != isActive)
        {
            isActive = nextActive;
            if (!isActive)
            {
                glowRenderer.ClearMesh();
                return;
            }
            // ONにした瞬間は待たずに描く
            nextRebuildTime = 0f;
        }

        if (!isActive) return;

        // 明滅はメッシュ再構築を伴わないので毎フレーム反映する
        float pulse = 1f - PulseAmplitude * 0.5f * (1f - Mathf.Cos(Time.time * Mathf.PI * 2f / PulsePeriod));
        glowRenderer.SetTint(new Color(1f, 1f, 1f, pulse));

        if (Time.time < nextRebuildTime) return;
        nextRebuildTime = Time.time + RebuildInterval;
        RebuildMesh();
    }

    /// <summary>
    /// 表示フラグ(HIGHLIGHT_PUBLIC)と建物の色モードからハイライトの有効・無効を解決する。
    /// 建物を非表示にしている間は光らせない(隠しているものを目立たせないため)。
    /// </summary>
    private bool ResolveActive()
    {
        var handler = VisualModeHandler.Instance;
        if (handler == null) return false;
        if ((handler.GetFlags(InputObjectType.BUILDING) & VisualFlag.HIGHLIGHT_PUBLIC) == 0) return false;
        return handler.GetMode(InputObjectType.BUILDING) != VisualMode.MODE_HIDDEN;
    }

    private void RebuildMesh()
    {
        if (BuildingManager.Instance == null)
        {
            glowRenderer.ClearMesh();
            return;
        }

        Camera cam = Camera.main;
        if (cam == null) return;
        Vector3 camPos = cam.transform.position;

        if (cachedParcelManager == null)
        {
            cachedParcelManager = FindAnyObjectByType<ParcelManager>();
        }

        glowRenderer.BeginFrame();

        foreach (BuildingSummary building in BuildingManager.Instance.GetAllBuildings())
        {
            if (building == null) continue;
            if (!building.public_facility) continue;
            if (building.status != Constants.STATUS_ACTIVE) continue;

            float[] center = building.centerPointArray;
            if (center == null || center.Length < 2) continue;

            float dx = camPos.x - center[0];
            float dz = camPos.z - center[1];
            if (dx * dx + dz * dz >= CullDistanceSq) continue;

            float[][] footprint = ResolveGlowFootprint(building, center);
            if (footprint == null || footprint.Length < 3) continue;

            AddGlow(footprint);
        }

        glowRenderer.EndFrame(cam);
    }

    /// <summary>
    /// 光を出す平面形状(ワールドXZのポリゴン)を解決する。
    /// 敷地の土地区画が解決できればその形状を、できなければ建物の底面ポリゴンを返す。
    /// </summary>
    private float[][] ResolveGlowFootprint(BuildingSummary building, float[] center)
    {
        if (cachedParcelManager != null)
        {
            int parcelId = cachedParcelManager.GetParcelIdAtPoint(new Vector3(center[0], 0f, center[1]));
            if (parcelId > 0)
            {
                ParcelSummary parcel = cachedParcelManager.GetParcelSummary(parcelId);
                float[][] parcelPolygon = parcel != null ? parcel.GetPolygonArray() : null;
                if (parcelPolygon != null && parcelPolygon.Length >= 3) return parcelPolygon;
            }
        }
        return building.polygonArray;
    }

    /// <summary>
    /// 1区画分の光(地面フィル + 光の柱)をフレームキューへ積む。
    /// polygon は [i][0]=ワールドX, [i][1]=ワールドZ。
    /// </summary>
    private void AddGlow(float[][] polygon)
    {
        int count = polygon.Length;

        // 地面フィル。頂点ごとに地形高さをサンプルして起伏に追従させる。
        var groundVerts = new List<Vector3>(count);
        Color groundColor = new Color(GlowColor.r, GlowColor.g, GlowColor.b, GroundAlpha);
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            if (polygon[i] == null || polygon[i].Length < 2) return;
            float x = polygon[i][0];
            float z = polygon[i][1];
            groundVerts.Add(GroundPoint(x, z));
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }
        glowRenderer.AddPolygon(groundVerts, groundColor);

        // 光の柱の高さは区画の大きさに比例させる(小さな区画に高すぎる柱が立たないように)。
        float diagonal = Mathf.Sqrt((maxX - minX) * (maxX - minX) + (maxZ - minZ) * (maxZ - minZ));
        float columnHeight = Mathf.Clamp(diagonal * 0.7f, ColumnHeightMin, ColumnHeightMax);

        Color baseColor = new Color(GlowColor.r, GlowColor.g, GlowColor.b, ColumnBaseAlpha);
        Color topColor = new Color(GlowColor.r, GlowColor.g, GlowColor.b, 0f);

        var columnVerts = new List<Vector3>(count * 12);
        var columnColors = new List<Color>(count * 12);

        for (int i = 0; i < count; i++)
        {
            Vector2 a = new Vector2(polygon[i][0], polygon[i][1]);
            int next = (i + 1) % count;
            Vector2 b = new Vector2(polygon[next][0], polygon[next][1]);

            // 長い辺は分割し、裾を地形に沿わせる。
            int segments = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / EdgeSubdivideStep));
            Vector3 prev = GroundPoint(a.x, a.y);
            for (int s = 1; s <= segments; s++)
            {
                Vector2 p = Vector2.Lerp(a, b, (float)s / segments);
                Vector3 cur = GroundPoint(p.x, p.y);

                Vector3 prevTop = prev + Vector3.up * columnHeight;
                Vector3 curTop = cur + Vector3.up * columnHeight;

                columnVerts.Add(prev); columnColors.Add(baseColor);
                columnVerts.Add(prevTop); columnColors.Add(topColor);
                columnVerts.Add(curTop); columnColors.Add(topColor);

                columnVerts.Add(prev); columnColors.Add(baseColor);
                columnVerts.Add(curTop); columnColors.Add(topColor);
                columnVerts.Add(cur); columnColors.Add(baseColor);

                prev = cur;
            }
        }

        glowRenderer.AddPreTriangulated(columnVerts, columnColors);
    }

    /// <summary>ワールド座標 (x, z) の地形上の点(わずかに浮かせた高さ)を返す。</summary>
    private static Vector3 GroundPoint(float x, float z)
    {
        float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
        return new Vector3(x, y + GroundOffset, z);
    }
}
