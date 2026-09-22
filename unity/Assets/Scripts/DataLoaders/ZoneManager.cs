using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// ゾーン情報を管理し描画するマネージャー。
/// 地区の枠線、地区名、建設可能建物アイコンはすべて2D UIオーバーレイ（ScreenSpaceOverlay）上に表示する。
/// これにより地形の高さ変化やLOD切り替えに依存せず、正確なスクリーン座標で一貫した表示が可能となる。
/// </summary>
public class ZoneManager : BaseApiDataManager, IVisualModeHandler
{
    public static ZoneManager Instance { get; private set; }

    private Dictionary<int, ZoneData> zoneCache = new Dictionary<int, ZoneData>();
    private Dictionary<int, Rect> zoneBboxCache = new Dictionary<int, Rect>();

    /// <summary>
    /// バックエンドから取得したゾーンのversionをキャッシュする。
    /// 差分更新時にバックエンドのversionと比較し、変更があった場合のみUIを再作成する。
    /// </summary>
    private Dictionary<int, int> zoneVersionCache = new Dictionary<int, int>();

    // 2D UI上の地区名ラベル（zoneId -> Text GameObject）
    private Dictionary<int, GameObject> zoneLabelUIs = new Dictionary<int, GameObject>();
    // 2D UI上の建物アイコン（zoneId -> Image GameObjectリスト）
    private Dictionary<int, List<GameObject>> zoneIconUIs = new Dictionary<int, List<GameObject>>();

    /// <summary>
    /// ゾーンの枠線データ。ワールド座標の端点を保持し、
    /// OverlayMeshRenderer へ毎フレーム渡して3D空間メッシュとして描画する。
    /// </summary>
    private class ZoneLineEntry
    {
        public Vector3 worldA;
        public Vector3 worldB;
    }
    private Dictionary<int, List<ZoneLineEntry>> zoneLinesByZone = new Dictionary<int, List<ZoneLineEntry>>();

    private const float LINE_THICKNESS = 5f;

    /// <summary>地区編集モード中の枠線の太さ。通常表示より太くして境界を主役にする。</summary>
    private const float EDIT_LINE_THICKNESS = 8f;

    /// <summary>地区編集モード中、選択中(拡張先・統合先)の地区の枠線の太さ。</summary>
    private const float SELECTED_LINE_THICKNESS = 13f;

    /// <summary>
    /// 枠線を地区の内側へ寄せる量(メートル)。
    /// 隣り合う地区は境界の街路を共有するためポリゴンの辺がぴったり重なり、
    /// セットバックしないと2本の枠線が同じ位置に描かれてどちらの色か分からなくなる。
    /// 内側へずらすことで、境界では2本の線が平行に並んで両方の地区色が読める。
    /// </summary>
    private const float OUTLINE_SETBACK = 3.0f;

    /// <summary>
    /// セットバックのマイター長の上限(セットバック量に対する倍率)。
    /// 鋭角の頂点では内側オフセットのマイター長が発散するため、ここで打ち切る。
    /// </summary>
    private const float OUTLINE_MITER_LIMIT = 4.0f;

    /// <summary>
    /// 地区編集モード中かどうか(SetEditOutlineMode で切り替える)。
    /// ONの間は枠線を太くし、マテリアルの _ZTest=Always で地形・建物に隠れないようにする。
    /// 通常プレイ中は深度テストを残す(全マップの枠線が山や建物を透けて見えると煩雑なため)。
    /// </summary>
    private bool editOutlineMode = false;

    /// <summary>地区編集モードで選択中(拡張先・統合先)の地区ID。0 は未選択。</summary>
    private int selectedZoneId = 0;

    private GameObject uiCanvasRoot;
    private Canvas uiCanvas;
    private RectTransform uiCanvasRect;

    private GameObject overlayMeshObj;
    private OverlayMeshRenderer overlayRenderer;

    /// <summary>

    /// <summary>
    /// 地区編集モードで既存地区にホバーした際の半透明フィル描画用レンダラ。
    /// 枠線用の overlayRenderer とは別インスタンスにし、マテリアルの _ZTest=Always で
    /// 地形・建物に隠れず常に手前に塗る（ParcelManager のデータ可視化と同じ採用済みトレードオフ）。
    /// ホバー地区が変わったフレームだけメッシュを作り直し、それ以外は既存メッシュをそのまま描く
    /// （OverlayMeshRenderer は次の BeginFrame まで前フレームのメッシュを保持するため毎フレームコストは無い）。
    /// </summary>
    private GameObject hoverFillMeshObj;
    private OverlayMeshRenderer hoverFillRenderer;
    private int hoverFillZoneId = -1;
    /// <summary>ホバーフィルの三角形頂点バッファ（GC回避のため使い回す）。</summary>
    private readonly List<Vector3> hoverFillTriVerts = new List<Vector3>(256);
    /// <summary>ホバーフィルの不透明度。</summary>
    private const float HOVER_FILL_ALPHA = 0.25f;
    /// <summary>ホバーフィルを地形から浮かせるオフセット（メートル）。</summary>
    private const float HOVER_FILL_HEIGHT_OFFSET = 0.08f;

    private TerrainManager terrainManager;

    private bool areZonesLoaded = false;
    private bool isZoneLoadInProgress = false;

    /// <summary>
    /// ロード進行中に RequestZoneRefresh が呼ばれた場合の追加リロード予約。
    /// 進行中のロードは古いスナップショットを取得している可能性があるため、
    /// 完了後にもう一度リロードして最新状態に追随する。
    /// </summary>
    private bool zoneRefreshQueued = false;

    // カメラ変化検知によるUI位置更新間引き用
    private Vector3 lastCamPos = Vector3.negativeInfinity;
    private Quaternion lastCamRot = Quaternion.identity;
    private const float CAMERA_POS_THRESHOLD = 1.0f;
    private const float CAMERA_ROT_THRESHOLD = 2.0f;

    public Dictionary<int, ZoneData> GetAllZones()
    {
        return zoneCache;
    }

    /// <summary>
    /// 指定したワールド座標がどの地区ポリゴン内にあるかを判定し、該当する地区IDを返す。
    /// どの地区にも含まれない場合は -1 を返す。
    /// </summary>
    public int GetZoneIdAtPoint(Vector3 worldPos)
    {
        Vector2 point = new Vector2(worldPos.x, worldPos.z);
        foreach (var kvp in zoneCache)
        {
            int zoneId = kvp.Key;
            if (zoneBboxCache.TryGetValue(zoneId, out Rect bbox))
            {
                if (!bbox.Contains(point)) continue;
            }

            float[][] poly = kvp.Value.GetPolygonArray();
            if (poly == null || poly.Length < 3) continue;
            if (PolygonUtils.Contains(poly, point.x, point.y))
            {
                return zoneId;
            }
        }
        return -1;
    }

    /// <summary>
    /// 地区IDから地区名を取得する。
    /// 存在しない場合は空文字を返す。
    /// </summary>
    public string GetZoneName(int zoneId)
    {
        if (zoneCache.TryGetValue(zoneId, out var zone))
        {
            return zone.name ?? "";
        }
        return "";
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

    private void Start()
    {
        EnsureUICanvas();
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.RegisterObject(InputObjectType.ZONE, this);
        }

        terrainManager = FindAnyObjectByType<TerrainManager>();
        if (terrainManager != null)
        {
            terrainManager.OnAllTerrainLodLoaded += OnTerrainAllLoaded;
        }
    }

    public override void LoadCells(List<Vector2Int> neededCells, bool isForce)
    {
        // 頻繁なロードを避けるため、Force指定時または未ロード時のみ実行
        if (!isZoneLoadInProgress && (isForce || !areZonesLoaded))
        {
            RenderWorkScheduler.Start(this, LoadAllZones(), "ZoneManager");
        }
    }

    protected override void UnloadCell(Vector2Int cellIndex)
    {
        // 使用しない
    }

    public override void ClearAllCells()
    {
        foreach (var kv in zoneLabelUIs)
        {
            if (kv.Value != null)
            {
                Destroy(kv.Value);
            }
        }
        zoneLabelUIs.Clear();

        foreach (var kvIcons in zoneIconUIs)
        {
            foreach (var icon in kvIcons.Value)
            {
                if (icon != null)
                {
                    Destroy(icon);
                }
            }
        }
        zoneIconUIs.Clear();

        zoneLinesByZone.Clear();

        zoneCache.Clear();
        zoneBboxCache.Clear();
        zoneVersionCache.Clear();
        loadedCells.Clear();
        areZonesLoaded = false;

        // ホバーフィルもクリアする（残存すると存在しない地区の塗りが残るため）
        ClearHoverFill();
    }

    /// <summary>
    /// 全地区を version 差分で再取得する。version が変わった地区のみ再描画される。
    /// エポック完了・編集確定後の CellDataLoader.ReloadAllData から呼ばれる。
    ///
    /// 地区はエポックごとにシステムが自動で作成・統合・分離し、ポリゴンも変化する
    /// (地区整合性フェーズ)。LoadCells は「未ロード時と強制時のみ再取得」の設計のため、
    /// ReloadAllData の isForce=false 経路では地区が二度と更新されない。
    /// エポック由来の地区の増減・変形はこのメソッドで追随する。
    /// </summary>
    public void RequestZoneRefresh()
    {
        if (isZoneLoadInProgress)
        {
            zoneRefreshQueued = true;
            return;
        }
        RenderWorkScheduler.Start(this, LoadAllZones(), "ZoneManager");
    }

    /// <summary>
    /// version 差分をバイパスして全地区を再取得・再描画する。
    /// 地区編集の確定後(道路の地区割り当て変更後)に呼ぶ。地区の拡張では侵食された側の地区や、
    /// 空になって消滅した地区も含めて複数地区のポリゴンが同時に変わるため、version 差分に頼らず
    /// 全地区を再描画する(消滅した地区の枠線・ラベルは UpdateZone / SyncZones が消す)。
    /// </summary>
    public void ReloadAllZonesForced()
    {
        // 全地区を再描画対象にする(version が同じでも UpdateZone が走るようにする)。
        zoneVersionCache.Clear();
        // 進行中のロードが無ければ新規に開始する。進行中の場合も、上でキャッシュを空にしたため
        // その完了時に全地区が再描画される。
        if (!isZoneLoadInProgress)
        {
            RenderWorkScheduler.Start(this, LoadAllZones(), "ZoneManager");
        }
    }

    public IEnumerator LoadAllZones()
    {
        if (isZoneLoadInProgress) yield break;

        isZoneLoadInProgress = true;
        ZonesListResponse response = null;
        yield return ApiClient.GetBulk<ZonesListResponse>(
            "/zones/",
            r => response = r,
            timingLabel: "ZoneManager zones");
        if (response == null)
        {
            areZonesLoaded = false;
            isZoneLoadInProgress = false;
            yield break;
        }

        HashSet<int> receivedIds = new HashSet<int>();

        if (response != null && response.zones != null)
        {
            foreach (var zone in response.zones)
            {
                try
                {
                    receivedIds.Add(zone.zone_id);

                    // version比較: キャッシュと同じversionなら再作成しない
                    if (zoneVersionCache.TryGetValue(zone.zone_id, out int cachedVersion) && cachedVersion == zone.version)
                    {
                        // 何もしない（UI位置は毎フレームUpdateで更新している）
                    }
                    else
                    {
                        UpdateZone(zone);
                        zoneVersionCache[zone.zone_id] = zone.version;
                    }
                }
                catch (Exception e)
                {
                    int zoneId = zone != null ? zone.zone_id : -1;
                    Debug.LogError($"ZoneManager: ゾーン {zoneId} の描画生成に失敗しました: {e.Message}\n{e.StackTrace}");
                }
            }
        }

        SyncZones(receivedIds);
        areZonesLoaded = true;
        isZoneLoadInProgress = false;
        // ゾーンデータの更新後にオーバーレイの再描画を確実に行うため、
        // カメラ位置のトラッキングをリセットする。これにより次のUpdateで
        // 枠線やラベルの位置が強制的に再計算される。
        lastCamPos = Vector3.negativeInfinity;
        Debug.Log($"ZoneManager: ゾーン一覧を読み込みました count={receivedIds.Count}");

        // ロード中に RequestZoneRefresh が予約されていたら、最新状態へ追随するため
        // もう一度リロードする(今回のロードは予約時点より古いスナップショットの可能性がある)。
        if (zoneRefreshQueued)
        {
            zoneRefreshQueued = false;
            RenderWorkScheduler.Start(this, LoadAllZones(), "ZoneManager");
        }
    }

    private void SyncZones(HashSet<int> receivedIds)
    {
        List<int> toRemove = new List<int>();
        foreach (int zoneId in zoneCache.Keys)
        {
            if (!receivedIds.Contains(zoneId))
                toRemove.Add(zoneId);
        }
        foreach (int zoneId in toRemove)
        {
            RemoveZone(zoneId);
        }
    }

    public void RemoveZone(int zoneId)
    {
        if (zoneLabelUIs.TryGetValue(zoneId, out GameObject labelObj))
        {
            Destroy(labelObj);
            zoneLabelUIs.Remove(zoneId);
        }

        if (zoneIconUIs.TryGetValue(zoneId, out List<GameObject> icons))
        {
            foreach (var icon in icons)
            {
                if (icon != null) Destroy(icon);
            }
            zoneIconUIs.Remove(zoneId);
        }

        RemoveZoneLines(zoneId);

        zoneCache.Remove(zoneId);
        zoneBboxCache.Remove(zoneId);
    }

    public IEnumerator FetchZone(int zoneId)
    {
        // バックエンドの GET /zones/{id} は { zone: { ... } } というラップ形式で返すため、
        // ZoneSingleResponse で受けてから .zone を取り出す。
        yield return ApiClient.Get<ZoneSingleResponse>(
            $"/zones/{zoneId}",
            response =>
            {
                if (response != null && response.zone != null)
                    UpdateZone(response.zone);
            },
            error => Debug.LogError($"FetchZone({zoneId}) failed: {error}")
        );
    }

    private void EnsureUICanvas()
    {
        if (uiCanvasRoot != null) return;

        uiCanvas = Utils.CreateOverlayCanvas("ZoneUICanvas", 60, persistent: true); // 3Dより前、モーダルより後ろ
        uiCanvasRoot = uiCanvas.gameObject;
        uiCanvasRect = uiCanvas.GetComponent<RectTransform>();
    }

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj != null) return;

        overlayRenderer = OverlayMeshRenderer.Create("ZoneOverlayMesh", alwaysOnTop: editOutlineMode, persistent: true);
        overlayMeshObj = overlayRenderer.gameObject;
    }

    /// <summary>
    /// 枠線マテリアルの深度テストを、現在のモードに合わせて設定する。
    /// 地区編集中は Always(8) にして地形・建物に埋もれず常に手前へ描き、
    /// 通常プレイ中は既定の LEqual(4) に戻す。
    /// </summary>
    private void ApplyOutlineDepthTest()
    {
        if (overlayRenderer == null) return;
        overlayRenderer.Material.SetFloat(
            "_ZTest",
            (float)(editOutlineMode
                ? UnityEngine.Rendering.CompareFunction.Always
                : UnityEngine.Rendering.CompareFunction.LessEqual));
    }

    /// <summary>
    /// 地区編集モードの枠線表示(太線＋深度テスト無効)を切り替える。
    /// ZoneEditContext の EnterCreateMode / EnterEditMode / Exit から呼ばれる。
    /// </summary>
    public void SetEditOutlineMode(bool enabled)
    {
        if (editOutlineMode == enabled) return;
        editOutlineMode = enabled;
        ApplyOutlineDepthTest();
        // 太さが変わるためオーバーレイを次のUpdateで必ず描き直す
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// 地区編集モードで選択中(拡張先・統合先)の地区を設定する。
    /// 選択中の地区は枠線をさらに太く描き、カーソルHUDの色チップと合わせて
    /// 「いまどの地区に対して操作しているか」を3Dビュー上で示す。0 で選択解除。
    /// </summary>
    public void SetSelectedZone(int zoneId)
    {
        if (selectedZoneId == zoneId) return;
        selectedZoneId = zoneId;
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// ホバーフィル用のレンダラを生成する(枠線用とは別インスタンス・別マテリアル。
    /// 枠線と違いモードに依らず常に手前に塗る)。
    /// </summary>
    private void EnsureHoverFillRenderer()
    {
        if (hoverFillMeshObj != null) return;

        hoverFillRenderer = OverlayMeshRenderer.Create("ZoneHoverFillMesh", alwaysOnTop: true, persistent: true);
        hoverFillMeshObj = hoverFillRenderer.gameObject;
    }

    /// <summary>
    /// 既存地区にホバーした際の半透明フィルを描画する。
    /// 地区編集モードの InputHandler がホバー地区の変化時にのみ呼ぶ（毎フレームは呼ばない）。
    /// zoneId が現在の描画と同じなら何もしない。zoneId <= 0 でクリア。
    /// 地区ポリゴンを耳切り分割（BuildingMeshBuilder.TriangulateOrFan。凹地区に対応）し、
    /// 各頂点Yを地形高さに追従させて一度だけメッシュ化する。
    /// </summary>
    public void SetHoverFillZone(int zoneId)
    {
        if (zoneId == hoverFillZoneId) return;
        hoverFillZoneId = zoneId;

        if (zoneId <= 0)
        {
            hoverFillRenderer?.ClearMesh();
            return;
        }

        if (!zoneCache.TryGetValue(zoneId, out ZoneData zone)) return;
        float[][] poly = zone.GetPolygonArray();
        if (poly == null || poly.Length < 3)
        {
            hoverFillRenderer?.ClearMesh();
            return;
        }

        // ポリゴンを Vector2[] に変換して三角形分割する（[i][0]=X, [i][1]=Z）。
        Vector2[] poly2D = new Vector2[poly.Length];
        for (int i = 0; i < poly.Length; i++)
        {
            float[] v = poly[i];
            poly2D[i] = (v != null && v.Length >= 2) ? new Vector2(v[0], v[1]) : Vector2.zero;
        }

        List<int> tris = BuildingMeshBuilder.TriangulateOrFan(poly2D);
        if (tris.Count < 3)
        {
            hoverFillRenderer?.ClearMesh();
            return;
        }

        // 分割インデックスを「3頂点/三角形」のフラット頂点列へ展開し、各頂点を地形高さに追従させる。
        hoverFillTriVerts.Clear();
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                Vector2 p = poly2D[tris[i + k]];
                float y = Utils.GetGroundHeight(new Vector3(p.x, 1000f, p.y)) + HOVER_FILL_HEIGHT_OFFSET;
                hoverFillTriVerts.Add(new Vector3(p.x, y, p.y));
            }
        }

        EnsureHoverFillRenderer();
        Color fillColor = GetZoneColor(zoneId, HOVER_FILL_ALPHA);
        hoverFillRenderer.BeginFrame();
        hoverFillRenderer.AddPreTriangulated(hoverFillTriVerts, fillColor);
        hoverFillRenderer.EndFrame(Camera.main);
    }

    /// <summary>
    /// ホバーフィルを消す（地区編集モード終了・全クリア時）。
    /// </summary>
    public void ClearHoverFill()
    {
        SetHoverFillZone(-1);
    }

    /// <summary>
    /// 地区IDに対応する識別色を返す。
    /// バックエンドが地区ごとに持つ color(HEX文字列 #RRGGBB) を Color に変換して返す。
    /// 地区が未ロード・色未設定・パース失敗の場合はシアンをフォールバックとする。
    /// 建物/区画/道路の「地区別色塗り」モードや地区枠線の描画で共通して使用する。
    /// </summary>
    public Color GetZoneColor(int zoneId)
    {
        if (zoneId > 0 && zoneCache.TryGetValue(zoneId, out ZoneData zone))
        {
            string hex = zone.color;
            if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out Color c))
            {
                return c;
            }
        }
        return new Color(0.2f, 0.8f, 1f, 1f); // Fallback to cyan
    }

    /// <summary>
    /// 地区識別色に任意の不透明度を設定して返す（地区枠線等の内部描画用）。
    /// </summary>
    private Color GetZoneColor(int zoneId, float alpha)
    {
        Color c = GetZoneColor(zoneId);
        c.a = alpha;
        return c;
    }

    public void UpdateZone(ZoneData zone)
    {
        float[] centerPoint = zone.GetCenterPointArray();
        zoneCache[zone.zone_id] = zone;

        // ゾーンポリゴンから BBOX（境界長方形）を計算してキャッシュする
        float[][] polygon = zone.GetPolygonArray();
        if (polygon != null && polygon.Length > 0)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var pt in polygon)
            {
                if (pt.Length >= 2)
                {
                    if (pt[0] < minX) minX = pt[0];
                    if (pt[0] > maxX) maxX = pt[0];
                    if (pt[1] < minZ) minZ = pt[1];
                    if (pt[1] > maxZ) maxZ = pt[1];
                }
            }
            zoneBboxCache[zone.zone_id] = new Rect(minX, minZ, maxX - minX, maxZ - minZ);
        }
        else
        {
            zoneBboxCache.Remove(zone.zone_id);
        }

        // 旧描画(枠線・ラベル・アイコン)は常に先に破棄する。
        // 拡張で侵食されて空になった地区(polygon=[], center_point=None)は下の早期リターンで
        // 再生成されないため、ここで消しておかないと古い枠線・ラベルが残り続ける(=消滅した地区の
        // ラベルが消えないバグになる)。
        RemoveZoneLines(zone.zone_id);

        if (zoneLabelUIs.ContainsKey(zone.zone_id))
        {
            Destroy(zoneLabelUIs[zone.zone_id]);
            zoneLabelUIs.Remove(zone.zone_id);
        }

        if (zoneIconUIs.ContainsKey(zone.zone_id))
        {
            foreach (var icon in zoneIconUIs[zone.zone_id])
            {
                if (icon != null) Destroy(icon);
            }
            zoneIconUIs.Remove(zone.zone_id);
        }

        // center_point が未設定のゾーン(空になった地区)は再描画しない。
        // 上で旧描画は破棄済みなので、枠線・ラベルは消えたままになる。
        if (centerPoint == null || centerPoint.Length < 2)
        {
            return;
        }

        zoneIconUIs[zone.zone_id] = new List<GameObject>();

        CreateZoneLines(zone);
        CreateZoneLabelUI(zone);
        CreateZoneIconsUI(zone);

        // 生成後、現在の表示モードを即座に適用する。
        int currentZoneMode = VisualModeHandler.Instance != null
            ? VisualModeHandler.Instance.GetMode(InputObjectType.ZONE)
            : VisualMode.MODE_NORMAL;
        ApplyVisualMode(currentZoneMode);
    }

    /// <summary>
    /// 2D UIキャンバス上に地区名テキストを生成する。
    /// 実際の位置更新は Update で毎フレーム行う。
    /// クリック可能にし、クリック時に zone_detail ウィンドウを開く IPC イベントを送信する。
    /// </summary>
    private void CreateZoneLabelUI(ZoneData zone)
    {
        EnsureUICanvas();
        if (uiCanvasRect == null) return;

        GameObject labelGO = new GameObject($"ZoneLabelUI_{zone.zone_id}");
        labelGO.transform.SetParent(uiCanvasRect, false);

        Text text = labelGO.AddComponent<Text>();
        text.text = zone.name;
        text.fontSize = 35;

        // 地区名は多言語を含むため、OSの多言語対応フォントを優先して使用する(UiFontProvider)。
        text.font = UiFontProvider.GetFont(text.fontSize);
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;

        // 読みやすさのためテキストに黒いアウトラインを付ける
        Outline outline = labelGO.AddComponent<Outline>();
        outline.effectColor = Color.black;
        outline.effectDistance = new Vector2(2.5f, 2.5f);

        RectTransform rt = text.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(500, 75);

        // クリック可能にして、クリック時に地区詳細ウィンドウを開く
        Button button = labelGO.AddComponent<Button>();
        int capturedZoneId = zone.zone_id;
        button.onClick.AddListener(() => OnZoneLabelClicked(capturedZoneId));

        zoneLabelUIs[zone.zone_id] = labelGO;
    }

    /// <summary>
    /// 2D UIキャンバス上に建物アイコンを生成する。
    /// 実際の位置更新は Update で毎フレーム行う。
    /// クリック可能にし、クリック時に zone_detail ウィンドウを開く IPC イベントを送信する。
    /// </summary>
    private void CreateZoneIconsUI(ZoneData zone)
    {
        EnsureUICanvas();
        if (uiCanvasRect == null) return;

        List<int> allowedBuildingTypes = GeneratedModelUtils.ExtractBuildingTypes(zone.allowed_bd_mask);
        if (allowedBuildingTypes == null || allowedBuildingTypes.Count == 0)
            return;

        if (BuildingIconFactory.Instance == null)
        {
            Debug.LogWarning("BuildingIconFactory.Instance が null です。アイコンを表示できません。");
            return;
        }

        var icons = new List<GameObject>();
        const float iconSize = 60f;
        int capturedZoneId = zone.zone_id;

        for (int i = 0; i < allowedBuildingTypes.Count; i++)
        {
            int buildingTypeId = allowedBuildingTypes[i];
            Sprite iconSprite = BuildingIconFactory.Instance.GetIcon(buildingTypeId);
            if (iconSprite == null)
            {
                Debug.LogWarning($"BuildingTypeId {buildingTypeId} のアイコンが見つかりません。");
                continue;
            }

            GameObject iconGO = new GameObject($"ZoneIconUI_{zone.zone_id}_{buildingTypeId}");
            iconGO.transform.SetParent(uiCanvasRect, false);

            Image image = iconGO.AddComponent<Image>();
            image.sprite = iconSprite;

            RectTransform rt = image.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(iconSize, iconSize);

            // クリック可能にして、クリック時に地区詳細ウィンドウを開く
            Button button = iconGO.AddComponent<Button>();
            button.onClick.AddListener(() => OnZoneLabelClicked(capturedZoneId));

            icons.Add(iconGO);
        }

        zoneIconUIs[zone.zone_id] = icons;
    }

    /// <summary>
    /// 地区名ラベルまたはアイコンがクリックされた時のハンドラ。
    /// Tauri UI に対して zone_detail ウィンドウを開くよう IPC イベントを送信する。
    /// </summary>
    private void OnZoneLabelClicked(int zoneId)
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitSelectObject("zone_detail", "zone", zoneId.ToString());
        }
    }

    /// <summary>
    /// ゾーンのアウトラインライン（枠線）をワールド座標付きで生成する。
    /// 隣接地区と線が重ならないよう、ポリゴンを OUTLINE_SETBACK だけ内側へオフセットしてから描く。
    /// 地形起伏に追従するため、長いセグメントは細分化して各端点の地面高さをサンプリングする。
    /// 実際の描画は OverlayMeshRenderer で行う。
    /// </summary>
    private void CreateZoneLines(ZoneData zone)
    {
        EnsureOverlayRenderer();

        float[][] polygon = zone.GetPolygonArray();
        if (polygon == null || polygon.Length < 3) return;

        Vector2[] outline = BuildSetbackOutline(polygon, OUTLINE_SETBACK);
        if (outline == null) return;

        // 1. 枠線の生成（地形起伏に対応するため、長いセグメントは分割）
        var entries = new List<ZoneLineEntry>();
        for (int i = 0; i < outline.Length; i++)
        {
            int nextI = (i + 1) % outline.Length;

            Vector2 p1 = outline[i];
            Vector2 p2 = outline[nextI];

            float dist = Vector2.Distance(p1, p2);
            const float maxSegmentLength = 5.0f;
            int segmentsCount = Mathf.Max(1, Mathf.FloorToInt(dist / maxSegmentLength));

            for (int j = 0; j < segmentsCount; j++)
            {
                float t1 = (float)j / segmentsCount;
                float t2 = (float)(j + 1) / segmentsCount;

                Vector2 subP1 = Vector2.Lerp(p1, p2, t1);
                Vector2 subP2 = Vector2.Lerp(p1, p2, t2);

                float y1 = Utils.GetGroundHeight(new Vector3(subP1.x, 1000f, subP1.y)) + 0.05f;
                float y2 = Utils.GetGroundHeight(new Vector3(subP2.x, 1000f, subP2.y)) + 0.05f;

                entries.Add(new ZoneLineEntry
                {
                    worldA = new Vector3(subP1.x, y1, subP1.y),
                    worldB = new Vector3(subP2.x, y2, subP2.y)
                });
            }
        }
        zoneLinesByZone[zone.zone_id] = entries;
    }

    /// <summary>
    /// 地区ポリゴン(XZ平面)を、指定量だけ内側へ平行オフセットした輪郭を返す。
    ///
    /// 各頂点を「前後の辺の内向き法線を平均した方向」へ、マイター長
    /// (= 1/dot(二等分方向, 辺の内向き法線)) を掛けた距離だけ動かす。これにより両辺が
    /// ちょうど setback だけ平行移動した位置で交わる(標準的なマイターオフセット)。
    /// 鋭角の頂点ではマイター長が発散するため OUTLINE_MITER_LIMIT で打ち切る。
    ///
    /// 細長い地区でオフセット量が幅を超えると輪郭が裏返る(自己交差する)が、地区は道路で
    /// 囲まれた面なのでセットバック(3m)に対して十分な幅がある前提とし、簡易オフセットに留める。
    /// </summary>
    private static Vector2[] BuildSetbackOutline(float[][] polygon, float setback)
    {
        int n = polygon.Length;
        var src = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float[] p = polygon[i];
            if (p == null || p.Length < 2) return null;
            src[i] = new Vector2(p[0], p[1]);
        }

        // 符号付き面積で巻き方向を判定する。内向きの法線は巻き方向で左右が入れ替わる。
        // 反時計回り(面積が正)なら、進行方向の左が内側。時計回りなら右が内側。
        float inwardSign = PolygonUtils.SignedArea(src) > 0f ? 1f : -1f;

        var result = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            Vector2 prev = src[(i - 1 + n) % n];
            Vector2 cur = src[i];
            Vector2 next = src[(i + 1) % n];

            Vector2 dirIn = (cur - prev);
            Vector2 dirOut = (next - cur);
            if (dirIn.sqrMagnitude < 1e-8f || dirOut.sqrMagnitude < 1e-8f)
            {
                result[i] = cur;
                continue;
            }
            dirIn.Normalize();
            dirOut.Normalize();

            // 進行方向に対する左法線 (-y, x)。inwardSign で内向きへ揃える。
            Vector2 nIn = new Vector2(-dirIn.y, dirIn.x) * inwardSign;
            Vector2 nOut = new Vector2(-dirOut.y, dirOut.x) * inwardSign;

            Vector2 bisector = nIn + nOut;
            if (bisector.sqrMagnitude < 1e-8f)
            {
                // 折り返し(180°)の頂点はマイターが定義できないので、片側の法線で逃がす
                result[i] = cur + nOut * setback;
                continue;
            }
            bisector.Normalize();

            // マイター長 = 1 / cos(半角) = 1 / dot(bisector, 法線)
            float cos = Vector2.Dot(bisector, nOut);
            float miter = Mathf.Abs(cos) < 1e-4f ? OUTLINE_MITER_LIMIT : 1f / cos;
            miter = Mathf.Clamp(miter, -OUTLINE_MITER_LIMIT, OUTLINE_MITER_LIMIT);

            result[i] = cur + bisector * (setback * miter);
        }
        return result;
    }

    private void RemoveZoneLines(int zoneId)
    {
        if (zoneLinesByZone.ContainsKey(zoneId))
        {
            zoneLinesByZone.Remove(zoneId);
        }
    }

    private void Update()
    {
        if (uiCanvasRoot == null || !uiCanvasRoot.activeSelf) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // カメラの位置・回転が閾値未満の変化しかない場合は、
        // 全ゾーンの WorldToScreenPoint と RectTransform 更新をスキップする。
        // 地区ラベルや枠線はカメラが 1m 未満動いたり 2° 未満回転した場合、
        // 視覚的に大きな変化はないため、毎フレーム更新する必要性が低い。
        Vector3 currentCamPos = cam.transform.position;
        Quaternion currentCamRot = cam.transform.rotation;
        bool posChanged = Vector3.Distance(currentCamPos, lastCamPos) > CAMERA_POS_THRESHOLD;
        bool rotChanged = Quaternion.Angle(currentCamRot, lastCamRot) > CAMERA_ROT_THRESHOLD;
        // カメラが動いていない場合、枠線の位置もラベルの位置も変わらないためスキップする
        // (枠線の太さ・選択地区の変化で描き直しが要るときは lastCamPos をリセットしてある)。
        if (!posChanged && !rotChanged)
        {
            return;
        }

        lastCamPos = currentCamPos;
        lastCamRot = currentCamRot;

        const float epsilon = 0.001f;
        Plane nearPlane = new Plane(
            cam.transform.forward,
            cam.transform.position + cam.transform.forward * (cam.nearClipPlane + epsilon));

        // 1. 枠線ラインの更新（OverlayMeshRenderer で3D空間メッシュとして一括描画）
        // 地区表示は「枠線＋名称ラベル＋アイコン」のみとし、ポリゴン板(塗りつぶし面)は描画しない。
        // 地区ごとの色分けは建物/区画/道路の各「地区別色塗り」モードで行う。
        if (overlayRenderer != null)
        {
            overlayRenderer.BeginFrame();

            // 枠線を描画。地区編集中は太く(選択中の地区はさらに太く)して境界を主役にする。
            foreach (var kvp in zoneLinesByZone)
            {
                int zoneId = kvp.Key;
                var entries = kvp.Value;
                Color outlineColor = GetZoneColor(zoneId, 1f); // 100% 不透明度
                float thickness = LINE_THICKNESS;
                if (editOutlineMode)
                {
                    thickness = (zoneId == selectedZoneId) ? SELECTED_LINE_THICKNESS : EDIT_LINE_THICKNESS;
                }
                for (int i = 0; i < entries.Count; i++)
                {
                    ZoneLineEntry entry = entries[i];

                    Vector3 ca = entry.worldA;
                    Vector3 cb = entry.worldB;

                    float da = nearPlane.GetDistanceToPoint(ca);
                    float db = nearPlane.GetDistanceToPoint(cb);

                    if (da < 0f && db < 0f)
                        continue;

                    if (da < 0f || db < 0f)
                    {
                        Vector3 dir = cb - ca;
                        float t = da / (da - db);
                        Vector3 p = ca + dir * t;
                        if (da < 0f) ca = p; else cb = p;
                    }

                    overlayRenderer.AddSolidLine(ca, cb, outlineColor, thickness);
                }
            }

            overlayRenderer.EndFrame(cam);
        }

        // 2. 地区名ラベルとアイコンの位置更新
        foreach (var kvp in zoneCache)
        {
            int zoneId = kvp.Key;
            ZoneData zone = kvp.Value;
            float[] centerPoint = zone.GetCenterPointArray();
            if (centerPoint == null || centerPoint.Length < 2) continue;

            float groundY = Utils.GetGroundHeight(new Vector3(centerPoint[0], 1000f, centerPoint[1]));
            Vector3 worldPos = new Vector3(centerPoint[0], groundY + 2f, centerPoint[1]);
            Vector3 screenPos = cam.WorldToScreenPoint(worldPos);
            bool isBehind = screenPos.z < 0;

            // ラベル
            if (zoneLabelUIs.TryGetValue(zoneId, out GameObject labelObj) && labelObj != null)
            {
                labelObj.SetActive(!isBehind);
                if (!isBehind)
                {
                    RectTransform rt = labelObj.GetComponent<RectTransform>();
                    rt.anchoredPosition = new Vector2(screenPos.x, screenPos.y);
                }
            }

            // アイコン
            if (zoneIconUIs.TryGetValue(zoneId, out List<GameObject> iconList) && iconList != null)
            {
                for (int i = 0; i < iconList.Count; i++)
                {
                    GameObject iconObj = iconList[i];
                    if (iconObj == null) continue;
                    iconObj.SetActive(!isBehind);
                    if (!isBehind)
                    {
                        RectTransform rt = iconObj.GetComponent<RectTransform>();
                        float offsetX = (i - (iconList.Count - 1) / 2f) * 70f;
                        rt.anchoredPosition = new Vector2(screenPos.x + offsetX, screenPos.y - 62.5f);
                    }
                }
            }
        }
    }

    private void OnDestroy()
    {
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.UnregisterObject(InputObjectType.ZONE, this);
        }
        if (terrainManager != null)
        {
            terrainManager.OnAllTerrainLodLoaded -= OnTerrainAllLoaded;
        }
    }

    /// <summary>
    /// TerrainManager が全セルのロードを完了した時に呼ばれる。
    /// ゾーンの枠線ラインの高さを地形に合わせて再計算する。
    /// (updatedCells 引数はイベントのシグネチャ整合のためで、地区枠線は全件再計算するため未使用)
    /// </summary>
    private void OnTerrainAllLoaded(IReadOnlyList<Vector2Int> updatedCells)
    {
        RefreshZoneLineHeights();
    }

    /// <summary>
    /// 全ゾーンの枠線ラインのワールド座標高さを、現在の地形高さに基づいて更新する。
    /// ZoneManager は地形に依存しないタイミングでロードされるため、
    /// 初回生成時に Terrain が未準備だと高さ 0 になってしまう。
    /// そのため地形ロード完了後に本メソッドを呼び、正しい高さに補正する。
    /// </summary>
    private void RefreshZoneLineHeights()
    {
        foreach (var kvp in zoneLinesByZone)
        {
            var entries = kvp.Value;
            for (int i = 0; i < entries.Count; i++)
            {
                ZoneLineEntry entry = entries[i];

                Vector3 wa = entry.worldA;
                Vector3 wb = entry.worldB;

                float newY1 = Utils.GetGroundHeight(new Vector3(wa.x, 1000f, wa.z)) + 0.05f;
                float newY2 = Utils.GetGroundHeight(new Vector3(wb.x, 1000f, wb.z)) + 0.05f;

                entry.worldA = new Vector3(wa.x, newY1, wa.z);
                entry.worldB = new Vector3(wb.x, newY2, wb.z);
            }
        }
    }

    public void ApplyVisualMode(int mode)
    {
        if (uiCanvasRoot == null) return;

        bool isVisible = mode != VisualMode.MODE_HIDDEN;
        uiCanvasRoot.SetActive(isVisible);

        if (overlayMeshObj != null)
        {
            overlayMeshObj.SetActive(isVisible);
        }

        // 地区表示をONにした時点でゾーンが未ロードなら、表示操作を契機に全ゾーンを取得する。
        // 通常はカメラセル更新でロードされるが、表示パネルだけを操作した場合でも空のCanvasにならないようにする。
        if (isVisible && !areZonesLoaded && !isZoneLoadInProgress)
        {
            RenderWorkScheduler.Start(this, LoadAllZones(), "ZoneManager");
        }
    }
}

[Serializable]
public class ZonesListResponse
{
    public List<GeneratedModels.ZoneData> zones;
}

[Serializable]
public class ZoneSingleResponse
{
    public GeneratedModels.ZoneData zone;
}
