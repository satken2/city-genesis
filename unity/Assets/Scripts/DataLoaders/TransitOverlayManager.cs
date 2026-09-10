using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// TransitOverlayManager:
/// 設置済みの公共交通パス(PATH_TRANSPORT)を「路線種別ごとの色帯」として、下地道路の上に
/// オーバーレイ描画する常設マネージャ。
///
/// PATH_TRANSPORT はメッシュ生成器を持たない(PathMeshFactory に登録が無い)ため通常描画では
/// 何も表示されないが、PathManager のキャッシュには点列込みで存在する。本マネージャは
/// PathManager.GetTransitPathsForOverlay() でその点列を取得し、OverlayMeshRenderer で
/// 種別色の帯(細い実線)として描く。
///
/// 表示は路線種別(区間パスのバリアント=transit_master の transit_path_id)単位で ON/OFF できる。
/// 表示対象のバリアント集合は表示オプションパネル(公共交通タブ)から
/// IpcViewCoordinator.ApplyTransitMode 経由で SetVisibleVariants() に渡される。
///
/// シーンに事前配置する必要はなく、AfterSceneLoad で自己生成する。
/// </summary>
public class TransitOverlayManager : MonoBehaviour
{
    public static TransitOverlayManager Instance { get; private set; }

    // 帯の太さ(ピクセル)。下地道路の上に細い色帯として重ねる。
    private const float RIBBON_THICKNESS = 4f;

    // 再構築の間引き基準(カメラ移動・回転・時間)。
    private const float REBUILD_MOVE_SQ = 1f;      // 1m
    private const float REBUILD_ANGLE = 1f;        // 1度
    private const float PERIODIC_REBUILD_SEC = 0.5f;

    // 角丸めパラメータ。cornerRadius=角のチャンファ半径(m)、cornerSegments=1角あたりの分割数。
    private const float CORNER_RADIUS = 6f;
    private const int CORNER_SEGMENTS = 6;

    // 連結時に共有端点(同一ノード)とみなす距離の二乗(m^2)。
    private const float WELD_DIST_SQ = 1f;

    private readonly HashSet<int> visibleVariants = new HashSet<int>();

    // 路線ごとの route_path_ids(=毎エポック再計算される実走行下地パスID列)。
    // GET /api/v1/transit/lines から取得し、下地パスの実ジオメトリを連結して描く。
    private List<TransitLineSummary> lines => TransitDataStore.Ensure().Lines;
    private int linesRevision = -1;

    private OverlayMeshRenderer overlayRenderer;
    private GameObject overlayMeshObj;
    private PathManager pathManager;

    private Vector3 lastCamPos;
    private Quaternion lastCamRot;
    private float rebuildTimer;
    private bool dirty;

    /// <summary>
    /// シーンに未配置でも動作するよう、シーンロード後に自己生成する。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance == null)
        {
            var go = new GameObject("TransitOverlayManager");
            go.AddComponent<TransitOverlayManager>();
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
        EnsureOverlayRenderer();
    }

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj != null) return;

        overlayMeshObj = new GameObject("TransitOverlayMesh");
        overlayMeshObj.transform.SetParent(transform);
        overlayRenderer = overlayMeshObj.AddComponent<OverlayMeshRenderer>();
        // デフォルトの Custom/OverlayLine(負のデプスオフセット)を使い、下地道路の上・地形に
        // 沿って帯を描く(ZTest Always にはせず、丘の裏の路線が透けないようにする)。
        Material mat = new Material(Shader.Find("Custom/OverlayLine"));
        overlayRenderer.Initialize(mat);
    }

    /// <summary>
    /// 表示する路線種別(区間パスのバリアント=transit_path_id)の集合を設定する。
    /// 空集合なら全非表示。
    /// </summary>
    public void SetVisibleVariants(HashSet<int> variants)
    {
        visibleVariants.Clear();
        if (variants != null)
        {
            foreach (int v in variants) visibleVariants.Add(v);
        }
        dirty = true;

        // 全非表示になった場合は即座にメッシュをクリアする。
        if (visibleVariants.Count == 0 && overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
    }

    /// <summary>データ更新(路線の作成・削除・リロード)後に再構築を促す。</summary>
    public void MarkDirty()
    {
        dirty = true;
    }

    /// <summary>
    /// セッション終了時やセッション切り替え時に、描画中の公共交通オーバーレイと路線キャッシュを完全にクリアする。
    /// これを怠ると、新しいゲームを開始した際に古いゲームの路線図やオーバーレイが画面に残ったままになる。
    /// </summary>
    public void Clear()
    {
        visibleVariants.Clear();
        TransitDataStore.Ensure().Clear();
        linesRevision = -1;
        dirty = false;
        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
    }

    /// <summary>
    /// 路線一覧(route_path_ids 込み)をバックエンドから再取得する。
    /// 路線作成直後(edit_transit.finish)と、毎エポックのセッションリロード後に呼ぶことで、
    /// 毎エポック再計算された実走行経路を描画へ反映する。
    /// </summary>
    public void RefreshLines() => TransitDataStore.Ensure().Refresh();

    private void Update()
    {
        if (visibleVariants.Count == 0) return;

        // 表示ONかつ未取得なら路線一覧を取得する(取得完了で dirty=true → 再構築)。
        if (linesRevision != TransitDataStore.Ensure().Revision)
        {
            linesRevision = TransitDataStore.Instance.Revision;
            dirty = true;
        }

        Camera cam = Camera.main;
        if (cam == null) return;

        rebuildTimer += Time.deltaTime;
        bool camMoved = (cam.transform.position - lastCamPos).sqrMagnitude > REBUILD_MOVE_SQ
                        || Quaternion.Angle(cam.transform.rotation, lastCamRot) > REBUILD_ANGLE;

        if (dirty || camMoved || rebuildTimer >= PERIODIC_REBUILD_SEC)
        {
            Rebuild(cam);
            dirty = false;
            rebuildTimer = 0f;
            lastCamPos = cam.transform.position;
            lastCamRot = cam.transform.rotation;
        }
    }

    private void Rebuild(Camera cam)
    {
        EnsureOverlayRenderer();
        if (pathManager == null) pathManager = FindAnyObjectByType<PathManager>();
        if (pathManager == null || overlayRenderer == null) return;

        overlayRenderer.BeginFrame();

        // 1) 路線ごとに route_path_ids(実走行下地パス列)を解決し、実ジオメトリを連結・角丸めして描く。
        //    描画できた路線種別(バリアント)を記録しておき、下の区間パス直線フォールバックから除外する。
        var drawnVariants = new HashSet<int>();
        foreach (var line in lines)
        {
            int variant = line.path_variant;
            if (!visibleVariants.Contains(variant)) continue;

            var poly = BuildRoutePolyline(line.route_path_ids);
            if (poly == null || poly.Count < 2) continue;

            poly = PolylineUtils.RoundCorners(poly, CORNER_RADIUS, CORNER_SEGMENTS);
            Color routeColor = GetVariantColor(variant);
            for (int i = 0; i < poly.Count - 1; i++)
            {
                overlayRenderer.AddSolidLine(poly[i], poly[i + 1], routeColor, RIBBON_THICKNESS);
            }
            drawnVariants.Add(variant);
        }

        // 2) フォールバック: 実経路を描けなかったバリアント(未取得・全区間切断・計画中など)は、
        //    従来どおり区間パス(停留所間の直線)で描画し、路線が消えないようにする。
        var transitPaths = pathManager.GetTransitPathsForOverlay();
        foreach (var (_, variant, points) in transitPaths)
        {
            if (!visibleVariants.Contains(variant)) continue;
            if (drawnVariants.Contains(variant)) continue;
            Color color = GetVariantColor(variant);
            for (int i = 0; i < points.Count - 1; i++)
            {
                overlayRenderer.AddSolidLine(points[i], points[i + 1], color, RIBBON_THICKNESS);
            }
        }

        overlayRenderer.EndFrame(cam);
    }

    /// <summary>
    /// route_path_ids(下地パスIDの走行順序列)を、各パスの実ジオメトリを連結した1本のポリラインにする。
    /// パスの順序はバックエンドを信頼し、各パスの点列は直前の末尾に近い端点を先頭に向ける端点マッチで
    /// 向きを合わせ、共有端点(同一ノード)は重複排除する。未読込/未解決のパスIDはスキップする。
    /// 連結できるジオメトリが無ければ null。
    /// </summary>
    private List<Vector3> BuildRoutePolyline(List<int> routePathIds)
    {
        if (routePathIds == null || routePathIds.Count == 0) return null;

        // まず解決できた下地パスの点列だけを順に集める。
        var segs = new List<List<Vector3>>();
        foreach (int pid in routePathIds)
        {
            if (pathManager.TryGetPathPolyline(pid, out var seg) && seg != null && seg.Count >= 2)
                segs.Add(seg);
        }
        if (segs.Count == 0) return null;

        // 先頭セグメントの向きは、2本目に近い側が末尾(=接合点)へ来るよう決める。
        if (segs.Count >= 2)
        {
            var a = segs[0];
            var b = segs[1];
            float tailToB = Mathf.Min((a[a.Count - 1] - b[0]).sqrMagnitude, (a[a.Count - 1] - b[b.Count - 1]).sqrMagnitude);
            float headToB = Mathf.Min((a[0] - b[0]).sqrMagnitude, (a[0] - b[b.Count - 1]).sqrMagnitude);
            if (headToB < tailToB) a.Reverse();
        }

        var poly = new List<Vector3>(segs[0]);
        for (int k = 1; k < segs.Count; k++)
        {
            var seg = segs[k];

            // 直前の末尾に近い端点が seg の先頭に来るよう向きを合わせる。
            Vector3 tail = poly[poly.Count - 1];
            if ((seg[seg.Count - 1] - tail).sqrMagnitude < (seg[0] - tail).sqrMagnitude)
                seg.Reverse();

            // 共有端点(ほぼ同座標)なら先頭を重複排除して連結する。
            int startIdx = ((seg[0] - tail).sqrMagnitude <= WELD_DIST_SQ) ? 1 : 0;
            for (int j = startIdx; j < seg.Count; j++)
            {
                poly.Add(seg[j]);
            }
        }

        return poly.Count >= 2 ? poly : null;
    }

    /// <summary>
    /// 路線種別(バリアント)ごとに安定した識別色を返す。
    /// マスターに色指定が無いため、バリアントIDから黄金角ベースの色相を割り当てる。
    /// </summary>
    private static Color GetVariantColor(int variant)
    {
        // 黄金比の小数部を掛けて色相を散らす(隣接IDでも色が離れる)。
        float hue = (variant * 0.61803398875f) % 1f;
        if (hue < 0f) hue += 1f;
        Color c = Color.HSVToRGB(hue, 0.75f, 0.95f);
        c.a = 0.9f;
        return c;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
