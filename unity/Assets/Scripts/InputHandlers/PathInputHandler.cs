
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;
using Newtonsoft.Json;
using GeneratedModels;

/// <summary>
/// パス（道路）オブジェクト用のInputHandler
/// </summary>
public class PathInputHandler : MonoBehaviour, IInputHandler
{
    public int pathId;

    public int ObjectTypeId => InputObjectType.PATH;

    private VisualModeObject visualModeObject;

    // RoadPlanManagerへの参照
    private RoadPlanManager roadPlanManager;

    // PathManagerへの参照
    private PathManager pathManager;

    private void Start()
    {
        // VisualModeObjectコンポーネントの取得
        visualModeObject = GetComponent<VisualModeObject>();

        // マネージャーへの参照を取得
        roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        pathManager = FindAnyObjectByType<PathManager>();
    }

    public void onClick(int mode)
    {
        // パーセルエディターの道路選択モード中は特別処理
        if (IsParcelEditorPathSelectionMode())
        {
            HandleParcelEditorPathSelection();
            return;
        }

        switch (mode)
        {
            case InputMode.MODE_NORMAL:
                HandleNormalClick();
                break;
            case InputMode.MODE_ZONE_EDIT:
                HandleZoneEditClick();
                break;
            case InputMode.MODE_PARCEL_CREATION_PATH:
                HandleParcelCreationClick();
                break;
            case InputMode.MODE_BUILDING_CONSTRUCTION:
                HandleBuildingConstructionClick();
                break;
            case InputMode.MODE_ROAD_CONSTRUCTION:
                HandleRoadConstructionClick();
                break;
            case InputMode.MODE_REMOVE:
                RemovalManager.Instance?.TryRemove(InputObjectType.PATH, pathId);
                break;
            case InputMode.MODE_PATH_MERGE:
                PathTopologyEditManager.Instance?.OnPathClickedForMerge(pathId);
                break;
            case InputMode.MODE_PATH_SPLIT:
                // 分割点はクリック地点を対象パスの中心線へ射影して決めるため、
                // クリックされたパスがどれかに関わらずマネージャの射影結果を使う。
                PathTopologyEditManager.Instance?.OnClickForSplit();
                break;
            case InputMode.MODE_NONE:
                // 何もしない
                break;
            default:
                HandleNormalClick();
                break;
        }
    }

    public void onHoverStart(int mode)
    {
        // ブルドーザモードでは削除可能(計画中)な道路のみハイライトする
        if (mode == InputMode.MODE_REMOVE)
        {
            if (RemovalManager.Instance != null
                && RemovalManager.Instance.IsRemovable(InputObjectType.PATH, pathId)
                && visualModeObject != null)
            {
                visualModeObject.SetHover(true);
            }
            return;
        }

        // パス結合の相手選択モードでは、結合できる候補パスのみハイライトする
        if (mode == InputMode.MODE_PATH_MERGE)
        {
            if (PathTopologyEditManager.Instance != null
                && PathTopologyEditManager.Instance.IsMergeCandidate(pathId)
                && visualModeObject != null)
            {
                visualModeObject.SetHover(true);
                TooltipManager.Instance?.Show("クリックでこのパスと結合");
            }
            return;
        }

        // パス分割の分割点選択モードでは、マーカー表示が案内を担うためホバー色は付けない
        if (mode == InputMode.MODE_PATH_SPLIT)
        {
            return;
        }

        // 道路建設モード中はハイライトをスキップ（既存パスとの混乱を避けるため）
        if (InputHandler.Instance != null && InputHandler.Instance.GetMode(InputObjectType.PATH) == InputMode.MODE_ROAD_CONSTRUCTION)
        {
            return;
        }

        // パーセルエディターの道路選択モード中は特別なハイライト
        if (IsParcelEditorPathSelectionMode())
        {
            // 道路沿い区画作成では、稼働中の一般道路のみ選択可能とする
            if (!IsPathSelectableForParcelCreation())
            {
                return;
            }

            // 道路選択モード用のハイライト
            if (visualModeObject != null)
            {
                visualModeObject.SetHover(true);
            }
            return;
        }

        // 地区編集モードではツールに応じたツールチップを表示
        if (mode == InputMode.MODE_ZONE_EDIT)
        {
            TooltipManager.Instance?.Show(BuildZoneEditTooltip());
        }

        // ハイライト表示
        if (visualModeObject != null)
        {
            visualModeObject.SetHover(true);
        }
    }

    public void onHoverEnd(int mode)
    {
        if (visualModeObject != null)
        {
            visualModeObject.SetHover(false);
        }

        if (mode == InputMode.MODE_ZONE_EDIT || mode == InputMode.MODE_PATH_MERGE)
        {
            TooltipManager.Instance?.Hide();
        }
    }

    public void onRightClick(int mode)
    {
        Debug.Log($"PathInputHandler: パス右クリック時の処理: {mode}");

        // 建設モードでの右クリック処理
        if (mode == InputMode.MODE_ROAD_CONSTRUCTION && roadPlanManager != null)
        {
            // 新規敷設モード（編集モードでない）の場合は、地形右クリックとして処理
            if (!roadPlanManager.IsEditMode)
            {
                Vector3 terrainClickPosition = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
                terrainClickPosition.y = Utils.GetGroundHeight(new Vector3(terrainClickPosition.x, 1000f, terrainClickPosition.z));

                Debug.Log($"道路敷設モード: パス上の右クリックを地形右クリックとして転送 {terrainClickPosition}");
                roadPlanManager.OnTerrainRightClick(terrainClickPosition);
                return;
            }

            // 編集モードの場合は従来通り
            Debug.Log($"道路編集モードでパス {pathId} が右クリックされました");
        }
        // 地区編集モードでの右クリック: この道路が属する地区を操作対象(拡張先・統合先)に選ぶ
        else if (mode == InputMode.MODE_ZONE_EDIT)
        {
            PathSummary pathData = pathManager?.GetPathData(pathId);
            int zoneId = pathData?.zone_id ?? 0;
            if (zoneId <= 0)
            {
                TooltipManager.Instance?.Show("この道路は地区に属していません");
                return;
            }
            ZoneEditContext.Instance?.SelectZone(zoneId);
        }
    }

    public void onHover(int mode)
    {
        // パスでは継続ホバー処理なし
    }

    private void HandleNormalClick()
    {
        // パスクリック時にpath_detailウィンドウを開く
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitSelectObject("path_detail", "path", pathId.ToString());
        }
    }

    private void HandleParcelCreationClick()
    {
        Debug.Log($"パス {pathId} が土地区画作成モードでクリックされました");

        // 道路沿い区画作成では、稼働中の一般道路のみ選択可能とする
        if (!IsPathSelectableForParcelCreation())
        {
            Debug.Log($"パス {pathId} は選択できません（status={GetPathStatus()}、type={GetPathType()}）");
            return;
        }

        // ParcelEditorManagerに道路選択を通知
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager != null)
        {
            Vector3[] waypoints = GetPathWaypoints(pathId);
            parcelEditorManager.OnPathSelected(pathId, waypoints);
        }

        // IPC: 土地区画作成モード用のパス選択イベントをUIに直接通知
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditParcelSelectPath(pathId);
        }
    }

    private void HandleBuildingConstructionClick()
    {
        Debug.Log($"パス {pathId} がクリックされました（建物建設モード）");
        // 建物建設モードでは特別な処理なし
    }

    private void HandleRoadConstructionClick()
    {
        // パス上のクリックは地形クリックとしてRoadPlanManagerに転送
        if (roadPlanManager != null)
        {
            Vector3 terrainClickPosition = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
            terrainClickPosition.y = Utils.GetGroundHeight(new Vector3(terrainClickPosition.x, 1000f, terrainClickPosition.z));
            roadPlanManager.OnTerrainLeftClick(terrainClickPosition);
        }
    }

    private void HandleParcelEditorPathSelection()
    {
        Debug.Log($"パーセルエディターで道路 {pathId} が選択されました");

        // 道路沿い区画作成では、稼働中の一般道路のみ選択可能とする
        if (!IsPathSelectableForParcelCreation())
        {
            Debug.Log($"パス {pathId} は選択できません（status={GetPathStatus()}、type={GetPathType()}）");
            return;
        }

        // ウェイポイントの取得（実際の実装ではAPIから取得など）
        Vector3[] waypoints = GetPathWaypoints(pathId);

        // ParcelEditorManagerに道路選択を通知
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager != null)
        {
            parcelEditorManager.OnPathSelected(pathId, waypoints);
        }

    }

    /// <summary>
    /// 道路沿い区画作成において、このパスが選択可能かどうかを判定する。
    /// 稼働中（STATUS_ACTIVE）かつ一般道路（PATH_STREET）のみ選択可能とする。
    /// </summary>
    private bool IsPathSelectableForParcelCreation()
    {
        PathSummary pathData = pathManager?.GetPathData(pathId);
        if (pathData == null)
        {
            return false;
        }
        return pathData.status == Constants.STATUS_ACTIVE && pathData.path_type == Constants.PATH_STREET;
    }

    /// <summary>
    /// このパスのステータスを取得する。データがない場合は -1 を返す。
    /// </summary>
    private int GetPathStatus()
    {
        PathSummary pathData = pathManager?.GetPathData(pathId);
        return pathData?.status ?? -1;
    }

    /// <summary>
    /// このパスの種別を取得する。データがない場合は -1 を返す。
    /// </summary>
    private int GetPathType()
    {
        PathSummary pathData = pathManager?.GetPathData(pathId);
        return pathData?.path_type ?? -1;
    }

    private bool IsParcelEditorPathSelectionMode()
    {
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        return parcelEditorManager != null &&
               parcelEditorManager.enabled &&
               parcelEditorManager.IsPathSelectionMode();
    }

    /// <summary>
    /// 地区編集モードで街路にホバーしたときのツールチップ文言を組み立てる。
    /// ツールと選択状態によって「次に何が起きるか」が変わるため、その場の操作結果を書く。
    /// </summary>
    private string BuildZoneEditTooltip()
    {
        var ctx = ZoneEditContext.Instance;
        if (ctx == null) return "";

        switch (ctx.CurrentTool)
        {
            case ZoneEditTool.Create:
                return "左クリックで新規地区を作成 / 右クリックで地区を選択";
            case ZoneEditTool.Expand:
                if (ctx.SelectedZoneId <= 0)
                {
                    return "右クリックで拡張先の地区を選択";
                }
                return $"左クリックで「{ctx.GetSelectedZoneName()}」に追加 / 右クリックで拡張先を変更";
            default:
                if (ctx.SelectedZoneId <= 0)
                {
                    return "右クリックで統合先の地区を選択";
                }
                return $"左クリックでこの道路の地区を「{ctx.GetSelectedZoneName()}」へ統合";
        }
    }

    /// <summary>
    /// 地区編集モードでのパスクリック処理。ツールは Ctrlラジアルメニュー
    /// (ZoneEditControlMenu)で切り替え、いずれも「右クリックで対象地区を選び、左クリックで適用」。
    ///
    /// - 作成ツール(Create): クリックした街路を含む新規地区を即座に作成する
    /// - 拡張ツール(Expand): クリックした街路を選択中地区へ即座に取り込む
    /// - 統合ツール(Merge): クリックした街路が属する地区を、選択中地区へ統合するようUIへ要求する
    ///
    /// 作成・拡張のバックエンド送信は ZoneEditContext が直接行う(3Dジェスチャで完結する操作のため、
    /// パネル確定型のようにUI(Tauri)を経由しない)。統合だけは不可逆なのでUIの確定ボタンを挟む。
    /// </summary>
    private void HandleZoneEditClick()
    {
        var ctx = ZoneEditContext.Instance;
        if (ctx == null) return;

        PathSummary pathData = pathManager?.GetPathData(pathId);

        // PATH_STREET以外は選択不可
        if (pathData != null && pathData.path_type != Constants.PATH_STREET)
        {
            TooltipManager.Instance?.Show("この道路は選択できません");
            return;
        }

        switch (ctx.CurrentTool)
        {
            case ZoneEditTool.Create:
                ctx.CreateZone(pathId);
                break;

            case ZoneEditTool.Expand:
                if (ctx.SelectedZoneId <= 0)
                {
                    TooltipManager.Instance?.Show("先に右クリックで拡張先の地区を選択してください");
                    return;
                }
                if (pathData != null && pathData.zone_id == ctx.SelectedZoneId)
                {
                    TooltipManager.Instance?.Show("この道路はすでにこの地区に属しています");
                    return;
                }
                // 飛び地の地区を作らないよう、選択中地区の道路と繋がっている街路だけ取り込む
                // (バックエンドの batch-zone は1本だけの割当だと連続性を検証しないため、
                //  ここで弾かないと離れた道路が地区に混ざる)。
                if (!IsConnectedToZone(ctx.SelectedZoneId))
                {
                    TooltipManager.Instance?.Show("この道路は地区と繋がっていません");
                    return;
                }
                ctx.ExpandZone(pathId);
                break;

            case ZoneEditTool.Merge:
                if (ctx.SelectedZoneId <= 0)
                {
                    TooltipManager.Instance?.Show("先に右クリックで統合先の地区を選択してください");
                    return;
                }
                int sourceZoneId = pathData?.zone_id ?? 0;
                if (sourceZoneId <= 0)
                {
                    TooltipManager.Instance?.Show("この道路は地区に属していません");
                    return;
                }
                ctx.RequestMerge(sourceZoneId);
                break;
        }
    }

    /// <summary>
    /// このパスが、指定地区に所属する道路群とグラフ上で繋がっているかを判定する。
    /// 判定対象はロード済みのパスキャッシュに限られるが、隣接の有無を見るだけなので
    /// 対象地区の道路がカーソル周辺にロードされていれば十分に機能する。
    /// </summary>
    private bool IsConnectedToZone(int zoneId)
    {
        if (pathManager == null) return true;
        var zonePathIds = pathManager.GetZonePathIds(zoneId);
        if (zonePathIds.Count == 0) return true;
        return pathManager.IsPathConnectedToSet(pathId, zonePathIds);
    }

    /// <summary>
    /// 道路のウェイポイント情報を取得（仮の実装）
    /// </summary>
    private Vector3[] GetPathWaypoints(int pathId)
    {
        // APIから道路情報を取得する処理
        // ここでは簡易的に仮のウェイポイントを作成
        return new Vector3[]
        {
            new Vector3(-10, 0, 0),
            new Vector3(0, 0, 0),
            new Vector3(10, 0, 0)
        };

        // 実際の実装では:
        // return FetchPathWaypoints(pathId);
    }
}

