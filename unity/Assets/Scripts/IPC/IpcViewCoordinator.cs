using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// IPC経由で view.change_view イベントを受信し、
/// VisualModeHandler のモードを各レイヤーごとに切り替える。
///
/// 表示状態の正はこのクラスの記憶(_layerModes / _layerSubModes / _layerFlags)で、
/// 編集ステート(道路敷設・地区編集など)や表示プリセットが強制した表示も必ずここを通す。
/// ステートを抜けて STATE_NORMAL に戻ると、_normalStateMemory に記憶した
/// 「ユーザーが通常モードで選んでいた表示」へ戻す。
/// </summary>
public class IpcViewCoordinator : MonoBehaviour
{
    /// <summary>
    /// ウィンドウを開いている間だけ表示を差し替えるプリセット名(view.apply_preset の preset)。
    /// 専用の編集ステートを持たないウィンドウ(公共交通の管理・新設パネル)が使う。
    /// 元の表示へ戻すのは STATE_NORMAL への復帰(=ウィンドウを閉じたときにRust側が送る)。
    /// </summary>
    public const string PRESET_TRANSIT = "transit";

    /// <summary>
    /// 土地区画詳細パネルが開いている間の表示プリセット名。
    /// 区画の枠線を強制表示し、パネルを閉じたら元の表示へ復帰する。
    /// </summary>
    public const string PRESET_PARCEL_DETAIL = "parcel_detail";

    private Dictionary<string, string> _layerModes = new Dictionary<string, string>
    {
        { "terrain", "default" },
        { "grid", "hidden" },
        { "road", "default" },
        { "building", "default" },
        { "parcel", "border" },
        { "district", "hidden" },
        { "resource", "hidden" },
        { "alert", "visible" },
        { "transit", "hidden" },
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

    /// <summary>
    /// 通常モードの既定表示。ユーザーが一度も触っていないレイヤーを STATE_NORMAL へ戻すときの値で、
    /// UI側(displayConstants.ts の DISPLAY_LAYERS)の初期値と一致させること。
    /// 地形(terrain)だけは色モードを強制しないため、この表からは外して個別に扱う。
    /// </summary>
    private static readonly Dictionary<string, (string mode, string subMode, string flags)> NORMAL_DEFAULTS =
        new Dictionary<string, (string, string, string)>
    {
        { "building", ("default", null, "plan") },
        { "road", ("default", null, "plan") },
        { "parcel", ("hidden", null, null) },
        { "district", ("hidden", null, null) },
        { "prop", ("visible", null, null) },
        { "transit", ("hidden", null, null) },
        { "resource", ("hidden", null, null) },
        { "alert", ("visible", null, null) },
    };

    private void OnEnable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnViewChangeView += HandleViewChange;
            IpcClient.Instance.OnViewSetObjectFilter += HandleSetObjectFilter;
            IpcClient.Instance.OnViewQueryObjectFilter += NotifyObjectFilterState;
        }
    }

    private void OnDisable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnViewChangeView -= HandleViewChange;
            IpcClient.Instance.OnViewSetObjectFilter -= HandleSetObjectFilter;
            IpcClient.Instance.OnViewQueryObjectFilter -= NotifyObjectFilterState;
        }
    }

    /// <summary>
    /// オブジェクト単位の表示フィルタ(建設プロジェクトのタグ単位／個別の表示切替)を適用する。
    /// UIから届くのは非表示IDの全集合で、レイヤー単位の色モード・表示フラグとは独立に効く。
    /// 適用後は現在値をUIへ返し、複数のパネル(建設プロジェクト・各詳細)の表示を揃える。
    /// </summary>
    private void HandleSetObjectFilter(int[] hiddenBuildingIds, int[] hiddenPathIds)
    {
        ObjectVisibilityFilter.SetHidden(hiddenBuildingIds, hiddenPathIds);
        NotifyObjectFilterState();
    }

    /// <summary>現在の非表示集合をUIへ通知する。</summary>
    public void NotifyObjectFilterState()
    {
        IpcClient.Instance?.EmitViewObjectFilterState(
            ObjectVisibilityFilter.GetHiddenBuildings(),
            ObjectVisibilityFilter.GetHiddenPaths());
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
            case "STATE_MERGE_PATH":
            case "STATE_SPLIT_PATH":
            case "STATE_EDIT_ROAD_MEMBERS":
            case "STATE_EDIT_GRADING":
                ApplyConstructionPreset("border", null);
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
                    ApplyConstructionPreset("buildable_color", filterSubMode);
                }
                else
                {
                    ApplyConstructionPreset("border", null);
                }
                break;
            }

            case "STATE_EDIT_PARCEL":
                ApplyLandEditPreset("default", "default", "plan");
                break;

            case "STATE_EDIT_ZONE":
                // 地区編集の主役は「道路の地区色」と「地区の枠線」の2つだけにする。
                // 建物まで地区色で塗ると画面が地区色で埋まって道路の所属変化が読み取れないため、
                // 建物は通常表示のままにする(地区の広がりは枠線が示す)。
                // 計画中表示・公共施設ハイライトは編集の邪魔になるためOFF。
                ApplyLandEditPreset("zone_color", "default", "");
                break;

            case "STATE_EDIT_TRANSIT":
                ApplyTransitPreset();
                break;

            case "STATE_REMOVE_OBJECTS":
                // ブルドーザ(削除)モードは計画中表示で地区は非表示にする。
                // 区画は境界線表示にして、空き地(削除対象)が見えるようにする。
                // プロップは削除対象そのものなので消さない(建設系プリセットと違う点)。
                ForceLayer("parcel", "border");
                ForceLayer("road", "default", null, "plan");
                ForceLayer("building", "default", null, "plan");
                ForceLayer("district", "hidden");
                break;

            default:
                Debug.LogWarning($"IpcViewCoordinator: SyncFromState 不明なステート: {state}");
                return;
        }

        Debug.Log($"IpcViewCoordinator: SyncFromState -> {state} (currentState was {_currentState})");
        NotifyStateChanged();
    }

    /// <summary>
    /// ウィンドウ単位の表示プリセットを適用する(view.apply_preset)。
    /// 専用の編集ステートを持たないウィンドウが、開いている間だけ表示を差し替えるために使う。
    /// プリセットの内容は編集ステートと同じ ForceLayer 経由なので記憶(_normalStateMemory)を
    /// 汚さず、STATE_NORMAL へ戻った時点で元の表示に復帰する。
    /// </summary>
    public void ApplyPreset(string preset)
    {
        switch (preset)
        {
            case PRESET_TRANSIT:
                ApplyTransitPreset();
                break;
            case PRESET_PARCEL_DETAIL:
                ApplyParcelDetailPreset();
                break;
            default:
                Debug.LogWarning($"IpcViewCoordinator: 未知の表示プリセット: {preset}");
                return;
        }

        Debug.Log($"IpcViewCoordinator: ApplyPreset -> {preset}");
        NotifyStateChanged();
    }

    /// <summary>
    /// 土地区画詳細パネルが開いている間の表示。
    /// 選択中の区画や敷地の形状を確認できるよう、土地区画の境界線を強制表示する。
    /// </summary>
    private void ApplyParcelDetailPreset()
    {
        ForceLayer("parcel", "border");
    }

    /// <summary>
    /// ウィンドウ単位の表示プリセットを解除し、元の表示状態へ復帰する(view.restore_preset)。
    /// 編集ステートと併存しうる詳細パネルが、STATE_NORMAL を送らずに自身のプリセットだけを
    /// 安全に解除するために使う。
    /// </summary>
    public void RestorePreset(string preset)
    {
        switch (preset)
        {
            case PRESET_PARCEL_DETAIL:
                RestoreParcelDetailPreset();
                break;
            case PRESET_TRANSIT:
                RestoreTransitPreset();
                break;
            default:
                Debug.LogWarning($"IpcViewCoordinator: 未知の復帰プリセット: {preset}");
                return;
        }

        Debug.Log($"IpcViewCoordinator: RestorePreset -> {preset}");
        NotifyStateChanged();
    }

    private void RestoreParcelDetailPreset()
    {
        if (_currentState == "STATE_NORMAL")
        {
            RestoreLayerToNormal("parcel");
        }
        else
        {
            SyncFromState(_currentState);
        }
    }

    private void RestoreTransitPreset()
    {
        if (_currentState == "STATE_NORMAL")
        {
            RestoreLayerToNormal("road");
            RestoreLayerToNormal("building");
            RestoreLayerToNormal("transit");
        }
        else
        {
            SyncFromState(_currentState);
        }
    }

    /// <summary>
    /// 指定レイヤーを通常状態の記憶(_normalStateMemory または NORMAL_DEFAULTS)へ復元し、描画へ反映する。
    /// </summary>
    private void RestoreLayerToNormal(string layer)
    {
        if (layer == "terrain")
        {
            if (_normalStateMemory.TryGetValue("terrain", out var terrainMem))
            {
                _layerModes["terrain"] = terrainMem.mode;
                if (!string.IsNullOrEmpty(terrainMem.subMode))
                {
                    _layerSubModes["terrain"] = terrainMem.subMode;
                }
                else
                {
                    _layerSubModes.Remove("terrain");
                }
            }
            else
            {
                _layerSubModes.Remove("terrain");
            }
        }
        else if (NORMAL_DEFAULTS.TryGetValue(layer, out var defaultVal))
        {
            var value = _normalStateMemory.TryGetValue(layer, out var mem) ? mem : defaultVal;
            _layerModes[layer] = value.mode;
            if (!string.IsNullOrEmpty(value.subMode))
            {
                _layerSubModes[layer] = value.subMode;
            }
            else
            {
                _layerSubModes.Remove(layer);
            }
            if (!string.IsNullOrEmpty(value.flags))
            {
                _layerFlags[layer] = value.flags;
            }
            else
            {
                _layerFlags.Remove(layer);
            }
        }

        if (VisualModeHandler.Instance == null) return;
        string effectiveFlags = _layerFlags.TryGetValue(layer, out var f) ? f : "";
        ApplyLayerMode(layer, _layerModes[layer], _layerSubModes.TryGetValue(layer, out var s) ? s : null, effectiveFlags);
    }

    /// <summary>
    /// 編集ステート・表示プリセットがレイヤー1枚の表示を強制する。
    /// 記憶(_layerModes 等)の更新と実際の描画反映をここでまとめて行うため、
    /// ステート側(IpcStateCoordinator)では VisualModeHandler を直接触らないこと
    /// (二重管理になり、UIへ通知する表示状態と実際の見え方がずれる)。
    ///
    /// flags は view.change_view と同じ規約で、null=現在値を維持 / ""=全OFF / CSV=指定集合。
    /// ここでの変更は記憶(_normalStateMemory)には載せない。通常モードへ戻したときに
    /// ユーザーが自分で選んだ表示へ復帰させるため。
    /// </summary>
    private void ForceLayer(string layer, string mode, string subMode = null, string flags = null)
    {
        _layerModes[layer] = mode;
        if (string.IsNullOrEmpty(subMode))
        {
            _layerSubModes.Remove(layer);
        }
        else
        {
            _layerSubModes[layer] = subMode;
        }

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

        if (VisualModeHandler.Instance == null) return;
        string effectiveFlags = _layerFlags.TryGetValue(layer, out var f) ? f : "";
        ApplyLayerMode(layer, mode, _layerSubModes.TryGetValue(layer, out var s) ? s : null, effectiveFlags);
    }

    /// <summary>
    /// 道路・線路・インフラ・建物の敷設/建設中と、造成範囲の作図中の表示。
    /// 計画中の構造物と、位置決めの手がかりになるもの(区画・地区・等高線)をすべて出し、
    /// 視界を遮るプロップだけを消す。区画の色モードだけは呼び出し側が決める
    /// (建物建設=建設可能な区画の色分け、それ以外=境界線。造成は既存区画との重なりを境界線で見せる)。
    /// </summary>
    private void ApplyConstructionPreset(string parcelMode, string parcelSubMode)
    {
        ForceLayer("parcel", parcelMode, parcelSubMode);
        ForceLayer("road", "default", null, "plan");
        ForceLayer("building", "default", null, "plan");
        ForceLayer("district", "border_icon");
        ForceLayer("prop", "hidden");
        // 等高線だけONにする。地形の色モード(標高色分け等)はユーザーの選択を尊重して触らない。
        ForceLayer("terrain", _layerModes["terrain"], "contour_on");
    }

    /// <summary>
    /// 地区編集・土地区画編集中の表示。区画・地区を出して、
    /// どの区画がどの地区に属するかを地表で確認できるようにする。
    /// 道路・建物の色モードとフラグは呼び出し側が決める
    /// (地区編集=道路だけ地区色分け+計画中OFF、区画編集=通常表示+計画中ON)。
    /// </summary>
    private void ApplyLandEditPreset(string roadMode, string buildingMode, string flags)
    {
        ForceLayer("parcel", "border");
        ForceLayer("road", roadMode, null, flags);
        ForceLayer("building", buildingMode, null, flags);
        ForceLayer("district", "border_icon");
    }

    /// <summary>
    /// 公共交通の一覧・路線設置中の表示。既設の路線を全種別出し、
    /// 計画中の道路・建物も見えるようにする(停留所は建設中のパス上にも置けるため)。
    /// 停留所は既存パス上にしか置けないので、区画・地区の表示には手を出さない。
    /// </summary>
    private void ApplyTransitPreset()
    {
        ForceLayer("road", "default", null, "plan");
        ForceLayer("building", "default", null, "plan");

        string allVariants = AllTransitVariantsCsv();
        if (string.IsNullOrEmpty(allVariants))
        {
            // マスター未取得のときは種別を絞れないので、表示状態には触れない。
            return;
        }
        ForceLayer("transit", "default", allVariants);
    }

    /// <summary>
    /// 公共交通マスターの全路線種別(transit_path_id)をCSVで返す。
    /// 表示レイヤー transit のサブモードは「表示する種別の集合」なので、
    /// 「すべて表示」は全IDを並べた文字列で表す(UIの種別トグルと同じ表現)。
    /// </summary>
    private string AllTransitVariantsCsv()
    {
        var master = MasterDataStore.Instance?.GetTransitMasterDictionary();
        if (master == null || master.Count == 0) return null;

        var ids = new List<string>(master.Count);
        foreach (var item in master.Values)
        {
            ids.Add(item.transit_path_id.ToString());
        }
        return string.Join(",", ids);
    }

    /// <summary>
    /// STATE_NORMAL に戻ったとき、_normalStateMemory に記憶した表示状態を復元する。
    /// 記憶がないレイヤーは NORMAL_DEFAULTS(=UI側の初期値と一致)へ戻す。
    ///
    /// **編集ステート・表示プリセットが強制するレイヤーは必ずここに含めること。**
    /// 含め忘れると「記憶が無ければ現在値を維持」になり、編集中の表示が通常モードへ残る。
    /// 実際の描画反映は呼び出し元(IpcStateCoordinator)の ApplyAllLayerModes が行う。
    /// </summary>
    private void RestoreNormalLayerModes()
    {
        foreach (var entry in NORMAL_DEFAULTS)
        {
            string layer = entry.Key;
            var value = _normalStateMemory.TryGetValue(layer, out var mem) ? mem : entry.Value;

            _layerModes[layer] = value.mode;
            if (!string.IsNullOrEmpty(value.subMode))
            {
                _layerSubModes[layer] = value.subMode;
            }
            else
            {
                _layerSubModes.Remove(layer);
            }
            if (!string.IsNullOrEmpty(value.flags))
            {
                _layerFlags[layer] = value.flags;
            }
            else
            {
                _layerFlags.Remove(layer);
            }
        }

        // 地形は色モードを強制しない(編集中に触るのは等高線のサブモードだけ)ので、
        // 記憶が無ければ色モードは現在値のままにして等高線だけOFFへ戻す。
        if (_normalStateMemory.TryGetValue("terrain", out var terrainMem))
        {
            _layerModes["terrain"] = terrainMem.mode;
            if (!string.IsNullOrEmpty(terrainMem.subMode))
            {
                _layerSubModes["terrain"] = terrainMem.subMode;
            }
            else
            {
                _layerSubModes.Remove("terrain");
            }
        }
        else
        {
            _layerSubModes.Remove("terrain");
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
            case "road": ApplyRoadMode(mode, subMode, flags); break;
            case "building": ApplyBuildingMode(mode, subMode, flags); break;
            case "parcel": ApplyParcelMode(mode, subMode); break;
            case "district": ApplyDistrictMode(mode); break;
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
                case "speed_limit":
                    result |= VisualFlag.SPEED_LIMIT_SIGNS;
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
            case "noise_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualModeParcel.NOISE);
                break;
            case "air_pollution_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualModeParcel.AIR_POLLUTION);
                break;
            case "crime_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualModeParcel.CRIME);
                break;
            case "sunshine_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualModeParcel.SUNSHINE);
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
        if (mode != "hidden" && !string.IsNullOrEmpty(subMode))
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
        // 表示切替のたびに路線一覧を取り直す。UI は路線カラーの保存・路線の削除後にこのイベントを
        // 再送するので、TransitDataStore の定期取得を待たずに路線色帯が最新の色・構成へ追随する。
        TransitOverlayManager.Instance.RefreshLines();
    }

    private void ApplyPropMode(string mode)
    {
        if (PropManager.Instance != null)
        {
            PropManager.Instance.SetVisible(mode == "visible");
        }
    }

    /// <summary>
    /// アラートレイヤーの表示フィルタを適用する。
    /// subMode はアラート種別のビットマスク(下位バイト=ALERT_AGENT_*、
    /// 上位バイト=ALERT_PATH_* を ALERT_PATH_MASK_SHIFT シフトしたもの)。未指定なら全種別表示。
    /// </summary>
    private void ApplyAlertMode(string mode, string subMode = null)
    {
        if (AlertManager.Instance != null)
        {
            int mask = AlertManager.DefaultFilterMask;
            if (!string.IsNullOrEmpty(subMode))
            {
                int.TryParse(subMode, out mask);
            }
            AlertManager.Instance.SetFilterMask(mask);
        }
    }

}
