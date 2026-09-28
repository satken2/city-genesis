using UnityEngine;

/// <summary>
/// IpcClientの受信イベントを各Editor Managerのメソッドにルーティングするハンドラ。
/// </summary>
public class IpcEventHandler : MonoBehaviour
{
    private RoadPlanManager _roadPlanManager;
    private BuildingPlanManager _buildingPlanManager;
    private ResourceAreaEditManager _resourceAreaEditManager;
    private ParcelManager _parcelManager;
    private readonly HighlightManager _highlightManager = new HighlightManager();

    private void OnEnable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnBuildingExploded += HandleBuildingExploded;
            IpcClient.Instance.OnEditPathChangeNode += HandleEditPathChangeNode;
            IpcClient.Instance.OnEditPathChangeEmbankment += HandleEditPathChangeEmbankment;
            IpcClient.Instance.OnEditPathFinish += HandleEditPathFinish;
            IpcClient.Instance.OnEditPathChangePlatformSide += HandleEditPathChangePlatformSide;
            IpcClient.Instance.OnEditPathChangeHouseEnds += HandleEditPathChangeHouseEnds;
            IpcClient.Instance.OnEditPathChangePassageMode += HandleEditPathChangePassageMode;
            IpcClient.Instance.OnEditPathSetFollowTerrain += HandleEditPathSetFollowTerrain;
            IpcClient.Instance.OnEditPathChangeLateralOffsets += HandleEditPathChangeLateralOffsets;
            IpcClient.Instance.OnEditPathShapeCommand += HandleEditPathShapeCommand;
            IpcClient.Instance.OnEditPathQueryShape += HandleEditPathQueryShape;
            IpcClient.Instance.OnEditResourceFinish += HandleEditResourceFinish;
            IpcClient.Instance.OnEditTransitFinish += HandleEditTransitFinish;
            IpcClient.Instance.OnEditPropChangeSettings += HandleEditPropChangeSettings;
            IpcClient.Instance.OnEditPropFinish += HandleEditPropFinish;
            IpcClient.Instance.OnEditPropReset += HandleEditPropReset;
            IpcClient.Instance.OnRemoveObjectConfirmResult += HandleRemoveObjectConfirmResult;
            IpcClient.Instance.OnEditBuildingUpdateShape += HandleEditBuildingUpdateShape;
            IpcClient.Instance.OnEditBuildingPlacementCommand += HandleEditBuildingPlacementCommand;
            IpcClient.Instance.OnEditBuildingUpdateFloors += HandleEditBuildingUpdateFloors;
            IpcClient.Instance.OnEditBuildingUpdateVisualVariant += HandleEditBuildingUpdateVisualVariant;
            IpcClient.Instance.OnEditBuildingUpdateStructure += HandleEditBuildingUpdateStructure;
            IpcClient.Instance.OnEditBuildingUpdateLandmark += HandleEditBuildingUpdateLandmark;
            IpcClient.Instance.OnEditBuildingUpdateEffectRange += HandleEditBuildingUpdateEffectRange;
            IpcClient.Instance.OnEditBuildingUpdateDirectParams += HandleEditBuildingUpdateDirectParams;
            IpcClient.Instance.OnParcelShowFrontPath += HandleParcelShowFrontPath;
            IpcClient.Instance.OnParcelHideFrontPath += HandleParcelHideFrontPath;
            IpcClient.Instance.OnWorldHighlightObjects += HandleWorldHighlightObjects;
            IpcClient.Instance.OnWorldClearHighlights += HandleWorldClearHighlights;
            IpcClient.Instance.OnWorldSetRoadFocus += HandleWorldSetRoadFocus;
            IpcClient.Instance.OnWorldClearRoadFocus += HandleWorldClearRoadFocus;
            IpcClient.Instance.OnWorldRefreshRoads += HandleWorldRefreshRoads;
            IpcClient.Instance.OnEditRoadSetMembers += HandleEditRoadSetMembers;
        }
    }

    private void OnDisable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnBuildingExploded -= HandleBuildingExploded;
            IpcClient.Instance.OnEditPathChangeNode -= HandleEditPathChangeNode;
            IpcClient.Instance.OnEditPathChangeEmbankment -= HandleEditPathChangeEmbankment;
            IpcClient.Instance.OnEditPathFinish -= HandleEditPathFinish;
            IpcClient.Instance.OnEditPathChangePlatformSide -= HandleEditPathChangePlatformSide;
            IpcClient.Instance.OnEditPathChangeHouseEnds -= HandleEditPathChangeHouseEnds;
            IpcClient.Instance.OnEditPathChangePassageMode -= HandleEditPathChangePassageMode;
            IpcClient.Instance.OnEditPathSetFollowTerrain -= HandleEditPathSetFollowTerrain;
            IpcClient.Instance.OnEditPathChangeLateralOffsets -= HandleEditPathChangeLateralOffsets;
            IpcClient.Instance.OnEditPathShapeCommand -= HandleEditPathShapeCommand;
            IpcClient.Instance.OnEditPathQueryShape -= HandleEditPathQueryShape;
            IpcClient.Instance.OnEditResourceFinish -= HandleEditResourceFinish;
            IpcClient.Instance.OnEditTransitFinish -= HandleEditTransitFinish;
            IpcClient.Instance.OnEditPropChangeSettings -= HandleEditPropChangeSettings;
            IpcClient.Instance.OnEditPropFinish -= HandleEditPropFinish;
            IpcClient.Instance.OnEditPropReset -= HandleEditPropReset;
            IpcClient.Instance.OnRemoveObjectConfirmResult -= HandleRemoveObjectConfirmResult;
            IpcClient.Instance.OnEditBuildingUpdateShape -= HandleEditBuildingUpdateShape;
            IpcClient.Instance.OnEditBuildingPlacementCommand -= HandleEditBuildingPlacementCommand;
            IpcClient.Instance.OnEditBuildingUpdateFloors -= HandleEditBuildingUpdateFloors;
            IpcClient.Instance.OnEditBuildingUpdateVisualVariant -= HandleEditBuildingUpdateVisualVariant;
            IpcClient.Instance.OnEditBuildingUpdateStructure -= HandleEditBuildingUpdateStructure;
            IpcClient.Instance.OnEditBuildingUpdateLandmark -= HandleEditBuildingUpdateLandmark;
            IpcClient.Instance.OnEditBuildingUpdateEffectRange -= HandleEditBuildingUpdateEffectRange;
            IpcClient.Instance.OnEditBuildingUpdateDirectParams -= HandleEditBuildingUpdateDirectParams;
            IpcClient.Instance.OnParcelShowFrontPath -= HandleParcelShowFrontPath;
            IpcClient.Instance.OnParcelHideFrontPath -= HandleParcelHideFrontPath;
            IpcClient.Instance.OnWorldHighlightObjects -= HandleWorldHighlightObjects;
            IpcClient.Instance.OnWorldClearHighlights -= HandleWorldClearHighlights;
            IpcClient.Instance.OnWorldSetRoadFocus -= HandleWorldSetRoadFocus;
            IpcClient.Instance.OnWorldClearRoadFocus -= HandleWorldClearRoadFocus;
            IpcClient.Instance.OnWorldRefreshRoads -= HandleWorldRefreshRoads;
            IpcClient.Instance.OnEditRoadSetMembers -= HandleEditRoadSetMembers;
        }
    }

    private void HandleBuildingExploded(int agentId)
    {
        CellDataLoader.Instance?.FetchBuilding(agentId, playExplosion: true);
    }

    /// <summary>
    /// edit_path.change_node → RoadPlanManager.OnChangeNodeFromIpc()
    /// パネルの標高ステッパーからの変更を選択中ノード(または次回配置値)に反映する。
    /// </summary>
    private void HandleEditPathChangeNode(int nodeIndex, float height)
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.OnChangeNodeFromIpc(nodeIndex, height);
        }
    }

    /// <summary>
    /// edit_path.change_embankment → RoadPlanManager.OnChangeEmbankmentFromIpc()
    /// パネルの盛土ステッパーからの変更を選択中ノード(または次回配置値)に反映する。
    /// </summary>
    private void HandleEditPathChangeEmbankment(int nodeIndex, float embankmentHeight)
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.OnChangeEmbankmentFromIpc(nodeIndex, embankmentHeight);
        }
    }

    /// <summary>
    /// edit_path.finish → RoadPlanManager.FinishRoadPlacement() + CellDataLoader.ReloadAllData()
    /// Tauri側が直接バックエンドに送信し終わった後に呼ばれ、Unity側の編集状態をリセットし、
    /// 最新データをバックエンドから取得して3D表示を更新する。
    /// </summary>
    private void HandleEditPathFinish()
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.FinishRoadPlacement();
        }
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
    }

    /// <summary>
    /// edit_path.change_platform_side → RoadPlanManager.SetPlatformSide()
    /// UIパネルのホーム設置側トグルを受けて、敷設中の駅パスプレビューの
    /// プラットフォーム張り出し側を即座に切り替える。
    /// </summary>
    private void HandleEditPathChangePassageMode(string mode)
    {
        FindAnyObjectByType<RoadPlanManager>()?.SetPassageMode(mode);
    }

    private void HandleEditPathChangeHouseEnds(string ends)
    {
        FindAnyObjectByType<RoadPlanManager>()?.SetHouseEnds(ends);
    }

    private void HandleEditPathChangePlatformSide(int platformSide)
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.SetPlatformSide(platformSide);
        }
    }

    /// <summary>
    /// edit_path.set_follow_terrain → RoadPlanManager.SetFollowTerrain()
    /// UIパネルの地形追従トグル(-1=未設定/0=非追従/1=追従)を受けて、敷設中プレビューの
    /// 地形追従・トンネル可視化を即座に切り替える。
    /// </summary>
    private void HandleEditPathSetFollowTerrain(int value)
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.SetFollowTerrain(value);
        }
    }

    /// <summary>
    /// edit_path.change_lateral_offsets → RoadPlanManager.SetLateralOffsets()
    /// UIパネルの線路数・進行方向の変更を受けて、敷設中の複合パス(軌道複線)プレビューの
    /// 平行本数・間隔・進行方向(ホーム設置側・矢印)を即座に切り替える。
    /// 空配列は単一パスに戻すことを意味する。
    /// </summary>
    private void HandleEditPathChangeLateralOffsets(float[] lateralOffsets, int[] travelDirections)
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.SetLateralOffsets(lateralOffsets, travelDirections);
        }
    }

    /// <summary>
    /// edit_path.shape_command → RoadPlanManager.ApplyShapeCommand()
    /// 2Dパス形状編集パネルからのノード・制御点操作。結果は update_shape で返る。
    /// </summary>
    private void HandleEditPathShapeCommand(RoadPlanManager.ShapeCommand cmd)
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.ApplyShapeCommand(cmd);
        }
    }

    /// <summary>
    /// edit_path.query_shape → RoadPlanManager.EmitShapeUpdateOnRequest()
    /// 2Dパス形状編集パネルが開いた直後に現在形状を問い合わせる。
    /// </summary>
    private void HandleEditPathQueryShape()
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.EmitShapeUpdateOnRequest();
        }
    }

    /// <summary>
    /// edit_resource.finish → ResourceAreaEditManager.FinishEditMode() + CellDataLoader.ReloadAllData()
    /// Tauri側が直接バックエンドに POST /api/v1/resources を送信し終わった後に呼ばれ、
    /// Unity側の編集状態をリセットし、最新データをバックエンドから取得して3D表示を更新する。
    /// </summary>
    private void HandleEditResourceFinish()
    {
        var ream = GetResourceAreaEditManager();
        if (ream != null)
        {
            ream.FinishEditMode();
        }
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
    }

    /// <summary>
    /// edit_transit.finish → TransitPlanManager.FinishPlacement() + CellDataLoader.ReloadAllData()
    /// Tauri側が直接バックエンドに POST /api/v1/transit/lines を送信し終わった後に呼ばれ、
    /// Unity側の停留所配置状態をリセットし、最新データをバックエンドから取得して3D表示を更新する
    /// (新設された公共交通パスがオーバーレイに反映される)。
    /// </summary>
    private void HandleEditTransitFinish()
    {
        var tpm = FindAnyObjectByType<TransitPlanManager>();
        if (tpm != null)
        {
            tpm.FinishPlacement();
        }
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
        // 新設された公共交通パスをオーバーレイに反映させる。作成直後に route_path_ids(実走行経路)を
        // 取り直して即時反映する(取得完了で自動的に再構築される)。
        TransitOverlayManager.Instance?.RefreshLines();
    }

    /// <summary>
    /// edit_prop.change_settings → PropPlacementManager.OnChangeSettingsFromIpc()
    /// UI側の回転/スケール/密度/seed 変更を受けて、配置中のゴースト/散布プレビューに即反映する。
    /// 送られてきたキーのみを含む辞書(rotation? / scale? / density? / seed?)を渡す。
    /// </summary>
    private void HandleEditPropChangeSettings(System.Collections.Generic.Dictionary<string, object> settings)
    {
        var ppm = FindAnyObjectByType<PropPlacementManager>();
        if (ppm != null)
        {
            ppm.OnChangeSettingsFromIpc(settings);
        }
    }

    /// <summary>
    /// edit_prop.finish → PropPlacementManager.FinishPlacement() + CellDataLoader.ReloadAllData()
    /// Tauri側が直接バックエンドに POST /api/v1/props/... を送信し終わった後に呼ばれ、
    /// Unity側の配置状態(ゴースト/散布プレビュー)をリセットし、最新データを取得して3D表示を更新する。
    /// </summary>
    private void HandleEditPropFinish()
    {
        var ppm = FindAnyObjectByType<PropPlacementManager>();
        if (ppm != null)
        {
            ppm.FinishPlacement();
        }
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
    }

    /// <summary>
    /// edit_prop.reset → PropPlacementManager.ResetPlacement()
    /// UI側のリセットボタン押下を受けて、配置済みのインスタンス・頂点・仮確定状態をすべてクリアする。
    /// </summary>
    private void HandleEditPropReset()
    {
        var ppm = FindAnyObjectByType<PropPlacementManager>();
        if (ppm != null)
        {
            ppm.ResetPlacement();
        }
    }

    /// <summary>
    /// remove_object.confirm_result → RemovalManager.OnConfirmResult()
    /// ブルドーザモードで確認を求めた対象(範囲配置プロップ)について、UIの確認ダイアログの回答を受け取る。
    /// accepted なら RemovalManager が DELETE を発行して差分リロードする。
    /// </summary>
    private void HandleRemoveObjectConfirmResult(string objectType, int id, bool accepted)
    {
        RemovalManager.Instance?.OnConfirmResult(objectType, id, accepted);
    }

    /// <summary>
    /// edit_building.update_shape → BuildingPlanManager.OnUpdateShapeFromIpc()
    /// UI側がバックエンドのプレビューエンドポイントから取得した形状データ
    /// （polygon, floors, center_point, height）をそのままBuildingPlanManagerに渡す。
    /// </summary>
    private void HandleEditBuildingPlacementCommand(Newtonsoft.Json.Linq.JToken command)
    {
        GetBuildingPlanManager()?.OnPlacementCommand(command);
    }

    private void HandleEditBuildingUpdateShape(float[][] polygon, int floors, float[] centerPoint, float height, int revision)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.OnUpdateShapeFromIpc(polygon, floors, centerPoint, height, revision);
        }
    }

    /// <summary>
    /// edit_building.update_floors → BuildingPlanManager.SetFloors()
    /// </summary>
    private void HandleEditBuildingUpdateFloors(int floors)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.SetFloors(floors);
        }
    }

    /// <summary>
    /// edit_building.update_visual_variant → BuildingPlanManager.SetVisualVariant()
    /// UI側の外観乱数スライダー変更を受けて、Unity側の3Dプレビューメッシュを再生成する。
    /// </summary>
    private void HandleEditBuildingUpdateVisualVariant(float vv1, float vv2, float vv3)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.SetVisualVariant(vv1, vv2, vv3);
        }
    }

    /// <summary>
    /// edit_building.update_structure → BuildingPlanManager.SetStructure()
    /// UI側の構造種別の選択変更を受けて、構造に応じたパーツでプレビューメッシュを再生成する。
    /// </summary>
    private void HandleEditBuildingUpdateStructure(int structure)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.SetStructure(structure);
        }
    }

    /// <summary>
    /// edit_building.update_landmark → BuildingPlanManager.SetLandmark()
    /// テンプレート作成モードの観光名所の選択変更を受けて、名所専用のパーツでプレビューメッシュを再生成する。
    /// </summary>
    private void HandleEditBuildingUpdateLandmark(int landmark)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.SetLandmark(landmark);
        }
    }

    /// <summary>
    /// edit_building.update_effect_range → BuildingPlanManager.OnUpdateEffectRangeFromIpc()
    /// UI側が取得したインフラ施設の効果範囲を、Unityの3D上にオーバーレイ表示する。
    /// </summary>
    private void HandleEditBuildingUpdateEffectRange(EffectRangePayload payload)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.OnUpdateEffectRangeFromIpc(payload);
        }
    }

    /// <summary>
    /// edit_building.update_direct_params → BuildingPlanManager.SetDirectParams()
    /// UI側の直接配置パラメータ(横幅・縦幅・回転角・階数)の変更を受けて、
    /// Unity側のカーソル追従リアルタイム矩形ゴーストを更新する。
    /// </summary>
    private void HandleEditBuildingUpdateDirectParams(float width, float depth, float angle, int floors)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.SetDirectParams(width, depth, angle, floors);
        }
    }

    /// <summary>
    /// parcel:show_front_path → ParcelManager.ShowFrontPathIndicator()
    /// 土地区画詳細パネルを開いた際に、選択中区画の接道を青い矢印で3D表示する。
    /// </summary>
    private void HandleParcelShowFrontPath(int parcelId)
    {
        var pm = GetParcelManager();
        if (pm != null)
        {
            pm.ShowFrontPathIndicator(parcelId);
        }
    }

    /// <summary>
    /// parcel:hide_front_path → ParcelManager.HideFrontPathIndicator()
    /// 土地区画詳細パネルを閉じた際に、接道の矢印表示をクリアする。
    /// </summary>
    private void HandleParcelHideFrontPath()
    {
        var pm = GetParcelManager();
        if (pm != null)
        {
            pm.HideFrontPathIndicator();
        }
    }

    /// <summary>
    /// world:highlight_objects → HighlightManager.Apply()
    /// 計画中パスが干渉する区画・建物・既存パスを3Dビュー上でハイライトする。
    /// 敷設の形状確定/確定ボタン、着工前パスの詳細パネル表示から送られる。
    /// </summary>
    private void HandleWorldHighlightObjects(string source, PathInterferencePayload interference)
    {
        _highlightManager.Apply(source, interference);
    }

    /// <summary>
    /// world:clear_highlights → HighlightManager.Clear()
    /// 指定発信元の干渉ハイライトを解除する。敷設終了・詳細パネルを閉じたときに送られる。
    /// </summary>
    private void HandleWorldClearHighlights(string source)
    {
        _highlightManager.Clear(source);
    }

    /// <summary>
    /// world.set_road_focus → RoadFocusOverlay.SetFocus()
    /// 道路詳細パネルが開いている道路の区間を道路色の帯で強調する(干渉用の world:highlight_objects とは別系統)。
    /// </summary>
    private void HandleWorldSetRoadFocus(int roadId, string color, int[] pathIds)
    {
        RoadFocusOverlay.Ensure().SetFocus(roadId, RoadManager.ParseColor(color), pathIds);
    }

    /// <summary>
    /// world.clear_road_focus → RoadFocusOverlay.ClearFocus()。
    /// road_id 付きならその道路の強調だけを消す(別の道路詳細パネルを閉じても、開いている方の強調を残すため)。
    /// </summary>
    private void HandleWorldClearRoadFocus(int roadId)
    {
        RoadFocusOverlay.Instance?.ClearFocus(roadId);
    }

    /// <summary>world.refresh_roads → RoadManager.RequestRefresh()(version が同じならラベルは作り直さない)</summary>
    private void HandleWorldRefreshRoads()
    {
        RoadManager.Instance?.RequestRefresh();
    }

    /// <summary>edit_road.set_members → RoadMemberEditManager.SetMembers()(区間編集モード中でなければ無視)</summary>
    private void HandleEditRoadSetMembers(int roadId, int[] pathIds, int[] previewPathIds)
    {
        RoadMemberEditManager.Instance?.SetMembers(roadId, pathIds, previewPathIds);
    }

    private RoadPlanManager GetRoadPlanManager()
    {
        if (_roadPlanManager == null)
        {
            _roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        }
        return _roadPlanManager;
    }

    private BuildingPlanManager GetBuildingPlanManager()
    {
        if (_buildingPlanManager == null)
        {
            _buildingPlanManager = FindAnyObjectByType<BuildingPlanManager>();
        }
        return _buildingPlanManager;
    }

    private ResourceAreaEditManager GetResourceAreaEditManager()
    {
        if (_resourceAreaEditManager == null)
        {
            _resourceAreaEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
        }
        return _resourceAreaEditManager;
    }

    private ParcelManager GetParcelManager()
    {
        if (_parcelManager == null)
        {
            _parcelManager = FindAnyObjectByType<ParcelManager>();
        }
        return _parcelManager;
    }
}
