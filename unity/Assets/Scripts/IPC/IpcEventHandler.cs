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

    private void OnEnable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnEditPathChangeNode += HandleEditPathChangeNode;
            IpcClient.Instance.OnEditPathChangeEmbankment += HandleEditPathChangeEmbankment;
            IpcClient.Instance.OnEditPathFinish += HandleEditPathFinish;
            IpcClient.Instance.OnEditPathChangePlatformSide += HandleEditPathChangePlatformSide;
            IpcClient.Instance.OnEditPathSetFollowTerrain += HandleEditPathSetFollowTerrain;
            IpcClient.Instance.OnEditPathChangeLateralOffsets += HandleEditPathChangeLateralOffsets;
            IpcClient.Instance.OnEditResourceFinish += HandleEditResourceFinish;
            IpcClient.Instance.OnEditTransitFinish += HandleEditTransitFinish;
            IpcClient.Instance.OnEditPropChangeSettings += HandleEditPropChangeSettings;
            IpcClient.Instance.OnEditPropFinish += HandleEditPropFinish;
            IpcClient.Instance.OnEditPropReset += HandleEditPropReset;
            IpcClient.Instance.OnEditStationChangeVariant += HandleEditStationChangeVariant;
            IpcClient.Instance.OnEditStationFinish += HandleEditStationFinish;
            IpcClient.Instance.OnEditBuildingUpdateShape += HandleEditBuildingUpdateShape;
            IpcClient.Instance.OnEditBuildingUpdateFloors += HandleEditBuildingUpdateFloors;
            IpcClient.Instance.OnEditBuildingUpdateVisualVariant += HandleEditBuildingUpdateVisualVariant;
            IpcClient.Instance.OnEditBuildingUpdateEffectRange += HandleEditBuildingUpdateEffectRange;
            IpcClient.Instance.OnEditBuildingUpdateDirectParams += HandleEditBuildingUpdateDirectParams;
            IpcClient.Instance.OnParcelShowFrontPath += HandleParcelShowFrontPath;
            IpcClient.Instance.OnParcelHideFrontPath += HandleParcelHideFrontPath;
        }
    }

    private void OnDisable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnEditPathChangeNode -= HandleEditPathChangeNode;
            IpcClient.Instance.OnEditPathChangeEmbankment -= HandleEditPathChangeEmbankment;
            IpcClient.Instance.OnEditPathFinish -= HandleEditPathFinish;
            IpcClient.Instance.OnEditPathChangePlatformSide -= HandleEditPathChangePlatformSide;
            IpcClient.Instance.OnEditPathSetFollowTerrain -= HandleEditPathSetFollowTerrain;
            IpcClient.Instance.OnEditPathChangeLateralOffsets -= HandleEditPathChangeLateralOffsets;
            IpcClient.Instance.OnEditResourceFinish -= HandleEditResourceFinish;
            IpcClient.Instance.OnEditTransitFinish -= HandleEditTransitFinish;
            IpcClient.Instance.OnEditPropChangeSettings -= HandleEditPropChangeSettings;
            IpcClient.Instance.OnEditPropFinish -= HandleEditPropFinish;
            IpcClient.Instance.OnEditPropReset -= HandleEditPropReset;
            IpcClient.Instance.OnEditStationChangeVariant -= HandleEditStationChangeVariant;
            IpcClient.Instance.OnEditStationFinish -= HandleEditStationFinish;
            IpcClient.Instance.OnEditBuildingUpdateShape -= HandleEditBuildingUpdateShape;
            IpcClient.Instance.OnEditBuildingUpdateFloors -= HandleEditBuildingUpdateFloors;
            IpcClient.Instance.OnEditBuildingUpdateVisualVariant -= HandleEditBuildingUpdateVisualVariant;
            IpcClient.Instance.OnEditBuildingUpdateEffectRange -= HandleEditBuildingUpdateEffectRange;
            IpcClient.Instance.OnEditBuildingUpdateDirectParams -= HandleEditBuildingUpdateDirectParams;
            IpcClient.Instance.OnParcelShowFrontPath -= HandleParcelShowFrontPath;
            IpcClient.Instance.OnParcelHideFrontPath -= HandleParcelHideFrontPath;
        }
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
    /// edit_station.change_variant → StationPlacementManager.OnChangeVariant()
    /// UI側で駅舎モデル(house_variant)を切り替えたとき、ゴーストを新しいモデルに作り直す。
    /// </summary>
    private void HandleEditStationChangeVariant(int houseVariant)
    {
        var spm = FindAnyObjectByType<StationPlacementManager>();
        if (spm != null)
        {
            spm.OnChangeVariant(houseVariant);
        }
    }

    /// <summary>
    /// edit_station.finish → StationPlacementManager.FinishPlacement() + CellDataLoader.ReloadAllData()
    /// UI側が PATCH /api/v1/stations/{id} を送った後に届く。ゴーストを片付け、差分リロードで
    /// 設置済み(または設置予定→設置確定後)の駅舎を描画する。
    /// </summary>
    private void HandleEditStationFinish()
    {
        var spm = FindAnyObjectByType<StationPlacementManager>();
        if (spm != null)
        {
            spm.FinishPlacement();
        }
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
    }

    /// <summary>
    /// edit_building.update_shape → BuildingPlanManager.OnUpdateShapeFromIpc()
    /// UI側がバックエンドのプレビューエンドポイントから取得した形状データ
    /// （polygon, floors, center_point, height）をそのままBuildingPlanManagerに渡す。
    /// </summary>
    private void HandleEditBuildingUpdateShape(float[][] polygon, int floors, float[] centerPoint, float height)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.OnUpdateShapeFromIpc(polygon, floors, centerPoint, height);
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
