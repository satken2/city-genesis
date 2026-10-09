using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// PropPlacementManager:
/// 地形プロップ(森・岩・茂みなどの装飾)の配置エディタ。TransitPlanManager / ResourceAreaEditManager と
/// 似た操作感で、3Dマップ上にプロップを配置する。配置には2つのモードがある。
///
///   個別モード(instance): 左クリックで1個ずつ配置する(PropInstance)。カーソル追従のゴーストは
///     半透明、左クリックで確定すると不透明の実モデルになる。右クリックで直近の配置を取り消す。
///     回転角・スケール・高さは UI から設定され、edit_prop:change_settings で逐次反映される。
///     3D側でも建物の直接配置と同じく中ボタンドラッグで回転、PageUp/PageDown で高さを変えられ、
///     その結果は edit_prop:placement_settings でパネルのステッパーへ書き戻す(次に置くゴーストの設定を
///     変えるだけで、打点済みの仮配置は動かさない)。
///     種別を切り替えても仮配置を保持し、各インスタンスに配置時の種別を記録する。
///
///   範囲モード(scatter): 左クリックで散布範囲のポリゴン頂点を置く(PropScatter)。頂点が3つ以上に
///     なると seed ベースの散布点を生成し、prefab を半透明ゴーストで簡易プレビューする。密度・seed の
///     変更で再生成する。右クリックで直近の頂点を取り消す。
///
/// 確定送信(POST /api/v1/props/instances | /scatters)は UI(Tauri)側が直接行う。本マネージャは
/// 配置データの組み立てと UI への逐次通知(edit_prop:update_instances / edit_prop:update_shape)、
/// および配置プレビューの描画のみを担う(公共交通設置・資源編集と同じ責務分担)。
///
/// IpcStateCoordinator が STATE_EDIT_PROP 遷移時に動的生成・初期化する。シーンへの事前配置は不要。
/// </summary>
public class PropPlacementManager : MonoBehaviour
{
    private enum Mode { Instance, Scatter }


    // 散布ポリゴン(範囲モード)のギズモ描画パラメータ(ピクセル単位)。
    private const float VERTEX_MARKER_HALF_SIZE = 6f;
    private const float LINE_THICKNESS = 2.5f;

    private static readonly Color LineColor = new Color(0.4f, 0.9f, 0.5f, 0.95f);
    private static readonly Color VertexColor = new Color(1f, 1f, 1f, 1f);
    private static readonly Color PreviewColor = new Color(0.4f, 0.9f, 0.5f, 0.5f);

    // PageUp/PageDown 1回あたりの高さ変化(m)。パネルのステッパー刻みと同じ。Shift併用で1m。
    private const float HeightKeyStep = 0.1f;
    private const float HeightKeyShiftStep = 1.0f;

    private Mode mode = Mode.Instance;
    private bool shapeFinalized = false;
    private int propType = 0;
    private float rotation = 0f;
    private float scale = 1f;
    private float height = 0f;
    private float density = 400f;
    private int seed = 0;

    private bool active = false;

    // 個別モードの3D側操作(建物の直接配置と共通の PlacementInput.cs)。
    private readonly MiddleDragRotation middleDrag = new MiddleDragRotation();
    private readonly KeyRepeat heightUpKey = new KeyRepeat(Key.PageUp);
    private readonly KeyRepeat heightDownKey = new KeyRepeat(Key.PageDown);

    // 配置した prop の親。まとめて破棄できるよう1つの GameObject 配下に置く。
    private Transform container;

    // ---- 個別モード ----
    // 仮配置のインスタンス(不透明)。GameObject と配置データを対で保持する。
    private readonly List<GameObject> placedObjects = new List<GameObject>();
    private readonly List<(int propType, float x, float y, float z, float rotation, float scale)> placedInstances
        = new List<(int, float, float, float, float, float)>();
    // カーソル追従の半透明ゴースト(個別モードのみ)。
    private GameObject hoverGhost;
    private bool hoverValid = false;
    private Vector3 hoverPos;

    // ---- 範囲モード ----
    private readonly List<Vector3> scatterVertices = new List<Vector3>();
    private sealed class PreviewBatch
    {
        public PropRenderModel.Part part;
        public Matrix4x4[] matrices;
    }
    private readonly List<PreviewBatch> previewBatches = new List<PreviewBatch>();
    private PropRenderModel previewModel;
    private Coroutine previewWorker;
    private int previewRevision;
    private Vector3 previewOffset;
    private OverlayMeshRenderer overlayRenderer;
    private GameObject overlayMeshObj;

    private Camera gameCamera;
    private Material previewMaterial;

    // =====================================================================
    // ライフサイクル
    // =====================================================================

    private void Awake()
    {
        gameCamera = Camera.main;
        // 建物建設のプレビューと同じ半透明マテリアルを流用する(alpha≈0.39)。
        var sourceMaterial = Resources.Load<Material>("PreviewBuildingMaterial");
        if (sourceMaterial != null) previewMaterial = CreatePreviewMaterial(sourceMaterial);
        EnsureContainer();
        EnsureOverlayRenderer();
    }

    /// <summary>
    /// 個別モードの3D側操作。中ボタンドラッグで回転(ドラッグ中はゴーストを即時回転し、リリース時に
    /// 確定角度をUIへ通知)、PageUp/PageDown で地面からの高さを増減する(変更のたびにUIへ通知)。
    /// どちらも「次に置くゴースト」の設定を変えるだけで、打点済みの仮配置は動かさない。
    /// </summary>
    private void Update()
    {
        if (!active || mode != Mode.Instance || PlacementInput.IsBlocked())
        {
            // ドラッグ中にモードが外れた・カメラ操作が始まった等の場合は状態を落とし、途中までの角度をUIへ返す。
            if (middleDrag.IsDragging) { middleDrag.Cancel(); EmitPlacementSettings(); }
            return;
        }

        switch (middleDrag.Update(out int deltaDegrees))
        {
            case MiddleDragRotation.Result.Rotated:
                rotation = Mathf.Repeat(rotation + deltaDegrees, 360f);
                RefreshHoverGhost();
                break;
            case MiddleDragRotation.Result.Released:
                EmitPlacementSettings();
                break;
        }
        if (middleDrag.IsDragging) return;

        float step = PlacementInput.ShiftHeld ? HeightKeyShiftStep : HeightKeyStep;
        float heightDelta = 0f;
        if (heightUpKey.Fired()) heightDelta += step;
        if (heightDownKey.Fired()) heightDelta -= step;
        if (heightDelta == 0f) return;
        // 0.1m刻みの累積で浮動小数の端数が溜まらないよう cm 単位へ丸める。
        height = Mathf.Round((height + heightDelta) * 100f) / 100f;
        RefreshHoverGhost();
        EmitPlacementSettings();
    }

    private void LateUpdate()
    {
        if (!active || mode != Mode.Scatter) return;
        UpdateOverlay();
        var manager = PropManager.Instance;
        if (manager == null) return;
        if (previewRevision != manager.PreviewRevision || previewOffset != manager.SceneOffset) RegenerateScatterPreview();
        if (previewMaterial == null) return;
        foreach (var batch in previewBatches)
            Graphics.RenderMeshInstanced(new RenderParams(previewMaterial) { camera = gameCamera,
                shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off, receiveShadows = false },
                batch.part.mesh, batch.part.submesh, batch.matrices);
    }

    private void EnsureContainer()
    {
        if (container != null) return;
        var go = new GameObject("PropPlacementContainer");
        container = go.transform;
        container.SetParent(transform, false);
    }

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj != null) return;
        // 散布範囲の頂点・辺は地形に埋もれず常に手前に描く(他エディタのギズモと同じ)。
        overlayRenderer = OverlayMeshRenderer.Create("PropEditorOverlayMesh", alwaysOnTop: true);
        overlayMeshObj = overlayRenderer.gameObject;
    }

    // =====================================================================
    // パブリックAPI: モード遷移
    // =====================================================================

    /// <summary>
    /// プロップ配置モードを初期化する。placementMode は "instance" | "scatter"。
    /// 個別モードでは rotation / scale / height、範囲モードでは density / seed を初期設定として使う。
    /// </summary>
    public void InitializePlacement(string placementMode, int propType, float rotation, float scale, float density, int seed, float height = 0f)
    {
        if (gameCamera == null) gameCamera = Camera.main;
        EnsureContainer();
        EnsureOverlayRenderer();
        if (previewMaterial == null)
        {
            var sourceMaterial = Resources.Load<Material>("PreviewBuildingMaterial");
            if (sourceMaterial != null) previewMaterial = CreatePreviewMaterial(sourceMaterial);
        }

        // 個別モード内の種別変更は打点済みの配置を保持し、次に置くゴーストだけを作り直す。
        bool preserveInstances = active && mode == Mode.Instance && placementMode == "instance";
        if (preserveInstances) ClearHoverGhost();
        else ClearAll();

        mode = (placementMode == "scatter") ? Mode.Scatter : Mode.Instance;
        this.propType = propType;
        this.rotation = rotation;
        this.scale = scale;
        this.height = height;
        this.density = density;
        this.seed = seed;
        this.shapeFinalized = false;
        active = true;

        // 種別切替後も全種類を含む仮配置を UI に通知する。
        if (mode == Mode.Instance) EmitInstances();
        else EmitShape();

        Debug.Log($"PropPlacementManager: 配置モード初期化 mode={placementMode}, prop_type={propType}");
    }

    /// <summary>
    /// 配置モードを終了し、状態とプレビューをすべてクリアする。
    /// </summary>
    public void FinishPlacement()
    {
        active = false;
        ClearAll();
        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
    }

    /// <summary>
    /// UI からの設定変更(edit_prop:change_settings)。送られてきたキーのみ反映する。
    /// 個別モードは rotation / scale / height をゴーストに、範囲モードは density / seed を散布プレビューに反映する。
    /// </summary>
    public void OnChangeSettingsFromIpc(Dictionary<string, object> settings)
    {
        if (settings == null) return;

        bool scatterDirty = false;
        if (settings.TryGetValue("rotation", out object rObj) && rObj != null)
            rotation = System.Convert.ToSingle(rObj);
        if (settings.TryGetValue("scale", out object sObj) && sObj != null)
            scale = System.Convert.ToSingle(sObj);
        if (settings.TryGetValue("height", out object hObj) && hObj != null)
            height = System.Convert.ToSingle(hObj);
        if (settings.TryGetValue("density", out object dObj) && dObj != null)
        {
            density = System.Convert.ToSingle(dObj);
            scatterDirty = true;
        }
        if (settings.TryGetValue("seed", out object seedObj) && seedObj != null)
        {
            seed = System.Convert.ToInt32(seedObj);
            scatterDirty = true;
        }

        if (mode == Mode.Instance)
        {
            RefreshHoverGhost();
        }
        else if (scatterDirty)
        {
            RegenerateScatterPreview();
        }
    }

    /// <summary>ホバーゴーストに現在の回転/スケール/高さを即反映する(ゴースト未生成なら何もしない)。</summary>
    private void RefreshHoverGhost()
    {
        if (hoverGhost != null && hoverValid)
        {
            ApplyTransform(hoverGhost, hoverPos, rotation, scale);
        }
    }

    /// <summary>3D側の操作で変わった回転角・高さをパネルへ書き戻す(パネルはこれをUnityへエコーしない)。</summary>
    private void EmitPlacementSettings()
    {
        IpcClient.Instance?.EmitEditPropPlacementSettings(rotation, height);
    }

    // =====================================================================
    // 入力ハンドラ(TerrainInputHandler から呼ばれる)
    // =====================================================================

    public void OnTerrainLeftClick(Vector3 worldPos)
    {
        if (!active) return;

        if (mode == Mode.Instance)
        {
            // 中ボタン回転中の左クリックは配置しない(回転操作の途中で誤って打点しないため)。
            if (middleDrag.IsDragging) return;

            // その場に不透明の実モデルを確定配置する。
            GameObject go = InstantiateProp(worldPos, rotation, scale, ghost: false);
            if (go != null)
            {
                placedObjects.Add(go);
                Vector3 offset = PropManager.Instance != null ? PropManager.Instance.SceneOffset : Vector3.zero;
                placedInstances.Add((propType, worldPos.x - offset.x, height, worldPos.z - offset.z, rotation, scale));
                EmitInstances();
            }
        }
        else
        {
            // すでに仮確定されている場合は頂点を追加しない。
            if (shapeFinalized) return;

            // 散布範囲のポリゴン頂点を追加する。
            scatterVertices.Add(worldPos);
            EmitShape();
            if (scatterVertices.Count >= 3)
            {
                RegenerateScatterPreview();
            }
        }
    }

    public void OnTerrainRightClick(Vector3 worldPos)
    {
        if (!active) return;

        if (mode == Mode.Instance)
        {
            // 直近の配置を取り消す(誤配置の巻き戻し。既存プロップの削除は別機能)。
            if (placedObjects.Count > 0)
            {
                int last = placedObjects.Count - 1;
                if (placedObjects[last] != null) Destroy(placedObjects[last]);
                placedObjects.RemoveAt(last);
                placedInstances.RemoveAt(last);
                EmitInstances();
            }
        }
        else
        {
            // 右クリックで形状を仮確定する。
            // 頂点が3つ以上あれば仮確定状態へ遷移。
            if (scatterVertices.Count >= 3)
            {
                shapeFinalized = true;
                EmitShape();
                RegenerateScatterPreview();
            }
        }
    }

    public void OnTerrainHover(Vector3 worldPos)
    {
        if (!active) return;

        if (mode == Mode.Instance)
        {
            // 中ボタン回転中はゴーストの位置を固定する(回転操作でカーソルが横に動いても滑らないようアンカーする)。
            if (middleDrag.IsDragging && hoverGhost != null) return;

            hoverValid = true;
            hoverPos = worldPos;
            if (hoverGhost == null)
            {
                hoverGhost = InstantiateProp(worldPos, rotation, scale, ghost: true);
            }
            else
            {
                ApplyTransform(hoverGhost, worldPos, rotation, scale);
                hoverGhost.SetActive(true);
            }
        }
        else
        {
            // 仮確定済みの場合はホバープレビュー線を非表示にするため、無効化する。
            if (shapeFinalized)
            {
                hoverValid = false;
                return;
            }

            // 範囲モードのホバーは Placing プレビュー線用に位置だけ覚えておく。
            hoverValid = true;
            hoverPos = worldPos;
        }
    }

    // =====================================================================
    // プロップ生成
    // =====================================================================

    /// <summary>
    /// prop_type から prefab を読み込んで配置する。ghost=true の場合は全 Renderer を
    /// 半透明プレビューマテリアルで上書きする。読み込みに失敗した場合は null を返す。
    /// </summary>
    private GameObject InstantiateProp(Vector3 worldPos, float rot, float scl, bool ghost)
    {
        GameObject prefab = GetPrefab();
        if (prefab == null) return null;

        GameObject go = Instantiate(prefab, container);
        go.SetActive(true);
        ApplyTransform(go, worldPos, rot, scl);

        if (ghost && previewMaterial != null)
        {
            ApplyGhostMaterial(go);
        }
        return go;
    }

    /// <summary>prop_type ごとの prefab を ModelManager 経由で読み込む(Resources/Models/Props/{id})。</summary>
    private GameObject GetPrefab()
    {
        if (ModelManager.Instance == null) return null;
        return ModelManager.Instance.GetModel($"Models/Props/{propType}");
    }

    /// <summary>地形標高に指定高さを加える。重なりや種別で配置位置を補正しない。</summary>
    private void ApplyTransform(GameObject go, Vector3 worldPos, float rot, float scl)
    {
        worldPos.y = Utils.GetGroundHeight(worldPos) + height;
        go.transform.position = worldPos;
        go.transform.rotation = Quaternion.Euler(0f, rot, 0f);
        go.transform.localScale = Vector3.one * scl;
    }

    /// <summary>配置前のゴースト表示のため、全子 Renderer を半透明マテリアルで上書きする。</summary>
    private void ApplyGhostMaterial(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            var mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = previewMaterial;
            r.sharedMaterials = mats;
        }
    }

    // =====================================================================
    // 散布プレビュー生成(範囲モード)
    // =====================================================================

    /// <summary>永続表示と同じ座標ハッシュ・小区画・障害物判定を使う。</summary>
    private void RegenerateScatterPreview()
    {
        ClearScatterGhosts();
        var manager = PropManager.Instance;
        if (manager == null) return;
        previewRevision = manager.PreviewRevision;
        previewOffset = manager.SceneOffset;
        if (scatterVertices.Count < 3 || density <= 0) return;
        previewModel = new PropRenderModel(propType);
        previewWorker = StartCoroutine(BuildScatterPreview(manager));
    }

    private System.Collections.IEnumerator BuildScatterPreview(PropManager manager)
    {
        var ring = scatterVertices.ConvertAll(v => new Vector2(v.x - previewOffset.x, v.z - previewOffset.z)).ToArray();
        var region = new PropGeometry.Region { bounds = PropGeometry.Bounds(ring) };
        region.rings.Add(ring);
        var regions = new List<PropGeometry.Region> { region };
        int candidates = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var rect in manager.PreviewRects(region.bounds))
        {
            var matrices = new List<Matrix4x4>();
            foreach (var point in PropGeometry.Scatter(rect, regions, density, seed))
            {
                if (++candidates > manager.CandidateBudget) yield break;
                var position = new Vector3(point.position.x, 0, point.position.y) + previewOffset;
                position.y = Utils.GetGroundHeight(position);
                if (manager.CanPreview(position, propType, point.scale))
                    matrices.Add(Matrix4x4.TRS(position, Quaternion.Euler(0,point.rotation,0), Vector3.one * point.scale));
                if ((candidates & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.FrameBudgetMs)
                { yield return null; timer.Restart(); }
            }
            foreach (var part in previewModel.normal)
                for (int start = 0; start < matrices.Count; start += 511)
                {
                    int count = Mathf.Min(511, matrices.Count - start);
                    var values = new Matrix4x4[count];
                    for (int i = 0; i < count; i++) values[i] = matrices[start+i] * part.local;
                    previewBatches.Add(new PreviewBatch { part = part, matrices = values });
                    if (timer.ElapsedMilliseconds > PerformanceConfig.FrameBudgetMs)
                    { yield return null; timer.Restart(); }
                }
        }
        previewWorker = null;
    }

    // =====================================================================
    // クリーンアップ
    // =====================================================================

    private void ClearHoverGhost()
    {
        middleDrag.Cancel();
        if (hoverGhost != null) { Destroy(hoverGhost); hoverGhost = null; }
        hoverValid = false;
    }

    private void ClearAll()
    {
        ClearHoverGhost();
        // 個別モードの仮配置。
        foreach (var go in placedObjects) if (go != null) Destroy(go);
        placedObjects.Clear();
        placedInstances.Clear();

        // 範囲モードの頂点・散布ゴースト。
        scatterVertices.Clear();
        ClearScatterGhosts();
        shapeFinalized = false;
    }

    /// <summary>
    /// UI側のリセットボタン押下を受けて、配置済みのインスタンス・頂点・仮確定状態をすべてクリアする。
    /// </summary>
    public void ResetPlacement()
    {
        if (!active) return;
        ClearAll();
        if (mode == Mode.Instance) EmitInstances();
        else EmitShape();
        Debug.Log("PropPlacementManager: 配置状態をリセットしました");
    }

    private void ClearScatterGhosts()
    {
        if (previewWorker != null) StopCoroutine(previewWorker);
        previewWorker = null;
        previewBatches.Clear();
        previewModel?.Dispose(); previewModel = null;
    }

    // =====================================================================
    // IPC 送出
    // =====================================================================

    /// <summary>全種類の仮配置を配置順で UI に通知する({prop_type, x, y, z, rotation, scale} の配列)。</summary>
    private void EmitInstances()
    {
        if (IpcClient.Instance == null) return;
        var list = new List<Dictionary<string, object>>(placedInstances.Count);
        foreach (var inst in placedInstances)
        {
            list.Add(new Dictionary<string, object>
            {
                ["prop_type"] = inst.propType,
                ["x"] = inst.x,
                ["y"] = inst.y,
                ["z"] = inst.z,
                ["rotation"] = inst.rotation,
                ["scale"] = inst.scale,
            });
        }
        IpcClient.Instance.EmitEditPropUpdateInstances(list);
    }

    /// <summary>散布範囲ポリゴンの頂点列を UI に通知する([x, z] の配列)。</summary>
    private void EmitShape()
    {
        if (IpcClient.Instance == null) return;
        var polygon = new List<List<float>>(scatterVertices.Count);
        foreach (var v in scatterVertices)
        {
            polygon.Add(new List<float> { v.x, v.z });
        }
        IpcClient.Instance.EmitEditPropUpdateShape(polygon, shapeFinalized);
    }

    // =====================================================================
    // オーバーレイ描画(範囲モードのポリゴン)
    // =====================================================================

    private void UpdateOverlay()
    {
        if (overlayRenderer == null) return;
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        overlayRenderer.BeginFrame();

        // 確定済みの辺(頂点をぐるりと結ぶ)。
        int n = scatterVertices.Count;
        for (int i = 0; i < n - 1; i++)
        {
            overlayRenderer.AddSolidLine(scatterVertices[i], scatterVertices[i + 1], LineColor, LINE_THICKNESS);
        }
        // 3頂点以上なら閉じ線も描く。
        if (n >= 3)
        {
            overlayRenderer.AddSolidLine(scatterVertices[n - 1], scatterVertices[0], LineColor, LINE_THICKNESS);
        }

        // 最後の頂点からカーソルまでのプレビュー線。
        // 仮確定していない場合のみ描画する。
        if (!shapeFinalized && hoverValid && n > 0)
        {
            overlayRenderer.AddSolidLine(scatterVertices[n - 1], hoverPos, PreviewColor, LINE_THICKNESS);
        }

        // 頂点マーカー。
        for (int i = 0; i < n; i++)
        {
            overlayRenderer.AddSquare(scatterVertices[i], VERTEX_MARKER_HALF_SIZE, VertexColor);
        }

        overlayRenderer.EndFrame(cam);
    }

    private static Material CreatePreviewMaterial(Material source)
    {
        // アルファ値だけではURP Litは透過しないため、複製側で描画状態も揃える。
        var material = new Material(source) { enableInstancing = true };
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_ZWrite", 0f);
        material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.SetOverrideTag("RenderType", "Transparent");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        return material;
    }

    private void OnDestroy()
    {
        if (previewMaterial != null) Destroy(previewMaterial);
        ClearAll();
        if (overlayMeshObj != null) Destroy(overlayMeshObj);
    }
}
