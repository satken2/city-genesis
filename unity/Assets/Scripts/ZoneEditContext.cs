using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// 地区編集モードのツール。Ctrlラジアルメニュー(ZoneEditControlMenu)で切り替える。
/// - Create: 街路をクリックしてその街路を含む新規地区を作成する
/// - Expand: 既存地区の領域から Shift+ドラッグして、なぞった街路・面する区画・建物を地区へ取り込む
/// </summary>
public enum ZoneEditTool
{
    Create,
    Expand
}

/// <summary>
/// 地区編集モードの状態を管理するシングルトン。
/// タスクバーから地区編集モードに入った場合（新規作成）と、
/// 地区詳細パネルから編集モードに入った場合（既存編集）を区別する。
/// </summary>
public class ZoneEditContext : MonoBehaviour
{
    public static ZoneEditContext Instance { get; private set; }

    /// <summary>
    /// 現在の編集ツール。ZoneEditControlMenu(Ctrlラジアル)で切り替える。
    /// 新規作成モードで入れば Create、既存編集モードで入れば Expand が初期値になる。
    /// </summary>
    public ZoneEditTool CurrentTool { get; private set; } = ZoneEditTool.Create;

    /// <summary>
    /// 編集ツールを設定する。ZoneEditControlMenu の項目選択から呼ばれる。
    /// </summary>
    public void SetTool(ZoneEditTool tool)
    {
        CurrentTool = tool;
    }

    /// <summary>
    /// 新規地区プレビュー用のセンチネル値。
    /// 実際のzone_idの代わりにtempZoneIdに設定し、
    /// VisualModeObjectに「新規地区のプレビュー色」を適用させるために使う。
    /// </summary>
    public const int PREVIEW_SENTINEL = -1;

    /// <summary>
    /// 現在地区編集モードがアクティブかどうか。
    /// </summary>
    public bool IsActive => isActive;

    /// <summary>
    /// 既存地区編集モードかどうか。
    /// アクティブかつ CurrentZoneId が非 null の場合に true となる。
    /// </summary>
    public bool IsEditMode => isActive && currentZoneId.HasValue;

    /// <summary>
    /// 編集対象の地区ID。新規作成時は null。
    /// </summary>
    public int? CurrentZoneId => currentZoneId;

    private bool isActive = false;
    private int? currentZoneId = null;

    /// <summary>
    /// ドラッグ中に追加予定としてホバーされた道路IDのリスト。
    /// ドラッグ終了時に CommitDragPendingPaths でまとめて地区へ割り当てる。
    /// </summary>
    private List<int> dragPendingPathIds = new List<int>();

    /// <summary>
    /// ドラッグ中に追加予定としてホバーされた道路IDのリスト（読み取り専用）。
    /// </summary>
    public IReadOnlyList<int> DragPendingPathIds => dragPendingPathIds;

    /// <summary>
    /// 新規地区プレビューの色インデックス（0〜3）。
    /// 既存地区の数に基づいて決定され、既存の4色を循環する。
    /// </summary>
    private int previewColorIndex = 0;

    /// <summary>
    /// 新規地区プレビューの色インデックス（読み取り専用）。
    /// </summary>
    public int PreviewColorIndex => previewColorIndex;

    /// <summary>
    /// 地区拡張ドラッグ中に一時色付けした道路IDの台帳。
    /// ドラッグ確定・キャンセル・モード終了時に ClearDragTempVisuals で元の色に戻す。
    /// リロードだけに依存すると、バックエンド送信失敗時(version不変でリロードが再描画しない)に
    /// 一時色が残るため、明示的に戻す。
    /// </summary>
    private readonly HashSet<int> tempColoredPathIds = new HashSet<int>();

    /// <summary>
    /// 地区拡張ドラッグ中に一時色付けした建物agent_idの台帳。
    /// </summary>
    private readonly HashSet<int> tempColoredBuildingIds = new HashSet<int>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    /// <summary>
    /// 新規作成モードに遷移する。
    /// パスをクリックした際に新規地区が作成される。
    /// </summary>
    public void EnterCreateMode()
    {
        isActive = true;
        currentZoneId = null;
        CurrentTool = ZoneEditTool.Create;

        // 既存地区の数に基づいてプレビュー色を決定（4色を循環）
        var zoneManager = FindAnyObjectByType<ZoneManager>();
        previewColorIndex = zoneManager != null ? zoneManager.GetAllZones().Count % 4 : 0;
    }

    /// <summary>
    /// 既存地区編集モードに遷移する。
    /// パスをクリックした際に UI に選択通知が送信される。
    /// </summary>
    public void EnterEditMode(int zoneId)
    {
        isActive = true;
        currentZoneId = zoneId;
        // 既存地区を選択した=拡張ツールに切り替える(右クリック選択・zone_id付き遷移でも同じ)。
        CurrentTool = ZoneEditTool.Expand;
    }

    /// <summary>
    /// ドラッグ中の追加予定道路リストに道路IDを追加する。
    /// 新規に追加した場合(=初回タッチ)は true を返す。既に含まれていれば false。
    /// 呼び出し側(PathInputHandler.onZoneDragHover)は true のときだけ
    /// 面区画・建物へのファンアウト色付けを行い、毎フレームの再走査を避ける。
    /// </summary>
    public bool AddDragPendingPathId(int pathId)
    {
        if (dragPendingPathIds.Contains(pathId)) return false;
        dragPendingPathIds.Add(pathId);
        return true;
    }

    /// <summary>
    /// ドラッグ中の追加予定道路リストをクリアする。
    /// </summary>
    public void ClearDragPendingPaths()
    {
        dragPendingPathIds.Clear();
    }

    /// <summary>
    /// 地区拡張ドラッグ中に一時色付けした道路を台帳へ記録する。
    /// </summary>
    public void RegisterTempColoredPath(int pathId)
    {
        tempColoredPathIds.Add(pathId);
    }

    /// <summary>
    /// 地区拡張ドラッグ中に一時色付けした建物を台帳へ記録する。
    /// </summary>
    public void RegisterTempColoredBuilding(int buildingId)
    {
        tempColoredBuildingIds.Add(buildingId);
    }

    /// <summary>
    /// 地区拡張ドラッグ中の一時色付け(道路・区画・建物)とポリゴンプレビューを全て元に戻す。
    /// ドラッグ確定(成功/失敗の両方)・キャンセル・モード終了時に呼ぶ。
    /// バックエンド送信失敗時は version が変わらずリロードで再描画されないため、
    /// リロード任せにせずここで明示的に戻すことが重要。
    /// </summary>
    public void ClearDragTempVisuals()
    {
        if (tempColoredPathIds.Count > 0)
        {
            var pathManager = FindAnyObjectByType<PathManager>();
            if (pathManager != null)
            {
                foreach (int pathId in tempColoredPathIds)
                {
                    GameObject go = pathManager.GetPathObject(pathId);
                    go?.GetComponent<VisualModeObject>()?.ClearTempZoneId();
                }
            }
            tempColoredPathIds.Clear();
        }

        if (tempColoredBuildingIds.Count > 0)
        {
            if (BuildingManager.Instance != null)
            {
                foreach (int buildingId in tempColoredBuildingIds)
                {
                    GameObject go = BuildingManager.Instance.GetBuildingObject(buildingId);
                    go?.GetComponent<VisualModeObject>()?.ClearTempZoneId();
                }
            }
            tempColoredBuildingIds.Clear();
        }

        FindAnyObjectByType<ParcelManager>()?.ClearTempZoneOverrides();
        ZonePolygonPreview.ClearPreview();
    }

    /// <summary>
    /// 地区編集モードを終了する。
    /// </summary>
    public void Exit()
    {
        isActive = false;
        currentZoneId = null;
        CurrentTool = ZoneEditTool.Create;
        dragPendingPathIds.Clear();
        // 一時色付け・プレビュー・ホバーフィルを全て片付ける
        ClearDragTempVisuals();
        ZoneManager.Instance?.ClearHoverFill();
        IpcClient.Instance?.EmitEditZoneSelected(0, "");
    }

    /// <summary>
    /// 指定された地区IDを選択中地区（アクティブ地区）として設定し、UIに通知する。
    /// zoneId = 0 の場合は選択解除。
    /// </summary>
    public void SelectZone(int zoneId)
    {
        if (zoneId > 0)
        {
            EnterEditMode(zoneId);
            var zoneManager = FindAnyObjectByType<ZoneManager>();
            string zoneName = zoneManager != null ? zoneManager.GetZoneName(zoneId) : "";
            IpcClient.Instance?.EmitEditZoneSelected(zoneId, zoneName);
            Debug.Log($"ZoneEditContext: 地区 {zoneId} ({zoneName}) を選択しました");
        }
        else
        {
            IpcClient.Instance?.EmitEditZoneSelected(0, "");
            Debug.Log("ZoneEditContext: 選択を解除しました");
        }
    }

    /// <summary>
    /// ドラッグ中に追加予定リストへ溜めた道路を、拡張元地区へ一括で割り当てる。
    /// ドラッグ終了時(InputHandler.EndDrag)に呼ばれ、クリックの即時割当と同じく
    /// Unity側がそのままバックエンドへ送信する(3Dジェスチャで完結する操作のため)。
    /// 割り当て後はプレビューポリゴンをクリアし、リロードで実際の地区色に置き換わる。
    /// </summary>
    public void CommitDragPendingPaths(int zoneId)
    {
        // 何もなぞらずに離した場合は通信せず、一時色付け・プレビューだけ片付ける。
        if (dragPendingPathIds.Count == 0)
        {
            ClearDragTempVisuals();
            return;
        }

        int[] pathIds = dragPendingPathIds.ToArray();
        dragPendingPathIds.Clear();

        Debug.Log($"ZoneEditContext: ドラッグ拡張を確定 -> zone={zoneId}, paths={pathIds.Length}");
        StartCoroutine(AssignPathsCoroutine(zoneId, pathIds));
    }

    /// <summary>
    /// パスをベースに新規地区を作成する（Createツールでの街路クリック）。
    /// </summary>
    public void CreateZone(int pathId)
    {
        var zoneManager = FindAnyObjectByType<ZoneManager>();
        int count = zoneManager != null ? zoneManager.GetAllZones().Count + 1 : 1;
        string zoneName = $"Zone {count}";
        StartCoroutine(CreateZoneCoroutine(new int[] { pathId }, zoneName));
    }

    private IEnumerator AssignPathsCoroutine(int zoneId, int[] pathIds)
    {
        if (ZoneApiClient.Instance == null)
        {
            new GameObject("ZoneApiClient").AddComponent<ZoneApiClient>();
        }

        yield return ZoneApiClient.Instance.AssignPathsToZone(
            pathIds,
            zoneId,
            () => {
                Debug.Log($"ZoneEditContext: Paths assigned to zone {zoneId}: count={pathIds.Length}");
                // ドラッグ中の一時色付け(道路・区画・建物)を戻してからリロードで本色に置き換える。
                ClearDragTempVisuals();
                ForceReloadZonesAndPaths();
            },
            (error) => {
                Debug.LogError($"ZoneEditContext: Failed to assign paths to zone {zoneId}: {error}");
                // 失敗時は version が変わらずリロードで再描画されないため、一時色付けを明示的に戻すことが必須。
                ClearDragTempVisuals();
                ForceReloadZonesAndPaths();
            }
        );
    }

    private IEnumerator CreateZoneCoroutine(int[] pathIds, string zoneName)
    {
        var request = new CreateZoneRequest
        {
            path_ids = pathIds,
            name = zoneName,
            allowed_bd_mask = null,
            limit_volume_ratio = null,
            limit_coverage_ratio = null,
            limit_height = null
        };

        if (ZoneApiClient.Instance == null)
        {
            new GameObject("ZoneApiClient").AddComponent<ZoneApiClient>();
        }

        yield return ZoneApiClient.Instance.CreateZone(request,
            (response) => {
                Debug.Log($"ZoneEditContext: Zone created: ID={response.zone.zone_id}");
                // 作成した新規地区を自動選択する
                SelectZone(response.zone.zone_id);
                ForceReloadZonesAndPaths();
            },
            (error, statusCode) => {
                Debug.LogError($"ZoneEditContext: Failed to create zone: {error}");
            }
        );
    }

    private void ForceReloadZonesAndPaths()
    {
        // 道路・区画の差分更新(道路の zone_id 変更・区画の所属地区変更を反映)。
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
        // 地区は version 差分に頼らず全地区を強制再描画する。
        // 拡張では侵食された側の地区・空になって消滅した地区も同時に変わるため、
        // 変更のあった地区だけを差分描画すると取りこぼす(ReloadAllData の地区更新も
        // isForce=false のため areZonesLoaded 済みだとスキップされる)。
        var zoneManager = ZoneManager.Instance ?? FindAnyObjectByType<ZoneManager>();
        if (zoneManager != null)
        {
            zoneManager.ReloadAllZonesForced();
        }
    }
}
