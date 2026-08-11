using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 各オブジェクトがVisualModeHandlerからの通知を受け取り、表示状態を更新するコンポーネント
/// </summary>
using TMPro;

public class VisualModeObject : MonoBehaviour, IVisualModeHandler
{
    /// <summary>
    /// 耐久度0.0の色（#ff4444）。UI凡例の meterStartColor と一致させる。
    /// </summary>
    private static readonly Color DurabilityMinColor = new Color(1f, 0.267f, 0.267f);

    [Tooltip("このオブジェクトの種類(InputObjectType)を指定します")]
    public int objectType;

    [Tooltip("ハイライト表示に使用するマテリアル")]
    [SerializeField] private Material highlightMaterial;

    [Tooltip("pendingステータス用の赤いマテリアル")]
    [SerializeField] private Material pendingMaterial;

    [Tooltip("plannedステータス用の灰色マテリアル")]
    [SerializeField] private Material plannedMaterial;

    [Tooltip("constructionステータス用の黄色いマテリアル")]
    [SerializeField] private Material constructionMaterial;

    [Tooltip("プレビュー表示用のマテリアル")]
    [SerializeField] private Material previewMaterial;

    [Tooltip("所属地区色分け用マテリアル（4色）")]
    [SerializeField] private Material[] zoneMaterials = new Material[4];

    [Tooltip("道路の交通量・耐久度など数値ベース色分けに使用する白色ベースマテリアル")]
    [SerializeField] private Material pathColorMaterial;

    [Tooltip("建物の数値ベース色分けに使用する白色ベースマテリアル。未設定の場合はResourcesから読み込む。")]
    [SerializeField] private Material buildingColorMaterial;

    [Tooltip("建物のフロア色分けモードで、対象フロアを持たない建物の半透明表示に使用するマテリアル。未設定の場合はResourcesから読み込む。")]
    [SerializeField] private Material buildingColorFadeMaterial;

    [Tooltip("このオブジェクトのマテリアルを一切変更しない（表示/非表示のみ制御する）")]
    public bool preventMaterialChange = false;

    [Header("Renderer Configuration")]
    [Tooltip("子オブジェクトのRendererも対象にするか")]
    public bool useChildRenderers = false;
    [Tooltip("自分自身のRendererを対象外にするか")]
    public bool ignoreSelfRenderer = false;

    // Rendererと元のマテリアルを保持するクラス
    private class RendererInfo
    {
        public Renderer renderer;
        public Material[] originalMaterials;
        public bool preventMaterialChange;

        public RendererInfo(Renderer r, bool preventChange)
        {
            renderer = r;
            originalMaterials = r.sharedMaterials;
            preventMaterialChange = preventChange;
        }
    }

    private List<RendererInfo> targetRenderers = new List<RendererInfo>();
    private int objectStatus = Constants.STATUS_ACTIVE; // オブジェクトのステータス

    private bool isHovered = false;
    private bool isPreview = false; // プレビューモードかどうか
    private int tempZoneId = 0; // ドラッグ中の一時的な地区ID（ゾーン色分けプレビュー用）
    // パス干渉ハイライトの色。値があるとプレビュー以外の全表示に優先して塗る。
    // HighlightManager が計画中パスの干渉オブジェクト（建物・既存パス）へ設定する。
    private Color? interferenceColor = null;

    private void Awake()
    {
        InitializeMaterials();
    }

    private void Start()
    {
        // デフォルトマテリアルを設定（リソースから読み込み）
        LoadDefaultMaterials();

        // VisualModeHandlerに自動登録
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.RegisterObject(objectType, this);
        }
    }

    private void OnDestroy()
    {
        // VisualModeHandlerから自動登録解除
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.UnregisterObject(objectType, this);
        }
    }

    /// <summary>
    /// マテリアル情報の初期化・再取得
    /// </summary>
    public void InitializeMaterials()
    {
        targetRenderers.Clear();

        // 自分自身
        if (!ignoreSelfRenderer)
        {
            var selfRenderer = GetComponent<Renderer>();
            if (selfRenderer != null)
            {
                bool prevent = ShouldPreventMaterialChange(selfRenderer);
                targetRenderers.Add(new RendererInfo(selfRenderer, prevent));
            }
        }

        // 子オブジェクト
        if (useChildRenderers)
        {
            var childRenderers = GetComponentsInChildren<Renderer>(true);
            foreach (var r in childRenderers)
            {
                if (r.gameObject == this.gameObject) continue;
                bool prevent = ShouldPreventMaterialChange(r);
                targetRenderers.Add(new RendererInfo(r, prevent));
            }
        }
    }

    private bool ShouldPreventMaterialChange(Renderer r)
    {
        if (r is SpriteRenderer) return true;
        if (r.GetComponent<TextMeshPro>() != null) return true;
        // 一方通行線路の進行方向矢印はオーバーレイ表示なので、ホバーハイライトや
        // 計画中の青塗りで色を上書きせず固有色を維持する（表示/非表示は本体側の制御に従う）。
        if (r.GetComponent<TrackDirectionArrows>() != null) return true;
        return false;
    }

    private void LoadDefaultMaterials()
    {
        if (highlightMaterial == null) highlightMaterial = Resources.Load<Material>("HighlightMaterial");
        if (pendingMaterial == null) pendingMaterial = Resources.Load<Material>("PendingMaterial");
        if (plannedMaterial == null) plannedMaterial = Resources.Load<Material>("PlannedMaterial");
        if (constructionMaterial == null) constructionMaterial = Resources.Load<Material>("ConstructionMaterial");
        if (previewMaterial == null) previewMaterial = Resources.Load<Material>("PreviewBuildingMaterial");

        // 所属地区色分け用マテリアルをロード
        for (int i = 0; i < 4; i++)
        {
            if (zoneMaterials[i] == null)
            {
                zoneMaterials[i] = Resources.Load<Material>($"ZoneMaterial{i + 1}");
            }
        }

        // 道路の数値ベース色分け用マテリアル
        if (pathColorMaterial == null) pathColorMaterial = Resources.Load<Material>("PathColorMaterial");

        // 建物の数値ベース色分け用マテリアル。
        // Inspectorで未設定の場合はResourcesから読み込む。
        if (buildingColorMaterial == null)
        {
            buildingColorMaterial = Resources.Load<Material>("BuildingColorMaterial");
        }
        // Resourcesにも無い場合はフォールバックとしてBuildingMaterialを使用する。
        // BuildingMaterialはDepthOffsetSimpleLitシェーダを使用しているため、
        // 建物と同じZ-fighting対策が適用される。
        if (buildingColorMaterial == null)
        {
            buildingColorMaterial = Resources.Load<Material>("BuildingMaterial");
        }

        // 建物のフロア色分けモードで、対象フロアを持たない建物の半透明表示用マテリアル。
        if (buildingColorFadeMaterial == null)
        {
            buildingColorFadeMaterial = Resources.Load<Material>("BuildingColorFadeMaterial");
        }

        // フォールバック
        if (previewMaterial == null) previewMaterial = Resources.Load<Material>("BuildingMaterial");
    }

    public void SetStatus(int status)
    {
        objectStatus = status;
        UpdateVisuals();
    }

    /// <summary>
    /// パス干渉ハイライトの色を設定する。null で解除。
    /// 計画中パスが干渉する建物・既存パスを、HighlightManager が警告色で塗るために使う。
    /// プレビュー以外のあらゆる表示（ホバー・ステータス・データ可視化）に優先する。
    /// </summary>
    public void SetInterferenceHighlight(Color? color)
    {
        // Color? の等価比較で無駄な再描画を避ける
        if (interferenceColor == color) return;
        interferenceColor = color;
        UpdateVisuals();
    }

    /// <summary>
    /// ホバー状態を設定する
    /// </summary>
    public void SetHover(bool hover)
    {
        if (isHovered != hover)
        {
            isHovered = hover;
            UpdateVisuals();
        }
    }

    /// <summary>
    /// ドラッグ中の一時的な地区IDを設定する。
    /// ZONE_COLORモード時に、この値が優先されて表示される。
    /// </summary>
    public void SetTempZoneId(int zoneId)
    {
        if (tempZoneId != zoneId)
        {
            tempZoneId = zoneId;
            UpdateVisuals();
        }
    }

    /// <summary>
    /// 一時的な地区IDをクリアし、元の表示に戻す。
    /// </summary>
    public void ClearTempZoneId()
    {
        if (tempZoneId != 0)
        {
            tempZoneId = 0;
            UpdateVisuals();
        }
    }

    // IVisualModeHandlerの実装
    public void ApplyVisualMode(int mode)
    {
        // 現在のモードを保持して更新
        UpdateVisuals(mode);
    }

    /// <summary>
    /// 現在の状態に基づいて表示を更新する
    /// </summary>
    private void UpdateVisuals(int? modeOverride = null)
    {
        if (targetRenderers == null || targetRenderers.Count == 0) return;

        // モードとフラグの取得。
        // 色モード(currentMode)は排他1択、表示フラグ(flags)は色モードと独立したON/OFF集合。
        int currentMode = modeOverride ?? (VisualModeHandler.Instance != null ? VisualModeHandler.Instance.GetMode(objectType) : VisualMode.MODE_NORMAL);
        int flags = VisualModeHandler.Instance != null ? VisualModeHandler.Instance.GetFlags(objectType) : VisualFlag.SHOW_PLAN;

        // 1. プレビューモードの処理（最優先）
        if (isPreview)
        {
            SetRenderersEnabled(true);
            if (!preventMaterialChange) ApplyMaterialToAllSubmeshes(previewMaterial);
            return;
        }

        // 1.5. パス干渉ハイライト（プレビュー以外の全表示に優先）。
        // 計画中パスが干渉する建物・既存パスを警告色で塗り、他の色分けを上書きする。
        if (interferenceColor.HasValue && !preventMaterialChange)
        {
            SetRenderersEnabled(true);
            if (objectType == InputObjectType.BUILDING)
            {
                ApplyBuildingColorOverlay(interferenceColor.Value);
            }
            else
            {
                ApplyColorToRenderers(interferenceColor.Value);
            }
            return;
        }

        // 2. 建設中/解体中/倒壊ステータスの処理
        // これらは常に表示する。メッシュ自体が専用の表示
        // （建設中/解体中=土マウンド/土ストリップ + 足場 + 建機 + 柱 + 坑口、
        //   倒壊=瓦礫マウンド + 倒壊片）なので、色モードの上塗りは行わず
        // オリジナルの見た目をそのまま出す（色分けで塗り潰すと現場/瓦礫の表現が消える）。
        // ホバー中は通常ステータスと同様にハイライトする。
        if (objectStatus == Constants.STATUS_CONSTRUCTING
            || objectStatus == Constants.STATUS_DEMOLISHING
            || objectStatus == Constants.STATUS_COLLAPSED
            || objectStatus == Constants.STATUS_COLLAPSED_FIRE)
        {
            SetRenderersEnabled(true);
            if (!preventMaterialChange)
            {
                if (isHovered)
                {
                    ApplyMaterialToAllSubmeshes(highlightMaterial);
                }
                else
                {
                    RestoreOriginalMaterials();
                }
            }
            return;
        }

        // 3. 計画中（Pending/Planned）ステータスの処理
        // 計画中は不透明の単一マテリアルで塗る。半透明にすると建物の形状が読み取れないため、
        // 深度書き込みと影を有効にした灰色マテリアルで立体感を保つ。
        if (objectStatus == Constants.STATUS_TEMPORARY || objectStatus == Constants.STATUS_SCHEDULED_CONSTRUCTION || objectStatus == Constants.STATUS_PENDING)
        {
            // SHOW_PLAN フラグが立っている時のみ表示する。
            // ただし色モードが非表示のときは計画中も含めて全て隠す。
            if ((flags & VisualFlag.SHOW_PLAN) != 0 && currentMode != VisualMode.MODE_HIDDEN)
            {
                SetRenderersEnabled(true);
                EnableCollider(true);

                if (!preventMaterialChange)
                {
                    Material targetMat = (objectStatus == Constants.STATUS_TEMPORARY) ? pendingMaterial : plannedMaterial;
                    ApplyMaterialToAllSubmeshes(targetMat);
                }
            }
            else
            {
                SetRenderersEnabled(false);
                EnableCollider(false);
            }
            return;
        }

        // 4. 通常ステータス(ACTIVE)の処理
        // 公共施設ハイライト(VisualFlag.HIGHLIGHT_PUBLIC)はここでは扱わない。
        // 建物のマテリアルではなく敷地の土地区画形状に光を描く独立オーバーレイ
        // (PublicFacilityGlowOverlay)が担当するため、どの色モードとも共存する。
        EnableCollider(true);

        switch (currentMode)
        {
            case VisualMode.MODE_HIDDEN:
                SetRenderersEnabled(false);
                EnableCollider(false);
                break;

            case VisualMode.MODE_BOUNDARY:
                if (objectType == InputObjectType.PARCEL)
                {
                    SetRenderersEnabled(false);
                }
                else
                {
                    SetRenderersEnabled(true);
                    if (!preventMaterialChange) ApplyMaterialToAllSubmeshes(highlightMaterial);
                }
                break;

            case VisualMode.MODE_HIGHLIGHT_OWNER:
                SetRenderersEnabled(true);
                if (!preventMaterialChange) ApplyMaterialToAllSubmeshes(highlightMaterial);
                break;

            case VisualModePath.ZONE_COLOR:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.PATH)
                {
                    ApplyZoneColorToPath();
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            case VisualModeBuilding.ZONE_COLOR:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.BUILDING)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        int zid = ResolveEffectiveZoneId();
                        if (zid > 0 && ZoneManager.Instance != null)
                        {
                            // 建物が所属する地区の識別色で塗りつぶす。
                            ApplyBuildingColorOverlay(ZoneManager.Instance.GetZoneColor(zid));
                        }
                        else
                        {
                            RestoreOriginalMaterials();
                        }
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            // --- 道路の数値ベース色分けモード ---
            // すべて VisualPathStats から値を読み取り、Lerp で色を決定して塗りつぶす。
            // ホバー中は highlightMaterial を優先する（通常モードと同じ挙動）。

            case VisualModePath.UTILIZATION:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.PATH)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        var stats = GetComponent<VisualPathStats>();
                        float value = stats != null ? Mathf.Clamp01(stats.utilization) : 0f;
                        ApplyColorToRenderers(Color.Lerp(Color.white, Color.red, value));
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            case VisualModePath.SPEED:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.PATH)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        var stats = GetComponent<VisualPathStats>();
                        float value = stats != null ? Mathf.Clamp01(stats.effective_speed / 100f) : 0f;
                        ApplyColorToRenderers(Color.Lerp(Color.red, Color.green, value));
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            case VisualModePath.PEDESTRIAN:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.PATH)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        var stats = GetComponent<VisualPathStats>();
                        float value = stats != null ? Mathf.Clamp01(stats.pedestrian_traffic / 100f) : 0f;
                        ApplyColorToRenderers(Color.Lerp(Color.gray, Color.blue, value));
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            case VisualModePath.DURABILITY:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.PATH)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        var stats = GetComponent<VisualPathStats>();
                        float value = stats != null ? Mathf.Clamp01(stats.durability) : 0f;
                        // 耐久度: 0→赤(#ff4444), 1→白(#ffffff)
                        ApplyColorToRenderers(Color.Lerp(DurabilityMinColor, Color.white, value));
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            // --- 建物の数値ベース色分けモード ---
            // すべて VisualBuildingObject から値を読み取り、Lerp で色を決定して塗りつぶす。
            // ホバー中は highlightMaterial を優先する（通常モードと同じ挙動）。

            case VisualModeBuilding.RENT:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.BUILDING)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        var buildingStatsRent = GetComponent<VisualBuildingObject>();
                        Color? rentColor = buildingStatsRent != null ? buildingStatsRent.GetOverlayColor(currentMode) : null;
                        if (rentColor.HasValue)
                        {
                            ApplyBuildingColorOverlay(rentColor.Value);
                        }
                        else
                        {
                            RestoreOriginalMaterials();
                        }
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            case VisualModeBuilding.DURABILITY:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.BUILDING)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        var buildingStatsDur = GetComponent<VisualBuildingObject>();
                        Color? durColor = buildingStatsDur != null ? buildingStatsDur.GetOverlayColor(currentMode) : null;
                        if (durColor.HasValue)
                        {
                            ApplyBuildingColorOverlay(durColor.Value);
                        }
                        else
                        {
                            RestoreOriginalMaterials();
                        }
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            // --- 建物のフロア種別色分けモード ---
            // 指定したスペース種別の容量を持つ建物をその種別色で塗りつぶし、
            // 持たない建物を半透明で表示する。
            // ホバー中は highlightMaterial を優先する。

            case VisualModeBuilding.SPACE_OTHER:
            case VisualModeBuilding.SPACE_RESIDENCE:
            case VisualModeBuilding.SPACE_RETAIL:
            case VisualModeBuilding.SPACE_FACTORY:
            case VisualModeBuilding.SPACE_OFFICE:
            case VisualModeBuilding.SPACE_FARM:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.BUILDING)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        int spaceType = VisualModeToSpaceType(currentMode);
                        var buildingStats = GetComponent<VisualBuildingObject>();
                        bool hasSpace = buildingStats != null && buildingStats.HasSpaceType(spaceType);

                        if (hasSpace)
                        {
                            // 対象スペースを持つ建物: 種別色で塗りつぶす
                            Color spaceColor = GetSpaceTypeColor(spaceType);
                            ApplyBuildingColorOverlay(spaceColor);
                        }
                        else
                        {
                            // 対象スペースを持たない建物: 半透明で表示する
                            Color fadedColor = new Color(0.7f, 0.7f, 0.7f, 0.4f);
                            ApplyBuildingColorOverlay(fadedColor, isSemiTransparent: true);
                        }
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            case VisualMode.MODE_NORMAL:
            default:
                SetRenderersEnabled(true);

                if (!preventMaterialChange)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        RestoreOriginalMaterials();
                    }
                }
                break;
        }
    }

    private void SetRenderersEnabled(bool enabled)
    {
        foreach (var info in targetRenderers)
        {
            if (info.renderer != null) info.renderer.enabled = enabled;
        }
    }

    /// <summary>
    /// 指定したマテリアルを全サブメッシュに適用する
    /// </summary>
    /// <param name="mat">適用するマテリアル。nullの場合はオリジナルを使用</param>
    /// <param name="useOriginalIfNull">matがnullの場合にオリジナルに戻すかどうか</param>
    private void ApplyMaterialToAllSubmeshes(Material mat, bool useOriginalIfNull = false)
    {
        if (mat == null && useOriginalIfNull)
        {
            RestoreOriginalMaterials();
            return;
        }

        if (mat == null) return;

        foreach (var info in targetRenderers)
        {
            if (info.renderer == null) continue;
            if (info.preventMaterialChange) continue;

            int count = info.originalMaterials != null ? info.originalMaterials.Length : 1;
            Material[] newMaterials = new Material[count];
            for (int i = 0; i < count; i++)
            {
                newMaterials[i] = mat;
            }
            info.renderer.sharedMaterials = newMaterials;
        }
    }

    /// <summary>
    /// オリジナルのマテリアルに戻す。
    /// MaterialPropertyBlockも併せてクリアする。
    /// </summary>
    private void RestoreOriginalMaterials()
    {
        foreach (var info in targetRenderers)
        {
            if (info.renderer != null && info.originalMaterials != null)
            {
                info.renderer.sharedMaterials = info.originalMaterials;
                info.renderer.SetPropertyBlock(null);
            }
        }
    }

    /// <summary>
    /// 建物の色オーバーレイを適用する（不透明版）。
    /// buildingColorMaterial（白色不透明マテリアル）を全サブメッシュに適用したあと、
    /// MaterialPropertyBlock で per-object の _BaseColor と _Color を設定する。
    /// </summary>
    private void ApplyBuildingColorOverlay(Color color)
    {
        ApplyBuildingColorOverlay(color, isSemiTransparent: false);
    }

    /// <summary>
    /// 建物の色オーバーレイを適用する。
    /// isSemiTransparent が false の場合は buildingColorMaterial（不透明白）をベースにし、
    /// true の場合は buildingColorFadeMaterial（半透明）をベースにして塗りつぶす。
    /// どちらも MaterialPropertyBlock で per-object の _BaseColor と _Color を設定し、
    /// マテリアルのインスタンス化を抑止しつつ正しい色を描画できる。
    /// </summary>
    private void ApplyBuildingColorOverlay(Color color, bool isSemiTransparent)
    {
        Material baseMat = isSemiTransparent ? buildingColorFadeMaterial : buildingColorMaterial;

        if (baseMat != null)
        {
            ApplyMaterialToAllSubmeshes(baseMat);
        }

        MaterialPropertyBlock props = new MaterialPropertyBlock();
        props.SetColor("_BaseColor", color);
        // 後方互換性: 古いシェーダが _Color を使用する場合に備えて設定する
        props.SetColor("_Color", color);

        foreach (var info in targetRenderers)
        {
            if (info.renderer == null || info.preventMaterialChange) continue;

            if (baseMat != null)
            {
                info.renderer.SetPropertyBlock(props);
            }
            else
            {
                // フォールバック: ベースマテリアルが無い場合は
                // 既存マテリアルの色を直接上書きする
                int count = info.originalMaterials != null ? info.originalMaterials.Length : 0;
                if (count > 0)
                {
                    Material[] mats = info.renderer.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] != null) mats[i].color = color;
                    }
                    info.renderer.sharedMaterials = mats;
                }
            }
        }
    }

    /// <summary>
    /// 道路の数値ベース色分け用の描画メソッド。
    /// pathColorMaterial を全サブメッシュに適用したあと、
    /// MaterialPropertyBlock で per-object の色を設定する。
    /// URPシェーダでは _Color ではなく _BaseColor が描画に使用されるため、
    /// 両方のプロパティを設定して後方互換性を確保する。
    /// pathColorMaterial が未設定の場合はフォールバックとして
    /// オリジナルマテリアルの色を直接変更する。
    /// </summary>
    private void ApplyColorToRenderers(Color color)
    {
        MaterialPropertyBlock props = new MaterialPropertyBlock();
        props.SetColor("_BaseColor", color);
        props.SetColor("_Color", color);

        if (pathColorMaterial != null)
        {
            ApplyMaterialToAllSubmeshes(pathColorMaterial);
        }

        foreach (var info in targetRenderers)
        {
            if (info.renderer == null || info.preventMaterialChange) continue;

            if (pathColorMaterial != null)
            {
                info.renderer.SetPropertyBlock(props);
            }
            else
            {
                int count = info.originalMaterials != null ? info.originalMaterials.Length : 0;
                if (count > 0)
                {
                    Material[] mats = info.renderer.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] != null) mats[i].color = color;
                    }
                    info.renderer.sharedMaterials = mats;
                }
            }
        }
    }

    /// <summary>
    /// 「地区別色塗り」モードで使用する有効な地区IDを解決する。
    /// ドラッグ中の一時地区ID(tempZoneId)が最優先。未設定の場合は
    /// 道路(VisualPathStats)または建物(VisualBuildingStats)の zone_id を読む。
    /// </summary>
    private int ResolveEffectiveZoneId()
    {
        int zid = tempZoneId;
        if (zid == 0)
        {
            var pathStats = GetComponent<VisualPathStats>();
            if (pathStats != null) zid = pathStats.zone_id;
        }
        if (zid == 0)
        {
            var bldStats = GetComponent<VisualBuildingStats>();
            if (bldStats != null) zid = bldStats.zone_id;
        }
        return zid;
    }

    /// <summary>
    /// 道路に地区識別色を適用する。
    /// 通常時はバックエンドの zone.color を道路のベース白色マテリアル + PropertyBlock で塗る。
    /// 地区編集中のドラッグプレビュー(PREVIEW_SENTINEL)は未確定のため、
    /// 編集コンテキストのプレビュー用パレット色(zoneMaterials)を使う。
    /// 所属地区がない道路はオリジナルマテリアルに戻す。
    /// </summary>
    private void ApplyZoneColorToPath()
    {
        int effectiveZoneId = ResolveEffectiveZoneId();

        if (effectiveZoneId == ZoneEditContext.PREVIEW_SENTINEL)
        {
            var ctx = ZoneEditContext.Instance;
            int colorIdx = ctx != null ? ctx.PreviewColorIndex : 0;
            colorIdx = Mathf.Clamp(colorIdx, 0, zoneMaterials.Length - 1);
            Material zoneMat = zoneMaterials[colorIdx];
            ApplyMaterialToAllSubmeshes(zoneMat != null ? zoneMat : null, true);
        }
        else if (effectiveZoneId > 0 && ZoneManager.Instance != null)
        {
            Color zoneColor = ZoneManager.Instance.GetZoneColor(effectiveZoneId);
            ApplyColorToRenderers(zoneColor);
        }
        else
        {
            RestoreOriginalMaterials();
        }
    }

    private void EnableCollider(bool enabled)
    {
        var col = GetComponent<Collider>();
        if (col != null)
        {
            col.enabled = enabled;
        }
    }

    /// <summary>
    /// VisualModeBuilding のフロア種別モード定数を、
    /// Constants の SPACE_* 定数に変換する。
    /// VisualModeBuilding.SPACE_* は 303..308 の連番。
    /// Constants.SPACE_* は 2の累乗（1, 2, 4, 8, 16, 32）。
    /// オフセットからビット位置を求め、1 をシフトして定数値を得る。
    /// </summary>
    private static int VisualModeToSpaceType(int mode)
    {
        int bitPosition = mode - VisualModeBuilding.SPACE_OTHER;
        if (bitPosition < 0 || bitPosition > 5) return Constants.SPACE_OTHER;
        return 1 << bitPosition;
    }

    /// <summary>
    /// スペース種別IDに対応する色を LocalConstants.SPACE_TYPE_COLOR_MAP から取得する。
    /// マップに無い種別の場合はグレーを返す。
    /// </summary>
    private static Color GetSpaceTypeColor(int spaceType)
    {
        if (LocalConstants.SPACE_TYPE_COLOR_MAP.ContainsKey(spaceType))
        {
            return LocalConstants.SPACE_TYPE_COLOR_MAP[spaceType];
        }
        return new Color(0.7f, 0.7f, 0.7f);
    }
}