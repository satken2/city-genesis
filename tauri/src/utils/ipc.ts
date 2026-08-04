/**
 * IPC ユーティリティ
 *
 * グラフィックプログラム(Unity)とのMemoryMappedFile IPC通信用関数。
 * Tauriコマンド(invoke)経由でRustバックエンドの send_window_action に渡し、
 * パイプを通じてUnityに送信する。イベント名はコロン区切り（例: `view:change_view`）。
 *
 * イベントの定義は genesis-docs/programs/IPC連携の整理.md に基づく。
 */

import { invoke } from "@tauri-apps/api/core";

/**
 * グラフィックプログラムのステート種別
 * @see genesis-docs/programs/IPC連携の整理.md「グラフィックプログラムのステート」
 */
export type GraphicState =
  | "STATE_NORMAL"
  | "STATE_EDIT_PATH"
  | "STATE_EDIT_BUILDING"
  | "STATE_EDIT_ZONE"
  | "STATE_EDIT_PARCEL"
  | "STATE_EDIT_PARCEL_SHAPE"
  | "STATE_UNLOCK_CELL"
  | "STATE_SET_GRID"
  | "STATE_EDIT_RESOURCE"
  | "STATE_EDIT_TRANSIT"
  | "STATE_EDIT_PROP"
  | "STATE_EDIT_CONNECTOR"
  | "STATE_EDIT_STATION"
  | "STATE_REMOVE_OBJECTS";

/* ---- ui -> graphic 方向の送信 ---- */

/**
 * `view.change_view`
 * 表示オプションで選択した表示モードをグラフィックプログラムに通知する。
 */
export async function sendChangeDisplay(
  layer: string,
  mode: string,
  subMode?: string | null,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "display_options",
    entity: null,
    action: "view:change_view",
    params: { layer, mode, sub_mode: subMode ?? null },
  });
}

/**
 * `world.change_state`
 * グラフィックプログラムのステート（通常モード、建物建設モード等）を切り替える。
 * extraに追加パラメータを渡すと、paramsにマージされて送信される。
 * 例: 道路敷設モード時に { path_type: 1, path_variant: 3 } を渡すと
 * Unity側でRoadPlanManagerに反映される。
 *
 * windowType は送信元ウィンドウの種別。省略時は "path_build" を使用する。
 */
export async function sendChangeState(
  state: GraphicState,
  extra?: Record<string, unknown>,
  windowType: string = "path_build",
): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "world:change_state",
    params: { state, ...extra },
  });
}

/**
 * `edit_path.change_node`
 * パス敷設モードにおいて、ノードの標高(相対高さ)を変更する。
 * 曲線/直線・吸着はUnity側のラジアルメニューが真実源だが、標高・盛土は既存ノードを選択して
 * 数値で微調整する用途があるため、パネルのステッパー(この関数)とUnityキー操作の双方向で編集できる。
 * nodeIndex=-1 は「次に配置するノード」の標高を指す。
 */
export async function sendEditPathChangeNode(nodeIndex: number, height: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "edit_path:change_node",
    params: { node_index: nodeIndex, height },
  });
}

/**
 * `edit_path.change_embankment`
 * パス敷設モードにおいて、ノードの盛土オフセット(-5〜+5m)を変更する。
 * 盛土は全道路タイプで利用可能（高架不可の街路でも盛土は可能）。nodeIndex=-1 は次回配置ノード。
 */
export async function sendEditPathChangeEmbankment(nodeIndex: number, embankmentHeight: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "edit_path:change_embankment",
    params: { node_index: nodeIndex, embankment_height: embankmentHeight },
  });
}

/**
 * `edit_building.update_shape`
 * 建物建設モードにおいて、建物の底面形状を変更する。
 * バックエンドのプレビューエンドポイントから取得した形状データを
 * Unity側の3Dプレビュー表示に反映するために使用する。
 * center_pointとheightはプレビューメッシュの配置位置とスケールに必要。
 */
export async function sendEditBuildingUpdateShape(
  polygon: number[][],
  floors: number,
  centerPoint?: number[],
  height?: number,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_shape",
    params: {
      polygon,
      floors,
      center_point: centerPoint ?? null,
      height: height ?? null,
    },
  });
}

/**
 * `edit_building.update_direct_params`
 * 直接配置モードにおいて、矩形の横幅・縦幅・回転角・階数をUnityへ同期する。
 * Unity側はこれらのパラメータでカーソル追従のリアルタイム矩形ゴーストを描画する。
 * バリアント選択時、および各値の変更時に送信する(地点確定前後どちらでも)。
 */
export async function sendEditBuildingUpdateDirectParams(
  width: number,
  depth: number,
  angle: number,
  floors: number,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_direct_params",
    params: { width, depth, angle, floors },
  });
}

/**
 * `edit_building.update_floors`
 * 建物建設モードにおいて、建物の階数を変更する。
 */
export async function sendEditBuildingUpdateFloors(floors: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_floors",
    params: { floors },
  });
}

/**
 * `edit_building.update_visual_variant`
 * 建物建設モードにおいて、外観バリアント(visual_variant1/2/3)を変更する。
 * Unity側はキャッシュしている底面ポリゴンを用いて、新しいvisual_variantで
 * プレビューメッシュを再生成する（バックエンド往復不要）。
 */
export async function sendEditBuildingUpdateVisualVariant(
  visualVariant: number,
  visualVariant2: number,
  visualVariant3: number,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_visual_variant",
    params: {
      visual_variant: visualVariant,
      visual_variant2: visualVariant2,
      visual_variant3: visualVariant3,
    },
  });
}

/**
 * インフラ供給施設の効果範囲(領域)の形。
 * - 円: {type:"circle", center:[x,z], radius}
 * - 不定形: {type:"polygon", polygons:[{exterior:[[x,z],...], holes:[[[x,z],...],...]}]}
 * バックエンド app/services/service_coverage.py の領域 dict と同一構造。
 */
export type EffectRangeRegion =
  | { type: "circle"; center: number[]; radius: number }
  | {
      type: "polygon";
      polygons: { exterior: number[][]; holes: number[][][] }[];
    };

/**
 * `edit_building.update_effect_range`
 * 建物建設モードにおいて、選択中インフラ施設の効果範囲を Unity の 3D 上に可視化する。
 * region が null の場合は効果範囲表示をクリアする（効果範囲を持たない variant を選んだとき、
 * バリアント選択を解除したとき、パネルを閉じたとき）。
 */
export async function sendEditBuildingUpdateEffectRange(
  region: EffectRangeRegion | null,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_effect_range",
    params: {
      region,
    },
  });
}

/**
 * `edit_parcel:update_preview`
 * 土地区画作成モードにおいて、プレビュー形状をUnity側の2Dオーバーレイに描画する。
 * UI側でバックエンドからプレビューを取得した後、ポリゴンデータをUnityに送信する。
 *
 * @param mode 作成モード（empty_area / along_path / split_rectangle 等）
 * @param previewPolygons プレビュー区画のポリゴン配列 [ [ [x,y,z], ... ], ... ]
 * @param originalPolygon 元区画のポリゴン（分割モード時）。省略可。
 */
export async function sendEditParcelUpdatePreview(
  mode: string,
  previewPolygons: number[][][],
  originalPolygon?: number[][] | null,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_creation",
    entity: null,
    action: "edit_parcel:update_preview",
    params: {
      mode,
      preview_polygons: previewPolygons,
      original_polygon: originalPolygon ?? null,
    },
  });
}

/**
 * `edit_parcel:start_shape_edit`
 * 土地区画形状編集モードを開始する。
 * Unity側で指定された土地区画の頂点を編集できる状態に遷移する。
 */
export async function sendEditParcelStartShapeEdit(
  parcelId: number,
  polygon: number[][],
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_detail",
    entity: null,
    action: "edit_parcel:start_shape_edit",
    params: { parcel_id: parcelId, polygon },
  });
}

/**
 * `edit_parcel:cancel_shape_edit`
 * 土地区画形状編集モードをキャンセルする。
 */
export async function sendEditParcelCancelShapeEdit(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_detail",
    entity: null,
    action: "edit_parcel:cancel_shape_edit",
    params: {},
  });
}

/**
 * `edit_parcel:finish_shape_edit`
 * 土地区画形状編集モードを終了する。
 * UI側が PATCH /api/v1/parcels/{id} でポリゴンの保存に成功した後に送信され、
 * Unity側の編集状態をリセットしてデータをリロードする(`edit_path:finish` と同じ役割)。
 */
export async function sendEditParcelFinishShapeEdit(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_detail",
    entity: null,
    action: "edit_parcel:finish_shape_edit",
    params: {},
  });
}

/**
 * `parcel:show_front_path`
 * 土地区画詳細パネルの表示中、選択中区画の接道(front_path)を Unity の3Dビュー上に
 * 青い矢印で可視化するよう指示する。矢印は接道辺の境界をまたぐように描画される。
 * Unity 側が parcel_id から詳細(front_path_id / facade_edge_index / polygon)を取得して描画する。
 */
export async function sendShowParcelFrontPath(parcelId: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_detail",
    entity: null,
    action: "parcel:show_front_path",
    params: { parcel_id: parcelId },
  });
}

/**
 * `parcel:hide_front_path`
 * 接道の矢印表示をクリアする。土地区画詳細パネルを閉じたときに送信する。
 */
export async function sendHideParcelFrontPath(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_detail",
    entity: null,
    action: "parcel:hide_front_path",
    params: {},
  });
}

/**
 * `edit_connector:show`
 * 経済主体詳細パネル(AgentDetailPanel)の表示中、施設の接続端子ノード
 * (インフラ接続点 infra_node_id / 走行網接続点 depot_node_id) を Unity の3Dビュー上に
 * 色付きマーカーで読み取り専用ハイライト表示するよう指示する。
 * 共用接続ノード(infra_node_id == depot_node_id)の場合はマーカーを1つだけ表示する。
 */
export async function sendEditConnectorShow(
  agentId: number,
  infraNodeId: number,
  depotNodeId: number,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "agent_detail",
    entity: null,
    action: "edit_connector:show",
    params: { agent_id: agentId, infra_node_id: infraNodeId, depot_node_id: depotNodeId },
  });
}

/**
 * `edit_connector:hide`
 * 接続端子ノードのハイライト表示をクリアする。パネルを閉じた／別エージェントに
 * 切り替わったときに送信する。
 */
export async function sendEditConnectorHide(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "agent_detail",
    entity: null,
    action: "edit_connector:hide",
    params: {},
  });
}

/**
 * `edit_resource:finish`
 * 資源編集モードを終了し、Unity側で編集状態をリセットしてデータをリロードする。
 * バックエンドへの POST /api/v1/resources 送信は Tauri(UI)側が直接行うため、
 * このイベントは送信成功後の後始末(編集状態のクリア・セッションリロード)のみを担う。
 * 道路建設の `edit_path:finish` と同じ役割。
 */
export async function sendEditResourceFinish(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "resource_edit",
    entity: null,
    action: "edit_resource:finish",
    params: {},
  });
}

/**
 * `edit_transit:finish`
 * 公共交通の路線設置モードを終了し、Unity側で編集状態(停留所ギズモ)をリセットして
 * データをリロードする。バックエンドへの POST /api/v1/transit/lines 送信は Tauri(UI)側が
 * 直接行うため、このイベントは送信成功後の後始末(編集状態のクリア・セッションリロード)のみを担う。
 * 道路建設の `edit_path:finish` ・資源編集の `edit_resource:finish` と同じ役割。
 */
export async function sendEditTransitFinish(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "transit_creation",
    entity: null,
    action: "edit_transit:finish",
    params: {},
  });
}

/**
 * `edit_prop:change_settings`
 * プロップ配置モードにおいて、プルダウン以外の数値設定(回転角・スケール・密度・seed)を変更する。
 * Unity 側は配置中のゴースト/散布プレビューにこの設定を即反映する
 * (道路建設の `edit_path:change_curved` と同じく、プレビューへの逐次反映のためのイベント)。
 * 個別モードでは rotation / scale、範囲モードでは density / seed を使う。
 */
export async function sendEditPropChangeSettings(params: {
  rotation?: number;
  scale?: number;
  density?: number;
  seed?: number;
}): Promise<void> {
  await invoke("send_window_action", {
    windowType: "prop_placement",
    entity: null,
    action: "edit_prop:change_settings",
    params,
  });
}

/**
 * `edit_prop:finish`
 * プロップ配置モードを終了し、Unity 側で配置状態(ゴースト/散布プレビュー)をリセットして
 * データをリロードする。バックエンドへの POST /api/v1/props/... 送信は Tauri(UI)側が直接行うため、
 * このイベントは送信成功後の後始末のみを担う。道路建設の `edit_path:finish`・
 * 公共交通の `edit_transit:finish` と同じ役割。
 */
export async function sendEditPropFinish(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "prop_placement",
    entity: null,
    action: "edit_prop:finish",
    params: {},
  });
}

/**
 * `edit_prop:reset`
 * プロップ配置モードにおいて、配置済みのインスタンスや頂点（および仮確定状態）をすべてクリアする。
 */
export async function sendEditPropReset(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "prop_placement",
    entity: null,
    action: "edit_prop:reset",
    params: {},
  });
}

/**
 * `edit_station:change_variant`
 * 駅舎設置モードにおいて、選択中の駅舎モデル(house_variant)が変わったことを Unity へ通知し、
 * カーソル追従のゴーストを新しいモデルに作り直させる。
 */
export async function sendEditStationChangeVariant(houseVariant: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "track_build",
    entity: null,
    action: "edit_station:change_variant",
    params: { house_variant: houseVariant },
  });
}

/**
 * `edit_station:finish`
 * 駅舎設置モードを終了し、Unity 側でゴーストを片付けてデータをリロードする。駅舎の設置は
 * PATCH /api/v1/stations/{id} を Tauri(UI)側が直接行うため、このイベントは送信成功後の
 * 後始末のみを担う(プロップ配置の `edit_prop:finish` と同じ役割)。
 */
export async function sendEditStationFinish(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "track_build",
    entity: null,
    action: "edit_station:finish",
    params: {},
  });
}

/**
 * `session.reset`
 * 現在の3Dオブジェクトや地形のデータを全てクリアする。
 */
export async function sendSessionReset(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "session:reset",
    params: {},
  });
}

/**
 * `session.reload`
 * バックエンドからデータを取得して、3Dを再描画する。
 */
export async function sendSessionReload(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "session:reload",
    params: {},
  });
}

/**
 * `session.force_reload`
 * すべての3Dオブジェクトを消去してからバックエンドからデータを取得し、再描画する。
 * タスクバーのリロードボタンなど、明示的に全オブジェクトを作り直したい場合に使用する。
 */
export async function sendSessionForceReload(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "session:force_reload",
    params: {},
  });
}

/**
 * `zone.updated`
 * 地区情報（名前や建築規制等）がUI側で更新されたことをグラフィックプログラムに通知する。
 * グラフィック側は該当地区のデータをバックエンドから再取得してキャッシュを更新する。
 */
export async function sendZoneUpdated(zoneId: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "zone:updated",
    params: { zone_id: zoneId },
  });
}

/**
 * `edit_path.finish`
 * パス敷設モードを終了し、Unity側で編集状態をリセットしてデータをリロードする。
 * Tauri側が直接バックエンドに送信した後に呼び出す。
 */
export async function sendEditPathFinish(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "edit_path:finish",
    params: {},
  });
}

/**
 * `edit_path.change_platform_side`
 * 駅パス敷設モードにおいて、ホーム設置側(PLATFORM_SIDE_OUTSIDE/INSIDE/BOTH)を変更する。
 * Unity側は敷設中プレビューメッシュのプラットフォーム張り出し側を即座に切り替える
 * (曲線モード等と同じく、プレビューへの逐次反映のためのイベント)。
 * 駅バリアント(platform_width>0)以外では無視される。
 */
export async function sendSetPlatformSide(platformSide: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "edit_path:change_platform_side",
    params: { platform_side: platformSide },
  });
}

/**
 * `edit_path.change_lateral_offsets`
 * パス敷設モードにおいて、複合パス(軌道複線)の各線の横オフセット(m。敷設方向右が正)と
 * 進行方向(DIR_*)を変更する。両配列は index 対応で同じ長さ。
 * Unity側は敷設中プレビューメッシュを各オフセット位置の平行パス群として即座に描き直す
 * (敷設セッション中に線路数を変えてもノード列を維持したまま本数だけ更新される)。
 * 進行方向は駅バリアントのホーム設置側の解決(TrackPath.ResolvePlatformExtents)と
 * 進行方向矢印の表示に使われる。空配列は単一パス(中心線1本のプレビュー)に戻すことを意味する。
 * 初期値は STATE_EDIT_PATH の lateral_offsets / travel_directions params で渡す。
 */
export async function sendEditPathChangeLateralOffsets(
  lateralOffsets: number[],
  travelDirections: number[],
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "edit_path:change_lateral_offsets",
    params: { lateral_offsets: lateralOffsets, travel_directions: travelDirections },
  });
}

/**
 * `edit_path.set_follow_terrain`
 * パス敷設モードにおいて、地形追従の個別設定(-1=未設定[バリアント既定]/0=非追従/1=追従)を変更する。
 * Unity側は敷設中プレビューの地形追従・トンネル可視化を即座に切り替える(真実源はUnity側で、
 * 変更後の値は edit_path:update_shape の follow_terrain としてUIへ逆流し確定送信に積まれる)。
 * height_editable のバリアント以外では無視される。
 */
export async function sendEditPathSetFollowTerrain(value: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "edit_path:set_follow_terrain",
    params: { value },
  });
}

/**
 * `view.set_grid`
 * グリッドの表示有無や設定をグラフィックプログラムに通知する。
 * ドット表示モードは廃止済み。
 */
export async function sendSetGrid(
  mode: "off" | "line",
  size?: number,
  angle?: number,
  originX?: number,
  originZ?: number,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "view:set_grid",
    params: {
      mode,
      ...(size !== undefined ? { size } : {}),
      ...(angle !== undefined ? { angle } : {}),
      ...(originX !== undefined ? { origin_x: originX } : {}),
      ...(originZ !== undefined ? { origin_z: originZ } : {}),
    },
  });
}

/**
 * `game.start`
 * グラフィックプログラムにゲーム開始を通知する。
 * バックエンドのセッションが作成され current_save_id が非 null になった際に送信する。
 */
export async function sendGameStart(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "game:start",
    params: {},
  });
}

/**
 * `game.end`
 * グラフィックプログラムにゲーム終了を通知する。
 * セッションが破棄され current_save_id が null に戻った際に送信する。
 */
export async function sendGameEnd(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "game:end",
    params: {},
  });
}

/**
 * `system.focus`
 * グラフィックプログラム(Unity)のウィンドウをOSのフォアグラウンドに持ってくる。
 * UIパネル上のボタン操作後にユーザーがすぐに3Dビューを操作できるようにするために使用する。
 */
export async function sendFocusGraphic(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "system:focus",
    params: {},
  });
}

/**
 * `system.shutdown`
 * グラフィックプログラムを終了する。
 */
export async function sendSystemShutdown(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "system:shutdown",
    params: {},
  });
}

/**
 * `view.query_state`
 * グラフィックプログラムに現在の表示状態を問い合わせる。
 */
export async function sendViewQueryState(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "display_options",
    entity: null,
    action: "view:query_state",
    params: {},
  });
}

/* ---- graphic -> ui 方向の受信イベント名（定数） ---- */
/* これらのイベント名は、Rust層から Tauri の emit 経由でフロントエンドに配信される際に使われる。 */

/**
 * パス敷設モードにおいて、敷設済みノードを選択した(Unity → UI)。
 * ペイロードに選択ノードの `height`(相対標高)・`embankment_height` を含み、
 * パネルの標高・盛土ステッパーの表示と編集対象ノード(node_id)を同期する。node_id=-1 は次回配置値。
 */
export const IPC_EVENT_EDIT_PATH_SELECT_NODE = "edit_path:select_node";

/** パス敷設モードにおいて、敷設中パスの形状が変更された */
export const IPC_EVENT_EDIT_PATH_UPDATE_SHAPE = "edit_path:update_shape";

/** 建物建設モードにおいて、土地区画を選択した */
export const IPC_EVENT_EDIT_BUILDING_SELECT_PARCEL = "edit_building:select_parcel";

/**
 * 建物建設モード(直接配置)において、土地区画外の地点がクリックされた。
 * ペイロードは { position: [x, y, z] }。UIはこの地点を中心とする矩形底面を
 * バックエンドにプレビュー問い合わせして直接配置モードに入る。
 */
export const IPC_EVENT_EDIT_BUILDING_SELECT_POINT = "edit_building:select_point";

/** 建物建設モードにおいて、選択中の建物種類と互換性のない土地区画がクリックされた */
export const IPC_EVENT_EDIT_BUILDING_INVALID_PARCEL = "edit_building:invalid_parcel";

/**
 * 直接配置モードにおいて、中ボタンドラッグによる回転が終了(リリース)し、
 * Unity側が確定した回転角(度)を通知する。UIは回転ステッパー(rectAngle)を更新する。
 */
export const IPC_EVENT_EDIT_BUILDING_UPDATE_DIRECT_ANGLE = "edit_building:update_direct_angle";

/** 土地区画作成モードにおいて、3Dビュー上の地点がクリックされた (empty_area 用) */
export const IPC_EVENT_EDIT_PARCEL_SELECT_POINT = "edit_parcel:select_point";

/** 土地区画作成モードにおいて、パスが選択された (along_path 用) */
export const IPC_EVENT_EDIT_PARCEL_SELECT_PATH = "edit_parcel:select_path";

/** 土地区画作成モードにおいて、既存土地区画が選択された (split 系用) */
export const IPC_EVENT_EDIT_PARCEL_SELECT_PARCEL = "edit_parcel:select_parcel";

/** 土地区画作成モード（empty_area）において、3Dビュー上で矩形の角度が変更された */
export const IPC_EVENT_EDIT_PARCEL_UPDATE_ROTATION = "edit_parcel:update_rotation";

/**
 * 土地区画作成モード（manual = 任意ポリゴン）において、編集中のポリゴン頂点列が変更された (Unity → UI)。
 * ペイロードは { polygon: number[][]; finalized: boolean }（各頂点は [x, z]）。
 * finalized は右クリックで形状を仮確定済み（頂点ドラッグ調整フェーズ）かどうか。
 * 作成パネルがこれを受けて頂点数の表示と POST 用のポリゴンを更新する。
 *
 * 既存区画の形状編集で使う `edit_parcel:update_shape`（IPC_EVENT_EDIT_PARCEL_UPDATE_SHAPE、
 * ParcelDetailPanel が使用）とは用途もペイロードも別物のため、イベント名を分けている。
 */
export const IPC_EVENT_EDIT_PARCEL_UPDATE_CREATION_SHAPE = "edit_parcel:update_creation_shape";

/** 表示オプションパネルの状態問い合わせに対するレスポンス */
export const IPC_EVENT_VIEW_CURRENT_STATE = "view:current_state";

/** 地形セル開放モードでセルが選択された */
export const IPC_EVENT_UNLOCK_CELL_SELECT = "unlock_cell:select_cell";

/** 土地区画形状編集モードにおいて、編集中のポリゴンが変更された */
export const IPC_EVENT_EDIT_PARCEL_UPDATE_SHAPE = "edit_parcel:update_shape";

/** 地区編集モードで選択中地区が変化した通知(Unity → UI)。zone_id=0 は選択なし。 */
export const IPC_EVENT_EDIT_ZONE_SELECTED = "edit_zone:selected";

/** 3Dビュー上でオブジェクトが選択された */
export const IPC_EVENT_WORLD_SELECT_OBJECT = "world:select_object";

/** グリッド角度が2点選択により変更された */
export const IPC_EVENT_VIEW_GRID_ANGLE_CHANGED = "view:grid_angle_changed";

/**
 * 3Dビューのラジアルメニューでグリッド表示がトグルされた(Unity → UI)。
 * ペイロードは { visible: boolean }。グリッド表示⇔グリッド吸着の連動はUnity側が行うため、
 * UI側(道路建設パネル)はグリッド設定UIの表示状態を追従させるだけでよい。
 */
export const IPC_EVENT_VIEW_GRID_TOGGLED = "view:grid_toggled";

/** 資源編集モードにおいて、編集中のポリゴンが変更された */
export const IPC_EVENT_EDIT_RESOURCE_UPDATE_SHAPE = "edit_resource:update_shape";

/**
 * 公共交通の路線設置モードにおいて、配置中の停留所の座標列が変更された(Unity → UI)。
 * ペイロードは { stops: number[][] }（各停留所は [x, y, z]。公共交通パスは相対Y=0）。
 * 新設パネル(TransitCreationPanel)がこれを受けて停留所数の表示と POST 用の停留所列を更新する。
 */
export const IPC_EVENT_EDIT_TRANSIT_UPDATE_STOPS = "edit_transit:update_stops";

/**
 * プロップ配置(個別モード)において、配置済みインスタンス列が変更された(Unity → UI)。
 * ペイロードは { instances: [{x, z, rotation, scale}] }。
 * PropPlacementPanel がこれを受けて配置数の表示と POST 用のインスタンス列を更新する。
 */
export const IPC_EVENT_EDIT_PROP_UPDATE_INSTANCES = "edit_prop:update_instances";

/**
 * プロップ配置(範囲モード)において、描画中の散布範囲ポリゴンが変更された(Unity → UI)。
 * ペイロードは { polygon: number[][] }（[x, z] の頂点列。資源編集 edit_resource:update_shape と同形）。
 * PropPlacementPanel がこれを受けて頂点数の表示と POST 用のポリゴンを更新する。
 */
export const IPC_EVENT_EDIT_PROP_UPDATE_SHAPE = "edit_prop:update_shape";

/**
 * 駅舎設置モードにおいて、左クリックで候補位置が確定した(Unity → UI)。
 * ペイロードは { path_id, position: [x, z], rotation, house_variant }。
 * TrackConstructionPanel(駅舎タブ)がこれを受けて、path_id から所属駅を解決し、
 * 確定ボタンで PATCH /api/v1/stations/{id} に駅舎の設置情報を送る。
 */
export const IPC_EVENT_EDIT_STATION_UPDATE_CANDIDATE = "edit_station:update_candidate";
