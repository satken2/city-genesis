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

/** チュートリアル案内は変更時とUnity再接続時に全置換する。周期送信しない。 */
export async function sendTutorialGuide(state: import("../tutorial/model").Snapshot): Promise<void> {
  await invoke("send_window_action", { windowType: "tutorial", entity: null, action: "tutorial:apply", params: state });
}

export const IPC_EVENT_WEATHER_STATE = "weather:state";
/** 乗車選択と走行HUDはUnityが表示する。文言はUIの現在言語で一括して渡す。 */
export async function openDriveMode(labels: Record<string, string>): Promise<void> {
  await invoke("send_window_action", { windowType: "cockpit", entity: null, action: "drive:open", params: labels });
}
export async function sendAmbienceVolume(volume: number): Promise<void> {
  await invoke("send_window_action", { windowType: "cockpit", entity: null, action: "ambience:volume", params: { volume } });
}

/** rain/snow は0=なし〜3=強い。weight は今の気候・季節での出現割合(%)で、0の天気も手動では選べる。 */
export type WeatherState = {
  id: string;
  locked: boolean;
  rain: number;
  snow: number;
  presets: { id: string; weight: number; rain: number; snow: number }[];
};

export async function queryWeather(): Promise<void> {
  await invoke("send_window_action", { windowType: "cockpit", entity: null, action: "weather:query", params: {} });
}

/** id=nullは固定を解除する。天気の正はUnityの応答とし、UIでは抽選しない。 */
export async function setWeather(id: string | null): Promise<void> {
  await invoke("send_window_action", { windowType: "cockpit", entity: null, action: "weather:set", params: { id } });
}

/**
 * 3D画面(Unityのウィンドウ)を隣のディスプレイへ移す。direction=1で次、-1で前。
 * UIとUnityではディスプレイの列挙順が一致しないので番号は送らず、隣の決定と表示先の記録はUnityに任せる。
 */
export async function sendMoveGraphicDisplay(direction: 1 | -1): Promise<void> {
  await invoke("send_window_action", { windowType: "settings", entity: null, action: "system:move_display", params: { direction } });
}

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
  | "STATE_SET_GRID"
  | "STATE_EDIT_RESOURCE"
  | "STATE_EDIT_TRANSIT"
  | "STATE_EDIT_PROP"
  | "STATE_EDIT_CONNECTOR"
  | "STATE_REMOVE_OBJECTS"
  | "STATE_MERGE_PATH"
  | "STATE_SPLIT_PATH"
  | "STATE_EDIT_ROAD_MEMBERS"
  | "STATE_EDIT_GRADING";

/* ---- ui -> graphic 方向の送信 ---- */

/**
 * `view.change_view`
 * 表示オプションで選択した表示モードをグラフィックプログラムに通知する。
 * flags は色モードと独立した表示トグル("plan"=計画中表示, "public"=公共施設ハイライト)の
 * ID配列で、CSVに結合して送信する。省略(未指定)時は null を送り、
 * Unity側は「現在のフラグを維持」と解釈する(空配列は「全トグルOFF」の明示指定)。
 */
export async function sendChangeDisplay(
  layer: string,
  mode: string,
  subMode?: string | null,
  flags?: string[] | null,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "display_options",
    entity: null,
    action: "view:change_view",
    params: {
      layer,
      mode,
      sub_mode: subMode ?? null,
      flags: flags != null ? flags.join(",") : null,
    },
  });
}

/**
 * `world.change_state`
 * グラフィックプログラムのステート（通常モード、建物建設モード等）を切り替える。
 * extraに追加パラメータを渡すと、paramsにマージされて送信される。
 * 例: 道路敷設モード時に { path_type: 1, path_variant: 212 } を渡すと
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
  placementRevision?: number,
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
      placement_revision: placementRevision ?? null,
    },
  });
}

export type BuildingPlacementState = {
  variant: number;
  revision: number;
  mode: "parcel" | "free" | "roadside";
  position: number[] | null;
  width: number;
  depth: number;
  angle: number;
  parcel_id: number;
  request_id?: string | null;
};

/** 配置設定は差分だけを送り、補正後の位置・寸法はplacement_stateで受け取る。 */
export async function sendBuildingPlacementCommand(variant: number, command: {
  width?: number; depth?: number; angle?: number; reset?: boolean; labels?: Record<string, string>; request_id?: string;
} = {}): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:placement_command",
    params: { command: { variant, ...command } },
  });
}

export const IPC_EVENT_BUILDING_PLACEMENT_STATE = "edit_building:placement_state";

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
 * `edit_building.update_structure`
 * 建物建設モードにおいて、構造種別(structures マスターのID。0=構造の区別なし)を変更する。
 * Unity側はキャッシュしている底面ポリゴンを用いて、構造に応じたパーツプレフィックス
 * (a{variant}s{structure} → a{variant} などのフォールバック)でプレビューメッシュを再生成する。
 */
export async function sendEditBuildingUpdateStructure(structure: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_structure",
    params: { structure },
  });
}

/**
 * `edit_building.update_landmark`
 * 建物建設モードにおいて、観光名所の種別(landmarks マスターのID。0=観光名所ではない)を変更する。
 * Unity側は名所ごとの専用モデル(Resources/Models/Buildings/landmark-{model_prefix})に
 * 差し替えてプレビューメッシュを再生成する。
 */
export async function sendEditBuildingUpdateLandmark(landmark: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_landmark",
    params: { landmark },
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
 * `building_construction:pick_source`
 * 既存建物コピー建設で、コピー元建物の選択モードを Unity 側で開始させる。
 * Unity は建物だけをクリック可能(MODE_PICK_BUILDING)にし、建物が選ばれると
 * `building_construction:copy_source` を UI へ返す。
 *
 * Unity側のこのモードは「既存の建物を1つ選ぶ」だけの汎用動作なので、テンプレートモードの
 * 経済主体作成パネル(入居先建物の選択)も同じ経路を使う。`windowType` は送信元の識別用で、
 * Unityの動作は変わらない。同じ通知を複数のパネルが受けないよう、この経路を使うウィンドウは
 * `window-mutex-groups.json` で同時に開けないようにしてある。
 */
export async function sendPickCopySource(windowType: string = "building_construction"): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "building_construction:pick_source",
    params: {},
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
 * 建物建設モードにおいて、効果範囲を Unity の 3D 上に可視化する。2種類の領域を運ぶ。
 * - region: 選択中インフラ施設を建てた場合の効果範囲（プレビュー。敷地選択後に決まる）
 * - existing: 同じサービス財を供給する既存施設の効果範囲一覧（バリアント選択中は常時表示）
 * region と existing がともに null/空 の場合は効果範囲表示をクリアする
 * （効果範囲を持たない variant を選んだとき、バリアント選択を解除したとき、パネルを閉じたとき）。
 */
export async function sendEditBuildingUpdateEffectRange(
  region: EffectRangeRegion | null,
  existing: EffectRangeRegion[] | null = null,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_effect_range",
    params: {
      region,
      existing,
    },
  });
}

/**
 * `edit_parcel:update_preview`
 * 土地区画作成モードにおいて、プレビュー形状をUnity側の2Dオーバーレイに描画する。
 * UI側でバックエンドからプレビューを取得した後、ポリゴンデータをUnityに送信する。
 *
 * 一括作成モードでは数百区画ぶんのポリゴンを載せるため、MMFの容量を圧迫しないよう
 * 座標を1cm精度に丸めて送る（オーバーレイ描画専用の値であり、確定はバックエンドが行う）。
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
  const roundPoint = (point: number[]) => point.map((v) => Math.round(v * 100) / 100);
  await invoke("send_window_action", {
    windowType: "parcel_creation",
    entity: null,
    action: "edit_parcel:update_preview",
    params: {
      mode,
      preview_polygons: previewPolygons.map((polygon) => polygon.map(roundPoint)),
      original_polygon: originalPolygon ? originalPolygon.map(roundPoint) : null,
    },
  });
}

/**
 * `edit_parcel:reset_selection`
 * 土地区画作成モードにおいて、作成モードは維持したまま3Dビュー側の選択とプレビューだけを捨てる。
 *
 * 作成を確定した直後・「クリア」ボタン押下時に送る。**確定してもモードから抜けない**ため、
 * ユーザーはパネルに戻らずそのまま3Dビューをクリックし続けて次の区画を作れる
 * （作成モードの切り替え自体はUnity側のCtrlラジアルメニューが行う）。
 */
export async function sendEditParcelResetSelection(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_creation",
    entity: null,
    action: "edit_parcel:reset_selection",
    params: {},
  });
}

/**
 * `edit_parcel:set_rect_params`
 * 土地区画作成モードの矩形パラメータ（幅・奥行き）をUnityへ送る。
 *
 * 用途は「地点を選んだあとの角度ドラッグ中に3Dビューが描くローカル矩形プレビュー」を
 * パネルの入力値に合わせることだけで、確定形状はバックエンドのプレビュー（`edit_parcel:update_preview`）が正。
 */
export async function sendEditParcelSetRectParams(width: number, height: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_creation",
    entity: null,
    action: "edit_parcel:set_rect_params",
    params: { width, height },
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
 * `edit_zone:finish_merge`
 * 地区編集モードの地区統合について、UI側での確定/取消の結果を Unity へ返す。
 * merged=true は「UI側が POST /api/v1/zones/{id}/merge の送信に成功した」の意味で、
 * Unity は地区・道路を再取得して統合結果を描画へ反映する。
 * merged=false は取消で、Unity は確定待ち状態を解除するだけ(地区編集モードは継続)。
 */
export async function sendEditZoneFinishMerge(merged: boolean): Promise<void> {
  await invoke("send_window_action", {
    windowType: "zone_merge",
    entity: null,
    action: "edit_zone:finish_merge",
    params: { merged: merged ? 1 : 0 },
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
 * パス干渉ハイライトのペイロード。バックエンドの
 * `compute_path_interference` / `GET|POST /api/v1/paths/interference` と同形。
 * 区画は着工可否の4分類、建物は当該区画に立地する agent_id、
 * パスは立体交差する既存 path_id、川は地表レベルで横切っている川の id(文字列)。
 */
export type PathInterferenceHighlight = {
  parcels: {
    blocking: number[];
    removable: number[];
    shavable: number[];
    exempt: number[];
  };
  building_ids: number[];
  colliding_path_ids: number[];
  colliding_river_ids?: string[];
};

/**
 * `world:highlight_objects`
 * 計画中パスが干渉する区画・建物・既存パスを Unity の3Dビュー上でハイライト表示する。
 * 区画は分類ごとに色分け(blocking=赤/removable=橙/shavable=黄/exempt=なし)、
 * 建物とパスは警告色で強調する。敷設中の形状確定時・確定ボタン押下時
 * (windowType="path_build")と、着工前パスの詳細パネル表示時
 * (windowType="path_detail")の双方から送る。
 */
export async function sendHighlightObjects(
  interference: PathInterferenceHighlight,
  windowType: string = "path_build",
): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "world:highlight_objects",
    params: {
      // ハイライトの発信元。Unity側はソースごとに独立レイヤーで保持し、
      // あるウィンドウのクリアが別ウィンドウのハイライトを消さないようにする。
      source: windowType,
      parcels: interference.parcels,
      building_ids: interference.building_ids,
      colliding_path_ids: interference.colliding_path_ids,
      colliding_river_ids: interference.colliding_river_ids ?? [],
    },
  });
}

/**
 * `world:clear_highlights`
 * `world:highlight_objects` で表示した干渉ハイライトのうち、この発信元(windowType)の
 * ぶんだけを消す。敷設終了・詳細パネルを閉じたときに送る。
 */
export async function sendClearHighlights(
  windowType: string = "path_build",
): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "world:clear_highlights",
    params: { source: windowType },
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
 * 切り替わったときに送信する。Unity は接続点編集モード(`STATE_EDIT_CONNECTOR`)中の
 * ときだけ通常モードへ戻し、他のステート中はハイライトを消すだけにする。
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
 * `edit_grading:finish`
 * 造成範囲の作図モードを終了する。POST /api/v1/grading は UI が直接送るため、これは発注成功後の後始末だけを担う。
 * Unity はこの1イベントで作図の片付け→通常ステートへの復帰→データの再取得までを行うので、
 * パネルは `STATE_NORMAL` を別送しない。確定後にパネルを閉じると Rust の close_window_by_label が
 * `STATE_NORMAL` を送るが、Unity は既に通常ステートなので表示・入力を再適用するだけで、再取得は取り消されない(冗長だが無害)。
 */
export async function sendEditGradingFinish(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "grading",
    entity: null,
    action: "edit_grading:finish",
    params: {},
  });
}

/**
 * `edit_prop:change_settings`
 * プロップ配置モードにおいて、プルダウン以外の数値設定(回転角・スケール・密度・seed)を変更する。
 * Unity 側は配置中のゴースト/散布プレビューにこの設定を即反映する
 * (道路建設の `edit_path:change_curved` と同じく、プレビューへの逐次反映のためのイベント)。
 * 個別モードでは rotation / scale / height（地面相対高さm）、範囲モードでは density / seed を使う。
 */
export async function sendEditPropChangeSettings(params: {
  height?: number;
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
 * `remove_object:confirm_result`
 * ブルドーザモードで Unity が確認を求めた対象(`remove_object:request_confirm`。範囲配置プロップ)について、
 * 確認ダイアログの回答を返す。accepted=true で Unity が DELETE を発行して差分リロードする。
 * 回答が届かない限り Unity はその対象を削除しない(取消はダイアログを閉じるだけでよいが、
 * Unity 側の確認待ちを解くため false も必ず返す)。
 */
export async function sendRemoveObjectConfirmResult(
  objectType: string,
  id: number,
  accepted: boolean,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "bulldozer",
    entity: null,
    action: "remove_object:confirm_result",
    params: { object_type: objectType, id, accepted: accepted ? 1 : 0 },
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

/** 爆破APIの成功後、対象建物の再取得・瓦礫への切替・単発エフェクトをUnityへ依頼する。 */
export async function sendBuildingExploded(agentId: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "building:exploded",
    params: { agent_id: agentId },
  });
}

/**
 * `session.force_reload`
 * すべての3Dオブジェクトを消去してからバックエンドからデータを取得し、再描画する。
 * システムトレイのリロードボタンなど、明示的に全オブジェクトを作り直したい場合に使用する。
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
export async function sendSetPassageMode(passageMode: string): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build", entity: null,
    action: "edit_path:change_passage_mode", params: { passage_mode: passageMode },
  });
}

export async function sendSetHouseEnds(houseEnds: string): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build", entity: null,
    action: "edit_path:change_house_ends", params: { house_ends: houseEnds },
  });
}

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
 * 2Dパス形状編集パネル(path_shape_editor)からの形状操作。op ごとに使うフィールドが違う。
 * - select_node: index
 * - move_node: index, x, z (XZのみ。地面相対の高さはUnity側が維持する)
 * - move_control: index, which("in"|"out"), x, z (制御点の絶対標高はUnity側が維持する)
 * - insert_node: edge_index(points[i]〜points[i+1]の辺), x, z
 * - remove_node: index
 */
export type PathShapeCommand =
  | { op: "apply_alignment"; revision: string; alignment_json: string }
  | { op: "apply_route"; revision: string; route_json: string }
  | { op: "undo_route"; revision: string }
  | { op: "select_node"; index: number }
  | { op: "move_node"; index: number; x: number; z: number }
  | { op: "move_control"; index: number; which: "in" | "out"; x: number; z: number }
  | { op: "insert_node"; edge_index: number; x: number; z: number }
  | { op: "remove_node"; index: number };

/**
 * `edit_path.shape_command`
 * 2Dパス形状編集パネルのノード・制御点操作をUnityへ送る。形状の真実源はUnity側
 * (RoadPlanManager)のままで、Unityは吸着を適用せず既存の移動・挿入・削除ロジックで
 * 適用し、結果を `edit_path:update_shape`(全状態)で返す。ドラッグ中は送らず、
 * ドラッグ終了時に1回だけ送る。Editingフェーズ以外や3Dでドラッグ中はUnity側が無視する。
 */
export async function sendEditPathShapeCommand(command: PathShapeCommand): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_shape_editor",
    entity: null,
    action: "edit_path:shape_command",
    params: command,
  });
}

/** 建設パネルの全線検証結果を、検証時のUnity編集世代とともに共有する。 */
export async function sendEditPathAlignment(revision: string, alignments: unknown): Promise<void> {
  await sendEditPathShapeCommand({ op: "apply_alignment", revision, alignment_json: JSON.stringify(alignments ?? null) });
}

/**
 * `edit_path.query_shape`
 * 敷設中パスの現在形状を問い合わせる。Unityは `edit_path:update_shape` を1回返す。
 * `update_shape` は形状が変化したときにしか送られないため、開くたびに再マウントされる
 * 2Dパス形状編集パネルが初期状態を得るのに使う(`camera:query_state` と同じ問い合わせ応答型)。
 */
export async function sendEditPathQueryShape(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_shape_editor",
    entity: null,
    action: "edit_path:query_shape",
    params: {},
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
 * `world.change_state` (STATE_MERGE_PATH)
 * パス結合の相手選択モードを開始する。Unity は指定パスと共有ノードで繋がる候補パスを
 * 色分けし、クリックされた1本を `edit_path:select_merge_target` で返す。
 * pathId は結合後に残る側(survivor)＝パス詳細パネルで開いているパス。
 */
export async function sendStartPathMerge(pathId: number): Promise<void> {
  await sendChangeState("STATE_MERGE_PATH", { path_id: pathId }, "path_detail");
}

/**
 * `world.change_state` (STATE_SPLIT_PATH)
 * パス分割の分割点選択モードを開始する。Unity はカーソルを対象パスの中心線へ射影した点を
 * マーカー表示し、クリックされた点を `edit_path:select_split_point` で返す。
 */
export async function sendStartPathSplit(pathId: number): Promise<void> {
  await sendChangeState("STATE_SPLIT_PATH", { path_id: pathId }, "path_detail");
}

/**
 * `world.change_state` (STATE_NORMAL)
 * パス結合/分割の選択モードをキャンセルする(確定せずに閉じたときの後始末)。
 * Unity 側は候補パスの着色と分割点マーカーをクリアして通常モードへ戻る。
 * 確定時は再描画までまとめて行う `sendFinishPathTopologyEdit` を使うこと。
 */
export async function sendEndPathTopologyEdit(): Promise<void> {
  await sendChangeState("STATE_NORMAL", undefined, "path_detail");
}

/**
 * `edit_path.finish_topology_edit`
 * パスの結合/分割の確定送信に成功したことを通知する。Unity 側は選択モードの終了・
 * 消えたパスの破棄・通常モードへの復帰・データ再取得を1つのハンドラでまとめて行う。
 *
 * `session:reload` と `world:change_state` を別々に送ると、非同期の差分リロードと
 * 状態遷移が前後して表示が更新されないことがあるため、道路敷設の `edit_path:finish` と
 * 同じく単一イベントにしてある。
 *
 * removedPathId は結合で吸収されて消えたパスのID(分割時は0)。差分リロードの削除判定を
 * 待たずに確実に3Dから消すために渡す。
 */
export async function sendFinishPathTopologyEdit(removedPathId: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_detail",
    entity: null,
    action: "edit_path:finish_topology_edit",
    params: { removed_path_id: removedPathId },
  });
}

/* ---- 道路(名前付き道路) ---- */

/**
 * `world.set_road_focus`
 * 道路詳細パネルで開いている道路の区間を、3Dビュー上に道路色の帯で強調する。
 * 帯は区間の中心線に沿って最前面に描く(公共交通ハイライトと同じ描き方)。送るたびに置き換わる。
 */
export async function sendRoadFocus(roadId: number, color: string, pathIds: number[]): Promise<void> {
  await invoke("send_window_action", {
    windowType: "road_detail",
    entity: null,
    action: "world:set_road_focus",
    params: { road_id: roadId, color, path_ids: pathIds },
  });
}

/**
 * `world.clear_road_focus` 道路の強調表示を消す(パネルを閉じたときは Rust 側も送る)。
 * roadId を渡すと、Unity は強調中の道路がそれと同じときだけ消す(別の道路詳細パネルの強調を消さないため)。
 */
export async function sendClearRoadFocus(roadId?: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "road_detail",
    entity: null,
    action: "world:clear_road_focus",
    params: roadId != null ? { road_id: roadId } : {},
  });
}

/**
 * `world.refresh_roads`
 * 道路の作成・改名・所属変更・削除のあとに送り、Unity に道路一覧(GET /api/v1/roads/)を取り直させる。
 * 道路名の3Dラベルは一覧の version が変わったときだけ作り直される。
 */
export async function sendRefreshRoads(windowType: string = "road_detail"): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "world:refresh_roads",
    params: {},
  });
}

/**
 * `world.change_state` (STATE_EDIT_ROAD_MEMBERS)
 * 道路の区間編集モードを開始する。Unity は現在の所属区間を道路色で示し、街路・高速道路の
 * 区間がクリックされるたびに `edit_road:toggle_path` を返す(所属の付け外しはUIがAPIで行う)。
 * sessionId はパネルが編集を始めるたびに採番する正の整数。Unity はモード終了時の `edit_road:ended` に
 * そのまま返し、パネルは遅れて届いた前回の終了通知で新しい編集を落とさないよう照合する。
 */
export async function sendStartRoadMemberEdit(
  roadId: number,
  color: string,
  pathIds: number[],
  sessionId: number,
): Promise<void> {
  await sendChangeState(
    "STATE_EDIT_ROAD_MEMBERS",
    { road_id: roadId, color, path_ids: pathIds, session_id: sessionId },
    "road_detail",
  );
}

/**
 * `edit_road.set_members`
 * 区間編集モード中に、所属区間の集合(全集合)と、直進方向の自動延長で追加候補になった区間を送る。
 * 差分ではなく毎回全集合を送る。previewPathIds は薄い色で示す候補(確定前)。
 */
export async function sendRoadEditSetMembers(
  roadId: number,
  pathIds: number[],
  previewPathIds: number[] = [],
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "road_detail",
    entity: null,
    action: "edit_road:set_members",
    params: { road_id: roadId, path_ids: pathIds, preview_path_ids: previewPathIds },
  });
}

/**
 * `edit_road.cancel`
 * 道路の区間編集モードを終える。Unity はこの道路の区間編集モード中のときだけ終了して通常モードへ戻し、
 * それ以外(別のモードへ移った後・別の道路の編集中)は何もしない。STATE_NORMAL を送らないのは、
 * UI の知らないうちに Unity が別のモード(道路の敷設など)へ移っていた場合にそれを打ち切らないため。
 * パネルを閉じたときは Rust 側が同じ `edit_road:cancel` を送る。
 */
export async function sendEndRoadMemberEdit(roadId: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "road_detail",
    entity: null,
    action: "edit_road:cancel",
    params: { road_id: roadId },
  });
}

/**
 * 区間編集モードで区間がクリックされた(Unity → UI)。
 * ペイロードは { path_id, path_name, path_type, shift }。shift=true は Shift+クリックで、
 * その区間から直進方向へたどった区間をまとめて追加する操作を表す。
 */
export const IPC_EVENT_ROAD_TOGGLE_PATH = "edit_road:toggle_path";

export type RoadTogglePathPayload = {
  path_id: number;
  path_name: string;
  path_type: number;
  shift: boolean;
};

/**
 * Unity の区間編集モードが終わった(Unity → UI)。理由(完了・Esc・別のモードへの切り替え・
 * 閉じたパネルの edit_road:cancel・セッションのリセットなど)は問わず、モードを抜けるたびに届く。
 * session_id は開始時の STATE_EDIT_ROAD_MEMBERS に載せた値(Unity が返さない場合は無い)。
 */
export const IPC_EVENT_ROAD_EDIT_ENDED = "edit_road:ended";

export type RoadEditEndedPayload = {
  road_id: number;
  session_id?: number;
};

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
 * `camera.query_state`
 * 3Dカメラの現在位置と視線方位を問い合わせる(応答は `camera:state`)。
 *
 * カメラの追従表示は行わない(常時配信はMMFを無駄に埋めるため)。2D都市マップが
 * 開かれたときと再読み込みのときにこれを送り、そのときのカメラ位置を1回だけ描く。
 */
export async function sendCameraQueryState(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "city_map",
    entity: null,
    action: "camera:query_state",
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

/**
 * 表示プリセットの名前。Unity側(`IpcViewCoordinator`の`PRESET_*`)と一致させること。
 * - 公共交通の一覧・新設パネルが開いている間の表示(全路線+計画中の道路・建物)。
 * - 土地区画詳細パネルが開いている間の表示(土地区画境界線の表示)。
 */
export const VIEW_PRESET_TRANSIT = "transit";
export const VIEW_PRESET_PARCEL_DETAIL = "parcel_detail";

/**
 * `view.apply_preset`
 * ウィンドウを開いている間だけ3Dビューの表示を差し替える。
 *
 * 道路敷設や地区編集のように専用の編集ステート(`STATE_*`)を持つ操作では、Unityが
 * ステートごとの表示プリセットを自動で適用するのでこれを呼ぶ必要はない。
 * **ステートを持たないウィンドウ専用の入口**で、元の表示への復帰は
 * `STATE_NORMAL`(ウィンドウを閉じたときにRust側が送る)が担う。
 * プリセットの中身はUnity側が持ち、UIは名前だけを指定する。
 *
 * ここで変えた表示はUnityの「通常モードの記憶」には載らないので、
 * `sendChangeDisplay`で同じことをしてはいけない(ユーザーが選んでいた表示を上書きしてしまう)。
 */
export async function sendViewApplyPreset(preset: string, windowType: string): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "view:apply_preset",
    params: { preset },
  });
}

/**
 * `view.restore_preset`
 * ウィンドウ単位の表示プリセットを解除し、元の表示状態へ復帰する。
 *
 * 詳細パネルのように編集ステートを持たず、かつ他の編集ステートと併存しうるウィンドウが、
 * `STATE_NORMAL` を送らずに自身の表示プリセットのみを安全に解除するために使う。
 */
export async function sendViewRestorePreset(preset: string, windowType: string): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "view:restore_preset",
    params: { preset },
  });
}

/**
 * `view.set_object_filter`
 * 建物・道路をオブジェクト単位で非表示にする集合をグラフィックプログラムへ送る。
 * レイヤー単位の表示制御(`view:change_view`)とは独立の別系統で、建設プロジェクトの
 * タグ単位の表示切り替えと、詳細パネルの個別の表示切り替えが共用する。
 *
 * **差分ではなく「今隠しているものの全集合」を毎回送る**(追加・削除の取りこぼしで
 * 表示が食い違う余地をなくすため)。状態の正はUnity側にあり、バックエンドには送らない。
 */
export async function sendSetObjectFilter(
  hiddenBuildingIds: number[],
  hiddenPathIds: number[],
  windowType: string = "construction_projects",
): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "view:set_object_filter",
    params: {
      hidden_building_ids: hiddenBuildingIds,
      hidden_path_ids: hiddenPathIds,
    },
  });
}

/**
 * `view.query_object_filter`
 * オブジェクト単位の表示フィルタの現在値をグラフィックプログラムに問い合わせる。
 * 応答は `view:object_filter_state`(`IPC_EVENT_VIEW_OBJECT_FILTER_STATE`)。
 */
export async function sendQueryObjectFilter(
  windowType: string = "construction_projects",
): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "view:query_object_filter",
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

/**
 * パス敷設モードにおいて、敷設中パスの形状が変更された(Unity → UI)。
 * 全WebViewへブロードキャストされるため、敷設パネルと2Dパス形状編集パネルの両方が同じイベントを受ける。
 * ペイロードの型は `components/path/pathBuildConstants.ts` の `PathShapeUpdatePayload`。
 */
export const IPC_EVENT_EDIT_PATH_UPDATE_SHAPE = "edit_path:update_shape";

/**
 * パス結合の相手選択モード(STATE_MERGE_PATH)で、結合相手のパスが選ばれた(Unity → UI)。
 * ペイロードは { path_id: number; path_name: string }。
 * パス詳細パネルがこれを受けて結合プレビュー(dry_run)を取得し、長さを表示して確定を待つ。
 */
export const IPC_EVENT_EDIT_PATH_SELECT_MERGE_TARGET = "edit_path:select_merge_target";

/**
 * パス分割の分割点選択モード(STATE_SPLIT_PATH)で、分割点が選ばれた(Unity → UI)。
 * ペイロードは { point: [x, y, z] }(対象パスの中心線へ射影済み)。
 * パス詳細パネルがこれを受けて分割プレビュー(dry_run)を取得し、前半・後半の長さを表示する。
 */
export const IPC_EVENT_EDIT_PATH_SELECT_SPLIT_POINT = "edit_path:select_split_point";

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
 * 既存建物コピー建設のコピー元選択モードで、コピー元の建物が選択された。
 * ペイロードは { source_agent_id, variant, floors, visual_variant, visual_variant2, visual_variant3 }。
 * パネルはこれをシードして STATE_EDIT_BUILDING の区画選択フローへ遷移する。
 * 経済主体作成パネル(`agent_creation`)も `sendPickCopySource` 経由でこの通知を受け、
 * source_agent_id を入居先建物として扱う(他のフィールドは使わない)。
 */
export const IPC_EVENT_BUILDING_CONSTRUCTION_COPY_SOURCE = "building_construction:copy_source";

/**
 * 直接配置モードにおいて、中ボタンドラッグによる回転が終了(リリース)し、
 * Unity側が確定した回転角(度)を通知する。UIは回転ステッパー(rectAngle)を更新する。
 */
export const IPC_EVENT_EDIT_BUILDING_UPDATE_DIRECT_ANGLE = "edit_building:update_direct_angle";

/** 土地区画作成モードにおいて、3Dビュー上の地点がクリックされた (empty_area 用) */
export const IPC_EVENT_EDIT_PARCEL_SELECT_POINT = "edit_parcel:select_point";

/**
 * 土地区画作成モードにおいて、パスが選択された (along_path 用)。
 * ペイロードは `{ path_id, position }` で、`position` はクリック地点をパス中心線へ
 * 射影した [x, y, z]。作成範囲が「クリック地点」のとき、そこだけ区画を作るアンカーになる。
 */
export const IPC_EVENT_EDIT_PARCEL_SELECT_PATH = "edit_parcel:select_path";

/** 土地区画作成モードにおいて、既存土地区画が選択された (split 系用) */
export const IPC_EVENT_EDIT_PARCEL_SELECT_PARCEL = "edit_parcel:select_parcel";

/**
 * 土地区画作成モードにおいて、3Dビューのラジアルメニュー(Ctrl)で作成モードが切り替わった (Unity → UI)。
 * ペイロードは { mode }（`empty_area` / `along_path` / … / `split_random`）。
 *
 * 作成モードの真実源はUnity側(`ParcelEditorManager.CurrentMode`)で、作成パネルはこれを受けて
 * 入力欄の構成と選択済み表示を合わせる。**現在と同じモードで届いた場合は「選択のリセット」**
 * （3DビューでEscを押した／同じモードを選び直した）を意味し、パネルは選択状態だけを捨てて
 * 幅・奥行きなどの入力値は保持する。
 */
export const IPC_EVENT_EDIT_PARCEL_SET_MODE = "edit_parcel:set_mode";

/** 土地区画作成モード（empty_area）において、3Dビュー上で矩形の角度が変更された */
export const IPC_EVENT_EDIT_PARCEL_UPDATE_ROTATION = "edit_parcel:update_rotation";

/** 表示オプションパネルの状態問い合わせに対するレスポンス */
export const IPC_EVENT_VIEW_CURRENT_STATE = "view:current_state";

/**
 * オブジェクト単位の表示フィルタの現在値(Unity → UI)。
 * ペイロードは `{ hidden_building_ids: number[], hidden_path_ids: number[] }`。
 * `sendSetObjectFilter()` の適用後と `sendQueryObjectFilter()` への応答で流れる。
 * 表示状態の正はUnity側にあるため、複数のパネルはこれを受けて表示を揃える。
 */
export const IPC_EVENT_VIEW_OBJECT_FILTER_STATE = "view:object_filter_state";

/**
 * 3Dビューのカメラ状態(Unity → UI)。`sendCameraQueryState()` への応答。
 * ペイロードは `{ position: [x,y,z], yaw: number }` で、yaw は北(ワールド+Z)を0とする
 * 時計回りの度。2D都市マップがこれを受けてカメラ位置マーカーを重ね描きする。
 */
export const IPC_EVENT_CAMERA_STATE = "camera:state";

/** 土地区画形状編集モードにおいて、編集中のポリゴンが変更された */
export const IPC_EVENT_EDIT_PARCEL_UPDATE_SHAPE = "edit_parcel:update_shape";

/** 地区編集モードで選択中地区が変化した通知(Unity → UI)。zone_id=0 は選択なし。 */
export const IPC_EVENT_EDIT_ZONE_SELECTED = "edit_zone:selected";

/**
 * 地区編集モードの統合ツールで、統合元と統合先が決まった通知(Unity → UI)。
 * ペイロードは { target_zone_id, target_zone_name, source_zone_id, source_zone_name }。
 * 地区がひとつ消える不可逆操作のため Unity は送信せず、アイコンパネルがこれを受けて
 * 確定専用の小ウィンドウ(`zone_merge`)を開き、そこの確定ボタンで UI が送信する
 * (結果は `sendEditZoneFinishMerge` で Unity へ返す)。
 */
export const IPC_EVENT_EDIT_ZONE_REQUEST_MERGE = "edit_zone:request_merge";

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
 * 造成範囲の作図モードにおいて、描画中の範囲ポリゴンが変更された(Unity → UI)。
 * ペイロードは { polygon: number[][], finalized: boolean }([x, z] の頂点列)。
 * finalized は範囲が閉じて頂点編集に移った後だけ true になり、GradingPanel はそのときだけ見積もりを取る。
 */
export const IPC_EVENT_EDIT_GRADING_UPDATE_SHAPE = "edit_grading:update_shape";

/**
 * 公共交通の路線設置モードにおいて、配置中の巡回点(停留所・経由点)が変更された(Unity → UI)。
 * ペイロードは { points: TransitRoutePoint[] }(巡回順。各点は {kind: "stop" | "waypoint", position: [x, y, z]}。
 * 公共交通パスは相対Y=0 なので y は0)。路線の新設・経路編集パネル(TransitCreationPanel)がこれを受けて
 * 停留所・経由点の数の表示と、確定で送る巡回点を更新する。
 */
export const IPC_EVENT_EDIT_TRANSIT_UPDATE_POINTS = "edit_transit:update_points";

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
 * プロップ配置(個別モード)において、3D側の操作(中ボタンドラッグ回転・PageUp/PageDown の高さ変更)で
 * 次に置くゴーストの設定が変わった(Unity → UI)。ペイロードは { rotation, height }。
 * PropPlacementPanel はステッパー表示を追従させるだけで、`edit_prop:change_settings` へエコーしない
 * (建物直接配置の `edit_building:update_direct_angle` と同じ扱い)。
 */
export const IPC_EVENT_EDIT_PROP_PLACEMENT_SETTINGS = "edit_prop:placement_settings";

/**
 * ブルドーザモードで、Unity が即削除せず確認を求める対象をクリックした(Unity → UI。コックピット宛)。
 * ペイロードは { object_type, id, prop_type }。object_type は現状 "prop_scatter"(範囲配置プロップ=
 * 森などを散布範囲ごと撤去する不可逆操作)のみ。コックピットが確認ダイアログを出し、
 * 結果を `sendRemoveObjectConfirmResult` で返す。
 */
export const IPC_EVENT_REMOVE_OBJECT_REQUEST_CONFIRM = "remove_object:request_confirm";

/** Unityの初期描画完了はRustに保持されるため、Tipsの購読開始順に依存しない。 */
export function getInitialLoadingReady(): Promise<boolean> {
  return invoke<boolean>("get_initial_loading_ready");
}

export function startGameFromTips(): Promise<void> {
  return invoke("close_tips_window");
}
