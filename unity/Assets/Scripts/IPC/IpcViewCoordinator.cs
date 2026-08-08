using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// IPC経由で view.change_view イベントを受信し、
/// VisualModeHandler のモードを各レイヤーごとに切り替える。
/// </summary>
public class IpcViewCoordinator : MonoBehaviour
{
    private Dictionary<string, string> _layerModes = new Dictionary<string, string>
    {
        { "terrain", "default" },
        { "grid", "hidden" },
        { "cell", "hidden" },
        { "road", "default" },
        { "building", "default" },
        { "parcel", "border" },
        { "district", "hidden" },
        { "environment", "hidden" },
        { "resource", "hidden" },
        { "alert", "visible" },
        { "transit", "default" },
        { "prop", "visible" }
    };

    private Dictionary<string, string> _layerSubModes = new Dictionary<string, string>();

    /// <summary>
    /// レイヤーごとの表示フラグ(CSV文字列。例 "plan,public")。
    /// 色モードと独立したON/OFF集合で、現状 road / building レイヤーのみが使用する。
    /// UI側の初期値(計画中=ON)と一致させる。
    /// </summary>
    private Dictionary<string, string> _layerFlags = new Dictionary<string, string>
    {
        { "road", "plan" },
        { "building", "plan" }
    };

    /// <summary>
    /// 現在のステート。HandleViewChange で STATE_NORMAL 中の変更のみ記憶するために使用する。
    /// </summary>
    private string _currentState = "STATE_NORMAL";

    /// <summary>
    /// STATE_NORMAL 時にユーザーが変更したレイヤーの表示状態を記憶する。
    /// 次に STATE_NORMAL に戻ったとき、この記憶を復元する。
    /// STATE_NORMAL 以外での変更は記憶しない。
    /// </summary>
    private Dictionary<string, (string mode, string subMode, string flags)> _normalStateMemory = new Dictionary<string, (string, string, string)>();

    private void OnEnable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnViewChangeView += HandleViewChange;
        }
    }

    private void OnDisable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnViewChangeView -= HandleViewChange;
        }
    }

    private void HandleViewChange(string layer, string mode, string subMode, string flags)
    {
        Debug.Log($"IpcViewCoordinator: view.change_view -> layer={layer}, mode={mode}, sub_mode={subMode}, flags={flags ?? "(keep)"}");

        _layerModes[layer] = mode;
        if (subMode != null && subMode.Length > 0)
        {
            _layerSubModes[layer] = subMode;
        }
        else
        {
            _layerSubModes.Remove(layer);
        }

        // flags は null=現在値を維持 / ""=全OFF / それ以外=CSVで指定されたフラグ集合。
        // 「維持」は、建設パネル等がモードだけ切り替える旧来の呼び出しでトグル状態を壊さないための仕様。
        if (flags != null)
        {
            if (flags.Length > 0)
            {
                _layerFlags[layer] = flags;
            }
            else
            {
                _layerFlags.Remove(layer);
            }
        }
        string effectiveFlags = _layerFlags.TryGetValue(layer, out var f) ? f : "";

        // STATE_NORMAL 中の変更のみ記憶する。
        if (_currentState == "STATE_NORMAL")
        {
            _normalStateMemory[layer] = (mode, subMode, effectiveFlags);
            Debug.Log($"HandleViewChange: STATE_NORMAL 中の変更を記憶 -> {layer}={mode}, sub={subMode ?? "(none)"}, flags={effectiveFlags}");
        }

        if (VisualModeHandler.Instance == null) return;

        switch (layer)
        {
            case "terrain":
                ApplyTerrainMode(mode, subMode);
                break;

            case "grid":
                ApplyGridMode(mode);
                break;

            case "cell":
                ApplyCellMode(mode);
                break;

            case "road":
                ApplyRoadMode(mode, subMode, effectiveFlags);
                break;

            case "building":
                ApplyBuildingMode(mode, subMode, effectiveFlags);
                break;

            case "parcel":
                ApplyParcelMode(mode, subMode);
                break;

            case "district":
                ApplyDistrictMode(mode);
                break;

            case "environment":
                ApplyEnvironmentMode(mode);
                break;

            case "resource":
                ApplyResourceMode(mode);
                break;

            case "alert":
                ApplyAlertMode(mode, subMode);
                break;

            case "transit":
                ApplyTransitMode(mode, subMode);
                break;

            case "prop":
                ApplyPropMode(mode);
                break;

            default:
                Debug.LogWarning($"IpcViewCoordinator: 未知のレイヤー: {layer}");
                break;
        }
    }

    /// <summary>
    /// UI から view.query_state が来たときに呼ばれる。
    /// 全レイヤーの現在の表示モードをまとめて返す。
    /// </summary>
    public void QueryCurrentState()
    {
        Debug.Log("IpcViewCoordinator: 現在の表示状態を送信します");
        NotifyStateChanged();
    }

    /// <summary>
    /// IpcStateCoordinator からステート変更後に呼ばれる。
    /// ステートに応じて _layerModes を同期し、UIに通知する。
    /// ipcParams は STATE_EDIT_BUILDING で選択された建物バリアントから
    /// bd_type を解決し、パーセルの buildable_color subMode を設定するために使用する。
    /// </summary>
    public void SyncFromState(string state, Dictionary<string, object> ipcParams = null)
    {
        _currentState = state;

        switch (state)
        {
            case "STATE_NORMAL":
                RestoreNormalLayerModes();
                break;

            case "STATE_EDIT_PATH":
                // 道路敷設中は地区を非表示にする。
                // 地区が表示されていると誤クリックで地区パネルが開き、
                // パネルを閉じた際に道路敷設がキャンセルされる恐れがあるため。
                _layerModes["parcel"] = "border";
                _layerModes["road"] = "default";
                _layerModes["building"] = "default";
                _layerModes["district"] = "hidden";
                _layerSubModes.Remove("parcel");
                _layerSubModes.Remove("road");
                _layerSubModes.Remove("building");
                _layerSubModes.Remove("district");
                SetPlanOnlyFlags();
                break;

            case "STATE_EDIT_BUILDING":
            {
                // 建物建設モードでは、選択された建物種類に応じて
                // パーセルを buildable_color モードにし、建設可能な区画をハイライトする。
                int currentBdType = 0;
                if (ipcParams != null && ipcParams.TryGetValue("building_variant", out object bvObj) && bvObj != null)
                {
                    int buildingVariant = Convert.ToInt32(bvObj);
                    if (MasterDataStore.Instance != null)
                    {
                        var agentMaster = MasterDataStore.Instance.GetAgentMaster(buildingVariant);
                        if (agentMaster != null && agentMaster.bd_type.HasValue)
                        {
                            currentBdType = agentMaster.bd_type.Value;
                        }
                    }
                }

                string filterSubMode = BuildingPlanManager.BdTypeToParcelFilterSubMode(currentBdType);
                if (filterSubMode != null)
                {
                    _layerModes["parcel"] = "buildable_color";
                    _layerSubModes["parcel"] = filterSubMode;
                }
                else
                {
                    _layerModes["parcel"] = "border";
                    _layerSubModes.Remove("parcel");
                }
                _layerModes["road"] = "default";
                _layerModes["building"] = "default";
                _layerModes["district"] = "border_icon";
                _layerSubModes.Remove("road");
                _layerSubModes.Remove("building");
                _layerSubModes.Remove("district");
                SetPlanOnlyFlags();
                break;
            }

            case "STATE_EDIT_PARCEL":
                _layerModes["parcel"] = "border";
                _layerModes["road"] = "default";
                _layerModes["building"] = "default";
                _layerModes["district"] = "border_icon";
                _layerSubModes.Remove("parcel");
                _layerSubModes.Remove("road");
                _layerSubModes.Remove("building");
                _layerSubModes.Remove("district");
                SetPlanOnlyFlags();
                break;

            case "STATE_EDIT_ZONE":
                // 地区編集中は道路・建物とも地区色分けにして所属地区を可視化する。
                // 計画中の表示は編集の邪魔になるためOFF。
                _layerModes["parcel"] = "border";
                _layerModes["road"] = "zone_color";
                _layerModes["building"] = "zone_color";
                _layerModes["district"] = "border_icon";
                _layerSubModes.Remove("parcel");
                _layerSubModes.Remove("road");
                _layerSubModes.Remove("building");
                _layerSubModes.Remove("district");
                _layerFlags.Remove("road");
                _layerFlags.Remove("building");
                break;

            case "STATE_UNLOCK_CELL":
                // セル開放中は現況の街並みだけを見せたいので、計画中の表示もOFFにする。
                _layerModes["parcel"] = "hidden";
                _layerModes["road"] = "default";
                _layerModes["building"] = "default";
                _layerModes["district"] = "hidden";
                _layerSubModes.Remove("parcel");
                _layerSubModes.Remove("road");
                _layerSubModes.Remove("building");
                _layerSubModes.Remove("district");
                _layerFlags.Remove("road");
                _layerFlags.Remove("building");
                break;

            case "STATE_EDIT_TRANSIT":
                // 公共交通の路線設置中は道路建設と同じ計画中表示にし、地区は非表示にする。
                // 公共交通レイヤー(transit)は、設置対象の路線種別が見えるよう表示は維持する
                // (実際の表示種別集合は UI 側パネルが view.change_view で明示指定する)。
                _layerModes["parcel"] = "border";
                _layerModes["road"] = "default";
                _layerModes["building"] = "default";
                _layerModes["district"] = "hidden";
                _layerSubModes.Remove("parcel");
                _layerSubModes.Remove("road");
                _layerSubModes.Remove("building");
                _layerSubModes.Remove("district");
                SetPlanOnlyFlags();
                break;

            case "STATE_MERGE_PATH":
            case "STATE_SPLIT_PATH":
                // パスの結合相手・分割点の選択中は道路敷設と同じ計画中表示にし、地区は非表示にする
                // (地区が表示されていると誤クリックで地区パネルが開き、選択操作が中断されるため)。
                _layerModes["parcel"] = "border";
                _layerModes["road"] = "default";
                _layerModes["building"] = "default";
                _layerModes["district"] = "hidden";
                _layerSubModes.Remove("parcel");
                _layerSubModes.Remove("road");
                _layerSubModes.Remove("building");
                _layerSubModes.Remove("district");
                SetPlanOnlyFlags();
                break;

            case "STATE_REMOVE_OBJECTS":
                // ブルドーザ(削除)モードは道路敷設と同じ計画中表示にし、地区は非表示にする。
                // 区画は境界線表示にして、空き地(削除対象)が見えるようにする。
                _layerModes["parcel"] = "border";
                _layerModes["road"] = "default";
                _layerModes["building"] = "default";
                _layerModes["district"] = "hidden";
                _layerSubModes.Remove("parcel");
                _layerSubModes.Remove("road");
                _layerSubModes.Remove("building");
                _layerSubModes.Remove("district");
                SetPlanOnlyFlags();
                break;

            default:
                Debug.LogWarning($"IpcViewCoordinator: SyncFromState 不明なステート: {state}");
                return;
        }

        Debug.Log($"IpcViewCoordinator: SyncFromState -> {state} (currentState was {_currentState})");
        NotifyStateChanged();
    }

    /// <summary>
    /// 編集系ステートの標準フラグ「計画中の表示のみON」を道路・建物に設定する。
    /// 公共施設ハイライト等の他フラグは編集の邪魔になるため一時的にOFFにする
    /// (STATE_NORMAL 復帰時に _normalStateMemory から復元される)。
    /// </summary>
    private void SetPlanOnlyFlags()
    {
        _layerFlags["road"] = "plan";
        _layerFlags["building"] = "plan";
    }

    /// <summary>
    /// STATE_NORMAL に戻ったとき、_normalStateMemory に記憶した表示状態を復元する。
    /// 記憶がないレイヤーはデフォルト値に戻す。
    /// </summary>
    private void RestoreNormalLayerModes()
    {
        string[] targetLayers = { "building", "road", "parcel", "district", "terrain", "grid", "cell", "environment", "resource", "alert" };

        foreach (var layer in targetLayers)
        {
            if (_normalStateMemory.TryGetValue(layer, out var mem))
            {
                Debug.Log($"RestoreNormalLayerModes: {layer} は記憶あり -> mode={mem.mode}, sub={mem.subMode ?? "(none)"}, flags={mem.flags ?? "(none)"}");
                _layerModes[layer] = mem.mode;
                if (!string.IsNullOrEmpty(mem.subMode))
                {
                    _layerSubModes[layer] = mem.subMode;
                }
                else
                {
                    _layerSubModes.Remove(layer);
                }
                if (!string.IsNullOrEmpty(mem.flags))
                {
                    _layerFlags[layer] = mem.flags;
                }
                else
                {
                    _layerFlags.Remove(layer);
                }
            }
            else
            {
                string oldMode = _layerModes.ContainsKey(layer) ? _layerModes[layer] : "(missing)";
                switch (layer)
                {
                    case "building":
                        _layerModes[layer] = "default";
                        _layerSubModes.Remove(layer);
                        _layerFlags[layer] = "plan";
                        break;
                    case "road":
                        _layerModes[layer] = "default";
                        _layerSubModes.Remove(layer);
                        _layerFlags[layer] = "plan";
                        break;
                    case "parcel":
                        _layerModes[layer] = "hidden";
                        _layerSubModes.Remove(layer);
                        break;
                    case "district":
                        _layerModes[layer] = "hidden";
                        _layerSubModes.Remove(layer);
                        break;
                    case "resource":
                        _layerModes[layer] = "hidden";
                        _layerSubModes.Remove(layer);
                        break;
                    case "alert":
                        _layerModes[layer] = "visible";
                        _layerSubModes.Remove(layer);
                        break;
                    // terrain, grid, cell, environment, resource は記憶がなければ現在の値を維持（初期値が設定済み）
                }
            }
        }
    }

    /// <summary>
    /// 現在の _layerModes / _layerSubModes の内容を VisualModeHandler に一括適用する。
    /// IpcStateCoordinator の ApplyStateNormal などから呼ばれる。
    /// </summary>
    public void ApplyAllLayerModes()
    {
        if (VisualModeHandler.Instance == null) return;

        foreach (var kvp in _layerModes)
        {
            string sub = _layerSubModes.TryGetValue(kvp.Key, out var s) ? s : null;
            string flags = _layerFlags.TryGetValue(kvp.Key, out var f) ? f : "";
            ApplyLayerMode(kvp.Key, kvp.Value, sub, flags);
        }
    }

    private void ApplyLayerMode(string layer, string mode, string subMode, string flags)
    {
        switch (layer)
        {
            case "terrain": ApplyTerrainMode(mode, subMode); break;
            case "grid": ApplyGridMode(mode); break;
            case "cell": ApplyCellMode(mode); break;
            case "road": ApplyRoadMode(mode, subMode, flags); break;
            case "building": ApplyBuildingMode(mode, subMode, flags); break;
            case "parcel": ApplyParcelMode(mode, subMode); break;
            case "district": ApplyDistrictMode(mode); break;
            case "environment": ApplyEnvironmentMode(mode); break;
            case "resource": ApplyResourceMode(mode); break;
            case "transit": ApplyTransitMode(mode, subMode); break;
            case "prop": ApplyPropMode(mode); break;
        }
    }

    /// <summary>
    /// STATE_NORMAL 時の表示状態記憶をクリアする。
    /// セッションリセット・リロード時など、状態を初期化したい場合に使用する。
    /// </summary>
    public void ClearNormalStateMemory()
    {
        _normalStateMemory.Clear();
    }

    /// <summary>
    /// 現在の全レイヤーの表示モードをUIに通知する。
    /// </summary>
    public void NotifyStateChanged()
    {
        var layers = new Dictionary<string, object>();
        foreach (var kvp in _layerModes)
        {
            string sub = _layerSubModes.ContainsKey(kvp.Key) ? _layerSubModes[kvp.Key] : null;
            string flags = _layerFlags.ContainsKey(kvp.Key) ? _layerFlags[kvp.Key] : null;
            layers[kvp.Key] = new Dictionary<string, string>
            {
                { "mode", kvp.Value },
                { "sub_mode", sub ?? "" },
                { "flags", flags ?? "" }
            };
        }

        IpcClient.Instance?.EmitViewCurrentState(layers);
    }

    private void ApplyTerrainMode(string mode, string subMode = null)
    {
        switch (mode)
        {
            case "default":
                VisualModeHandler.Instance.SetMode(InputObjectType.TERRAIN, VisualMode.MODE_NORMAL);
                break;
            case "transparent":
                VisualModeHandler.Instance.SetMode(InputObjectType.TERRAIN, VisualModeTerrain.TRANSPARENT);
                break;
            case "elevation":
                VisualModeHandler.Instance.SetMode(InputObjectType.TERRAIN, VisualModeTerrain.ELEVATION);
                break;
        }

        // 等高線の表示/非表示切り替え
        bool showContours = (subMode == "contour_on");
        var tm = FindAnyObjectByType<TerrainManager>();
        if (tm != null)
        {
            tm.SetContourLinesVisible(showContours);
        }
    }

    private void ApplyGridMode(string mode)
    {
        // グリッドは独自のシステムで管理される。
        // 現状はログのみ。GridOverlayコンポーネント等への通知は別途実装。
        Debug.Log($"IpcViewCoordinator: Grid mode -> {mode}");
    }

    private void ApplyCellMode(string mode)
    {
        var visualizer = FindAnyObjectByType<DebugCellVisualizer>();
        if (visualizer == null)
        {
            Debug.LogWarning("IpcViewCoordinator: DebugCellVisualizer not found");
            return;
        }

        switch (mode)
        {
            case "hidden":
                visualizer.SetShowCellGrid(false);
                break;
            case "boundaries":
                visualizer.SetShowCellGrid(true);
                break;
        }
    }

    private void ApplyRoadMode(string mode, string subMode, string flags)
    {
        // 計画中表示などのフラグは色モードと独立に適用する
        VisualModeHandler.Instance.SetFlags(InputObjectType.PATH, ParseFlags(flags));

        switch (mode)
        {
            case "default":
                VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
                break;
            case "traffic_color":
                int trafficMode = subMode switch
                {
                    "TRAFFIC_PEDESTRIAN" => VisualModePath.PEDESTRIAN,
                    "TRAFFIC_ROAD" => VisualModePath.UTILIZATION,
                    "TRAFFIC_RAIL" => VisualModePath.SPEED,
                    _ => VisualModePath.UTILIZATION
                };
                VisualModeHandler.Instance.SetMode(InputObjectType.PATH, trafficMode);
                break;
            case "speed_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualModePath.SPEED);
                break;
            case "durability_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualModePath.DURABILITY);
                break;
            case "zone_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualModePath.ZONE_COLOR);
                break;
            case "hidden":
                VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_HIDDEN);
                break;
        }
    }

    private void ApplyBuildingMode(string mode, string subMode, string flags)
    {
        // 計画中表示・公共施設ハイライトのフラグは色モードと独立に適用する
        VisualModeHandler.Instance.SetFlags(InputObjectType.BUILDING, ParseFlags(flags));

        switch (mode)
        {
            case "default":
                VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
                break;
            case "floor_color":
                int floorMode = subMode switch
                {
                    "SPACE_OTHER" => VisualModeBuilding.SPACE_OTHER,
                    "SPACE_RESIDENCE" => VisualModeBuilding.SPACE_RESIDENCE,
                    "SPACE_RETAIL" => VisualModeBuilding.SPACE_RETAIL,
                    "SPACE_FACTORY" => VisualModeBuilding.SPACE_FACTORY,
                    "SPACE_OFFICE" => VisualModeBuilding.SPACE_OFFICE,
                    "SPACE_FARM" => VisualModeBuilding.SPACE_FARM,
                    _ => VisualModeBuilding.SPACE_OTHER
                };
                VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, floorMode);
                break;
            case "durability_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualModeBuilding.DURABILITY);
                break;
            case "zone_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualModeBuilding.ZONE_COLOR);
                break;
            case "hidden":
                VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_HIDDEN);
                break;
        }
    }

    /// <summary>
    /// フラグのCSV文字列("plan,public" 形式)を VisualFlag のビット和に変換する。
    /// 未知のフラグIDは無視する。
    /// </summary>
    private static int ParseFlags(string flags)
    {
        int result = VisualFlag.NONE;
        if (string.IsNullOrEmpty(flags)) return result;

        foreach (var part in flags.Split(','))
        {
            switch (part.Trim())
            {
                case "plan":
                    result |= VisualFlag.SHOW_PLAN;
                    break;
                case "public":
                    result |= VisualFlag.HIGHLIGHT_PUBLIC;
                    break;
            }
        }
        return result;
    }

    private void ApplyParcelMode(string mode, string subMode)
    {
        switch (mode)
        {
            case "hidden":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_HIDDEN);
                break;
            case "border":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
                break;
            case "land_price_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualModeParcel.LAND_PRICE);
                break;
            case "zone_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualModeParcel.ZONE_COLOR);
                break;
            case "buildable_color":
                int filterMode = subMode switch
                {
                    "BUILDING_APARTMENT" => VisualModeParcel.FILTER_APARTMENT,
                    "BUILDING_HOUSE" => VisualModeParcel.FILTER_HOUSE,
                    "BUILDING_RETAIL" => VisualModeParcel.FILTER_COMMERCIAL,
                    "BUILDING_FACTORY" => VisualModeParcel.FILTER_FACTORY,
                    "BUILDING_OFFICE" => VisualModeParcel.FILTER_OFFICE,
                    "BUILDING_FARM" => VisualModeParcel.FILTER_FARM,
                    "BUILDING_MINE" => VisualModeParcel.FILTER_MINE,
                    "BUILDING_PORT" => VisualModeParcel.FILTER_PORT,
                    "BUILDING_OTHER" => VisualModeParcel.FILTER_OTHER,
                    "BUILDING_PUBLIC" => VisualModeParcel.FILTER_OTHER,
                    _ => VisualModeParcel.FILTER_HOUSE
                };
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, filterMode);
                break;
        }
    }

    private void ApplyDistrictMode(string mode)
    {
        switch (mode)
        {
            case "hidden":
                VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
                break;
            case "border_icon":
                VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_NORMAL);
                break;
        }
    }

    private void ApplyEnvironmentMode(string mode)
    {
        // 環境ヒートマップは Terrain レイヤーの拡張モードとして扱う。
        switch (mode)
        {
            case "hidden":
                VisualModeHandler.Instance.SetMode(InputObjectType.TERRAIN, VisualMode.MODE_NORMAL);
                break;
            case "noise_heatmap":
                VisualModeHandler.Instance.SetMode(InputObjectType.TERRAIN, VisualModeEnvironment.NOISE_HEATMAP);
                break;
            case "air_pollution_heatmap":
                VisualModeHandler.Instance.SetMode(InputObjectType.TERRAIN, VisualModeEnvironment.AIR_POLLUTION_HEATMAP);
                break;
        }
    }

    private void ApplyResourceMode(string mode)
    {
        if (ResourceManager.Instance != null)
        {
            ResourceManager.Instance.SetVisible(mode == "visible");
        }
    }

    /// <summary>
    /// 公共交通レイヤーの表示を切り替える。
    /// subMode は表示する路線種別(transit_master の transit_path_id ＝区間パスのバリアント)の
    /// CSV文字列。空なら全種別非表示。TransitOverlayManager に表示対象のバリアント集合を渡す。
    /// </summary>
    private void ApplyTransitMode(string mode, string subMode)
    {
        if (TransitOverlayManager.Instance == null) return;

        var variants = new HashSet<int>();
        if (!string.IsNullOrEmpty(subMode))
        {
            foreach (var part in subMode.Split(','))
            {
                if (int.TryParse(part, out int v))
                {
                    variants.Add(v);
                }
            }
        }
        TransitOverlayManager.Instance.SetVisibleVariants(variants);
    }

    private void ApplyPropMode(string mode)
    {
        if (PropManager.Instance != null)
        {
            PropManager.Instance.SetVisible(mode == "visible");
        }
    }

    private void ApplyAlertMode(string mode, string subMode = null)
    {
        if (AlertManager.Instance != null)
        {
            int mask = 27;
            if (!string.IsNullOrEmpty(subMode))
            {
                int.TryParse(subMode, out mask);
            }
            AlertManager.Instance.SetFilterMask(mask);
        }
    }

}
