using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using GeneratedModels;

/// <summary>
/// StationPlanManager:
/// 軌道建設パネルの「簡易モード → 駅タブ」専用の配置エディタ。プラットフォーム(複線の相対式)と
/// 駅舎をひとつのパッケージとして、1クリックで置けるようにする。
///
/// 通常の敷設(RoadPlanManager)と違い、プレイヤーが折れ線を引くことはない。指定できるのは
/// 「ホーム長(両数)・高さ・角度・規格(プラットフォーム/駅舎)」だけで、形状は次のように固定される。
///   - プラットフォームは中心線の左右に線路間隔の半分ずつ振り分けた平行2線(相対式=ホームは外側)。
///   - 駅舎は中心線と直交する1本の直線で、両プラットフォームを跨ぐ長さを自動で取る。
///   - 3本とも直線。プラットフォームは中心点の地盤標高+指定の高さで水平に揃え(駅は共通標高)、
///     駅舎の線は地面に沿わせる(連絡橋のデッキ高さはプレハブの造形が持つため)。
///
/// 操作は建物の自由配置・プロップの個別配置と同じ(共通部品 Editors/PlacementInput.cs)。
///   - カーソル追従のゴースト → 左クリックで地点を固定 → パネルの確定ボタンで発注。
///   - 中ボタンドラッグで回転、PageUp/PageDown で高さ、Esc で地点の固定解除。
///
/// 確定送信(POST /api/v1/paths のプラットフォーム2本 + 駅舎1本の一括作成)は UI(Tauri)側が
/// 直接行う。本マネージャは形状の組み立て・プレビュー描画・UIへの状態通知
/// (edit_station:placement_state)のみを担う(公共交通設置・プロップ配置と同じ責務分担)。
/// 干渉・線形の検証も UI 側の POST /paths/validate が権威で、ここでは行わない。
///
/// IpcStateCoordinator が STATE_EDIT_STATION 遷移時に動的生成・初期化する。シーンへの事前配置は不要。
/// </summary>
public class StationPlanManager : MonoBehaviour
{
    // PageUp/PageDown 1回あたりの高さ変化(m)。パネルのステッパー刻みと同じ。Shift併用で1m。
    private const float HeightKeyStep = 0.1f;
    private const float HeightKeyShiftStep = 1.0f;

    /// <summary>
    /// 駅舎の線を、プラットフォームの外縁からさらに外へ伸ばす余裕(m)。
    /// 駅舎の建屋は線路に重なると外側へスライドする(バックエンド geo_station_house と同一の
    /// アルゴリズムを StationHousePath が持つ)が、スライドはまだ存在しない敷設中の線路を
    /// 見られないため、プレビューと確定後で建屋の位置がずれてしまう。最初からホームの外側で
    /// 終わる長さにしておけばスライドが起きず、両者が一致する。
    /// </summary>
    private const float HOUSE_LINE_EXTRA_CLEARANCE = 1.0f;

    // 敷設設定(UI が真実源。placement_command で部分更新される)
    private int platformVariant;
    private int houseVariant;
    private float platformLength = 80f;
    private float trackSpacing = 5f;
    private float height;
    private float angle;
    private string houseEnds = "both";
    private string passageMode = "elevated";
    private int[] travelDirections = System.Array.Empty<int>();

    // 配置状態。fixedCenter が null の間はカーソル追従のゴースト、固定後は確定待ち。
    private bool active;
    private Vector3? fixedCenter;
    private Vector3 hoverCenter;
    private bool hasHover;
    /// <summary>形状の世代。UI は最後に送ったコマンドへの応答かどうかの照合に使う。</summary>
    private int revision;

    private readonly MiddleDragRotation middleDrag = new MiddleDragRotation();
    private readonly KeyRepeat heightUpKey = new KeyRepeat(Key.PageUp);
    private readonly KeyRepeat heightDownKey = new KeyRepeat(Key.PageDown);

    // プレビュー(プラットフォーム2本+駅舎1本)のルート。形状が変わるたびに作り直す。
    private GameObject previewRoot;
    private readonly List<Mesh> ownedMeshes = new List<Mesh>();
    private string lastPreviewKey;

    /// <summary>
    /// カーソル追従中のプレビュー再生成の最短間隔(秒)。ゴーストは線路2本+駅舎の実メッシュを
    /// 作り直すので、マウス移動のたびに再生成するとフレームが落ちる。位置は常に最新の
    /// ホバー座標を使い、作り直す頻度だけを間引く。
    /// </summary>
    private const float HOVER_REBUILD_INTERVAL = 0.1f;
    private float nextHoverRebuildTime;
    private bool hoverPreviewDirty;

    // =====================================================================
    // パブリックAPI: モード遷移
    // =====================================================================

    /// <summary>
    /// 駅パッケージ配置モードを初期化する。STATE_EDIT_STATION 遷移時に呼ばれる。
    /// 敷設設定は引き継がず、UI から渡された値でやり直す(連続設置は地点の固定解除で行う)。
    /// </summary>
    public void InitializeStationPlacement(int platformVariant, int houseVariant, float platformLength,
        float trackSpacing, float height, float angle, int[] travelDirections, string houseEnds, string passageMode = "elevated")
    {
        this.platformVariant = platformVariant;
        this.houseVariant = houseVariant;
        this.platformLength = Mathf.Max(1f, platformLength);
        this.trackSpacing = Mathf.Max(0.1f, trackSpacing);
        this.height = height;
        this.angle = Mathf.Repeat(angle, 360f);
        this.travelDirections = travelDirections ?? System.Array.Empty<int>();
        this.houseEnds = NormalizeHouseEnds(houseEnds);
        this.passageMode = passageMode;
        fixedCenter = null;
        hasHover = false;
        hoverPreviewDirty = false;
        middleDrag.Cancel();
        active = true;
        ClearPreview();
        EmitPlacementState();

        Debug.Log($"StationPlanManager: 駅配置モード初期化 platform={platformVariant}, house={houseVariant}, length={platformLength}");
    }

    /// <summary>配置モードを終了し、プレビューを片付ける。</summary>
    public void FinishPlacement()
    {
        active = false;
        fixedCenter = null;
        hasHover = false;
        hoverPreviewDirty = false;
        middleDrag.Cancel();
        ClearPreview();
    }

    /// <summary>
    /// UI からの設定変更(edit_station:placement_command)。送られてきたキーだけを反映する。
    /// reset=true は「地点の固定だけを解除」(寸法・角度・高さは維持)で、確定後の連続設置と
    /// パネルのキャンセルボタンに使う。何も含まないコマンドは状態の問い合わせになる。
    /// </summary>
    public void ApplyPlacementCommand(Dictionary<string, object> command)
    {
        if (!active || command == null)
        {
            EmitPlacementState();
            return;
        }

        if (command.TryGetValue("platform_variant", out object pvObj) && pvObj != null)
            platformVariant = System.Convert.ToInt32(pvObj);
        if (command.TryGetValue("house_variant", out object hvObj) && hvObj != null)
            houseVariant = System.Convert.ToInt32(hvObj);
        if (command.TryGetValue("platform_length", out object plObj) && plObj != null)
            platformLength = Mathf.Max(1f, System.Convert.ToSingle(plObj));
        if (command.TryGetValue("track_spacing", out object tsObj) && tsObj != null)
            trackSpacing = Mathf.Max(0.1f, System.Convert.ToSingle(tsObj));
        if (command.TryGetValue("height", out object hObj) && hObj != null)
            height = System.Convert.ToSingle(hObj);
        if (command.TryGetValue("angle", out object aObj) && aObj != null)
            angle = Mathf.Repeat(System.Convert.ToSingle(aObj), 360f);
        if (command.TryGetValue("house_ends", out object heObj) && heObj != null)
            houseEnds = NormalizeHouseEnds(System.Convert.ToString(heObj));
        if (command.TryGetValue("passage_mode", out object pmObj) && pmObj != null)
            passageMode = System.Convert.ToString(pmObj);
        if (command.TryGetValue("travel_directions", out object tdObj) && tdObj != null)
            travelDirections = ReadIntArray(tdObj);
        if (command.TryGetValue("reset", out object rObj) && rObj != null && System.Convert.ToBoolean(rObj))
            fixedCenter = null;

        RefreshPreview();
        EmitPlacementState();
    }

    // =====================================================================
    // 入力(TerrainInputHandler / Update から)
    // =====================================================================

    /// <summary>
    /// 中ボタンドラッグで回転、PageUp/PageDown で高さ、Esc で地点の固定解除。
    /// いずれも建物の自由配置・プロップの個別配置と同じ操作(PlacementInput 共用)。
    /// </summary>
    private void Update()
    {
        if (!active || PlacementInput.IsBlocked())
        {
            // カメラ操作が始まった等でドラッグが中断されたら、途中までの角度をUIへ返す。
            if (middleDrag.IsDragging) { middleDrag.Cancel(); EmitPlacementState(); }
            return;
        }

        switch (middleDrag.Update(out int deltaDegrees))
        {
            case MiddleDragRotation.Result.Rotated:
                angle = Mathf.Repeat(angle + deltaDegrees, 360f);
                RefreshPreview();
                break;
            case MiddleDragRotation.Result.Released:
                EmitPlacementState();
                break;
        }
        if (middleDrag.IsDragging) return;

        // ホバーの間引きで見送った位置を、間隔が空いたフレームで反映する。
        if (hoverPreviewDirty && Time.unscaledTime >= nextHoverRebuildTime)
        {
            nextHoverRebuildTime = Time.unscaledTime + HOVER_REBUILD_INTERVAL;
            hoverPreviewDirty = false;
            RefreshPreview();
        }

        if (InputUtil.KeyWasPressedThisFrame(Key.Escape) && fixedCenter.HasValue)
        {
            fixedCenter = null;
            RefreshPreview();
            EmitPlacementState();
            return;
        }

        float step = PlacementInput.ShiftHeld ? HeightKeyShiftStep : HeightKeyStep;
        float delta = 0f;
        if (heightUpKey.Fired()) delta += step;
        if (heightDownKey.Fired()) delta -= step;
        if (delta == 0f) return;
        // 0.1m刻みの累積で浮動小数の端数が溜まらないよう cm 単位へ丸め、規格の下限でクランプする。
        height = Mathf.Max(Mathf.Round((height + delta) * 100f) / 100f, MinHeightOf(platformVariant));
        RefreshPreview();
        EmitPlacementState();
    }

    /// <summary>左クリックで配置地点を固定する(確定はパネルの確定ボタン)。</summary>
    public void OnTerrainLeftClick(Vector3 worldPos)
    {
        if (!active) return;
        // 中ボタン回転中の左クリックは無視する(回転の途中で誤って地点を確定しないため)。
        if (middleDrag.IsDragging) return;
        fixedCenter = worldPos;
        hoverCenter = worldPos;
        hasHover = true;
        RefreshPreview();
        EmitPlacementState();
    }

    /// <summary>右クリックで地点の固定を解除する(Escと同じ。置き直しの導線)。</summary>
    public void OnTerrainRightClick(Vector3 worldPos)
    {
        if (!active || !fixedCenter.HasValue) return;
        fixedCenter = null;
        RefreshPreview();
        EmitPlacementState();
    }

    /// <summary>カーソル追従のゴースト位置。地点の固定後・回転ドラッグ中は動かさない。</summary>
    public void OnTerrainHover(Vector3 worldPos)
    {
        if (!active || fixedCenter.HasValue) return;
        if (middleDrag.IsDragging) return;
        hoverCenter = worldPos;
        hasHover = true;
        // 位置は毎回取り込みつつ、実メッシュの作り直しは一定間隔に間引く。
        if (Time.unscaledTime < nextHoverRebuildTime)
        {
            hoverPreviewDirty = true;
            return;
        }
        nextHoverRebuildTime = Time.unscaledTime + HOVER_REBUILD_INTERVAL;
        hoverPreviewDirty = false;
        RefreshPreview();
    }

    // =====================================================================
    // 形状の組み立て
    // =====================================================================

    /// <summary>現在の中心点(固定済みならその点、未固定ならカーソル位置)。未ホバーなら null。</summary>
    private Vector3? CurrentCenter => fixedCenter ?? (hasHover ? hoverCenter : (Vector3?)null);

    /// <summary>
    /// 中心点・角度・ホーム長・線路間隔から、プラットフォーム2本と駅舎1本の中心線(絶対座標)を作る。
    /// 角度は北(ワールド+Z)を0とする時計回りの度で、プラットフォームはこの方向に伸び、
    /// 駅舎はこれと直交する方向に伸びる。
    ///
    /// プラットフォームは中心点の地盤標高+指定高さで**水平に**揃える(駅は共通標高。確定送信の
    /// align_station_tracks でバックエンドも同じ扱いにする)。駅舎の線は逆に**地形へ追従**させる
    /// (連絡橋のデッキ高さ・橋脚は駅舎プレハブの造形が持ち、線そのものは地面に置く規約のため)。
    /// </summary>
    private bool TryBuildGeometry(out List<List<Vector3>> platforms, out List<Vector3> house)
    {
        platforms = null;
        house = null;
        Vector3? centerOpt = CurrentCenter;
        if (centerOpt == null || platformVariant <= 0) return false;

        Vector3 center = centerOpt.Value;
        float rad = angle * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        Vector3 right = new Vector3(dir.z, 0f, -dir.x);

        float trackAbsY = Utils.GetGroundHeight(center) + height;
        float half = platformLength * 0.5f;
        float[] offsets = { -trackSpacing * 0.5f, trackSpacing * 0.5f };
        platforms = new List<List<Vector3>>(offsets.Length);
        foreach (float offset in offsets)
        {
            Vector3 lineCenter = center + right * offset;
            platforms.Add(new List<Vector3>
            {
                new Vector3(lineCenter.x - dir.x * half, trackAbsY, lineCenter.z - dir.z * half),
                new Vector3(lineCenter.x + dir.x * half, trackAbsY, lineCenter.z + dir.z * half),
            });
        }

        // 駅舎は高さを編集できない規格(橋上・地下とも連絡橋の高さはプレハブ側が持つ)なので、
        // 相対高さは0のまま地面へ置く。編集可能な規格が現れたらプラットフォームと同じ高さにする。
        float houseRelY = IsHeightEditable(houseVariant) ? height : 0f;
        float houseHalf = HouseHalfLength();
        house = new List<Vector3>();
        foreach (float sign in new[] { -1f, 1f })
        {
            var point = new Vector3(center.x + right.x * houseHalf * sign, 0f, center.z + right.z * houseHalf * sign);
            point.y = Utils.GetGroundHeight(point) + houseRelY;
            house.Add(point);
        }
        return true;
    }

    /// <summary>
    /// 駅舎の線の半長(m)。複線の外側の線路の、さらに外へ張り出したホームの外縁まで届かせる。
    /// = 線路間隔の半分 + 線路コリドーの半分 + プラットフォーム幅 + クリアランス余白 + 余裕。
    /// </summary>
    private float HouseHalfLength()
    {
        var master = GetMaster(platformVariant);
        float trackWidth = master != null ? master.width : 0f;
        float platformWidth = master != null && master.platform_width.HasValue ? master.platform_width.Value : 0f;
        return trackSpacing * 0.5f + trackWidth * 0.5f + platformWidth
            + Constants.STATION_HOUSE_TRACK_CLEARANCE_MARGIN + HOUSE_LINE_EXTRA_CLEARANCE;
    }

    // =====================================================================
    // プレビュー描画
    // =====================================================================

    /// <summary>
    /// プラットフォーム2本と駅舎1本のプレビューメッシュを作り直す。実パスと同じ経路
    /// (PathMeshFactory へ仮の PathSummary を渡す)で生成するため、確定後と同じ見た目になる。
    /// </summary>
    private void RefreshPreview()
    {
        if (!active) return;
        if (!TryBuildGeometry(out var platforms, out var house))
        {
            ClearPreview();
            return;
        }

        string key = BuildPreviewKey(platforms, house);
        if (key == lastPreviewKey && previewRoot != null) return;

        ClearPreview();
        previewRoot = new GameObject("StationPlacementPreview");
        Material material = ResolvePreviewMaterial();

        int platformType = GetMaster(platformVariant)?.type ?? Constants.PATH_TRACK;
        for (int i = 0; i < platforms.Count; i++)
        {
            int direction = (travelDirections != null && i < travelDirections.Length)
                ? travelDirections[i] : Constants.DIR_BOTH;
            AddComponentPreview(platforms[i], platformType, platformVariant, direction,
                Constants.PLATFORM_SIDE_OUTSIDE, "both", material, designed: true);
        }
        if (houseVariant > 0)
        {
            // 駅舎は設計標高を渡さず地形追従のまま描く(連絡橋は各アンカーを地面に置く規約)。
            AddComponentPreview(house, GetMaster(houseVariant)?.type ?? Constants.PATH_STATION_HOUSE,
                houseVariant, Constants.DIR_BOTH, Constants.PLATFORM_SIDE_OUTSIDE, houseEnds, material,
                designed: false);
        }
        lastPreviewKey = key;
    }

    /// <summary>
    /// 1本ぶんのプレビューメッシュを生成して previewRoot 配下へぶら下げる。
    /// designed=true では生成器が地形へ再投影しないよう follow_terrain=false +
    /// station_grade_profile を渡す(RoadRenderer の造成済みプレビューと同じ扱い。
    /// プラットフォームを水平に保つため)。
    /// </summary>
    private void AddComponentPreview(List<Vector3> points, int pathType, int pathVariant,
        int travelDirection, int platformSide, string ends, Material material, bool designed)
    {
        Vector3 start = points[0];
        Vector3 end = points[points.Count - 1];
        float startRelY = start.y - Utils.GetGroundHeight(start);
        float endRelY = end.y - Utils.GetGroundHeight(end);

        var tempPathData = new PathSummary
        {
            path_type = pathType,
            path_variant = pathVariant,
            waypoints = new List<object>(),
            curved = false,
            platform_side = platformSide,
            house_ends = ends,
            passage_mode = passageMode,
            travel_direction = travelDirection,
            start_point = new List<object> { (object)start.x, (object)startRelY, (object)start.z },
            end_point = new List<object> { (object)end.x, (object)endRelY, (object)end.z },
        };
        if (designed)
        {
            tempPathData.follow_terrain = false;
            var gradeProfile = new List<object>();
            foreach (var p in points) gradeProfile.Add(new List<object> { p.x, p.y, p.z });
            tempPathData.station_grade_profile = gradeProfile;
        }

        PathMeshResult result = PathMeshFactory.GeneratePathMesh(tempPathData, points);
        if (result == null) return;
        // 生成器が返す帯はクリック用のコライダーメッシュ。表示には使わないが寿命はここが持つ。
        if (result.Mesh != null) ownedMeshes.Add(result.Mesh);
        if (result.GeneratedObject == null) return;

        GameObject obj = result.GeneratedObject;
        obj.transform.SetParent(previewRoot.transform, false);
        if (material == null) return;
        foreach (var renderer in obj.GetComponentsInChildren<Renderer>(true))
        {
            renderer.sharedMaterial = material;
        }
    }

    /// <summary>
    /// 敷設プレビュー用の半透明マテリアル。シーンに常駐する RoadPlanManager が持つものを
    /// 借りる(このマネージャは動的生成のためインスペクタ参照を持てない)。
    /// </summary>
    private Material ResolvePreviewMaterial()
    {
        var roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        return roadPlanManager != null ? roadPlanManager.previewMaterial : null;
    }

    private void ClearPreview()
    {
        if (previewRoot != null)
        {
            previewRoot.SetActive(false);
            Destroy(previewRoot);
            previewRoot = null;
        }
        foreach (var mesh in ownedMeshes) if (mesh != null) Destroy(mesh);
        ownedMeshes.Clear();
        lastPreviewKey = null;
    }

    /// <summary>同一形状での破棄→再生成のスラッシングを防ぐための重複判定キー。</summary>
    private string BuildPreviewKey(List<List<Vector3>> platforms, List<Vector3> house)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(platformVariant).Append('/').Append(houseVariant).Append('/').Append(houseEnds).Append('/').Append(passageMode).Append('/');
        foreach (var line in platforms)
            foreach (var p in line) sb.Append(p.x.ToString("F2")).Append(',').Append(p.y.ToString("F2")).Append(',').Append(p.z.ToString("F2")).Append(';');
        foreach (var p in house) sb.Append(p.x.ToString("F2")).Append(',').Append(p.y.ToString("F2")).Append(',').Append(p.z.ToString("F2")).Append(';');
        if (travelDirections != null) foreach (int d in travelDirections) sb.Append(d).Append(',');
        return sb.ToString();
    }

    // =====================================================================
    // IPC 送出
    // =====================================================================

    /// <summary>
    /// 現在の配置状態を UI へ通知する。position が null の間はカーソル追従中(確定できない)。
    /// platforms / house は確定送信にそのまま使える中心線([始点, 終点] の [x, 相対高さ, z])で、
    /// UI 側で幾何計算を二重に持たないためにここで解決して渡す。
    /// ホバー移動では送らない(MMFを埋めないため。建物直接配置の placement_state と同じ方針)。
    /// </summary>
    private void EmitPlacementState()
    {
        if (IpcClient.Instance == null) return;
        revision++;

        var payload = new Dictionary<string, object>
        {
            ["revision"] = revision,
            ["angle"] = angle,
            ["height"] = height,
            ["platform_length"] = platformLength,
            ["track_spacing"] = trackSpacing,
            ["platform_variant"] = platformVariant,
            ["house_variant"] = houseVariant,
            ["house_ends"] = houseEnds,
            ["passage_mode"] = passageMode,
            ["position"] = null,
            ["platforms"] = null,
            ["house"] = null,
        };

        if (fixedCenter.HasValue && TryBuildGeometry(out var platforms, out var house))
        {
            Vector3 center = fixedCenter.Value;
            payload["position"] = new List<float> { center.x, center.z };
            var platformPayload = new List<List<List<float>>>(platforms.Count);
            foreach (var line in platforms) platformPayload.Add(ToRelativeLine(line));
            payload["platforms"] = platformPayload;
            payload["house"] = ToRelativeLine(house);
        }

        IpcClient.Instance.EmitEditStationPlacementState(payload);
    }

    /// <summary>絶対座標の中心線を、確定送信と同じ [x, 相対高さ, z] の始点・終点へ変換する。</summary>
    private static List<List<float>> ToRelativeLine(List<Vector3> line)
    {
        var result = new List<List<float>>(2);
        foreach (int index in new[] { 0, line.Count - 1 })
        {
            Vector3 p = line[index];
            result.Add(new List<float> { p.x, p.y - Utils.GetGroundHeight(p), p.z });
        }
        return result;
    }

    // =====================================================================
    // ヘルパ
    // =====================================================================

    private static PathMasterItem GetMaster(int variant)
    {
        if (MasterDataStore.Instance == null || variant <= 0) return null;
        try { return MasterDataStore.Instance.GetPathMaster(variant); }
        catch { return null; }
    }

    private static bool IsHeightEditable(int variant)
    {
        var master = GetMaster(variant);
        return master != null && master.height_editable.HasValue && master.height_editable.Value;
    }

    private static float MinHeightOf(int variant)
    {
        var master = GetMaster(variant);
        return master != null && master.min_height.HasValue ? master.min_height.Value : 0f;
    }

    private static string NormalizeHouseEnds(string ends)
    {
        return (ends == "start" || ends == "end") ? ends : "both";
    }

    private static int[] ReadIntArray(object value)
    {
        if (value is Newtonsoft.Json.Linq.JArray array)
        {
            var result = new int[array.Count];
            for (int i = 0; i < array.Count; i++) result[i] = array[i].ToObject<int>();
            return result;
        }
        if (value is IEnumerable<object> list)
        {
            var result = new List<int>();
            foreach (var item in list) result.Add(System.Convert.ToInt32(item));
            return result.ToArray();
        }
        return System.Array.Empty<int>();
    }

    private void OnDestroy()
    {
        ClearPreview();
    }
}
