/**
 * 道路建設パネル用の状態管理フック
 *
 * グラフィックプログラム(Unity)側でマップ上のノード配置を行う前提のため、
 * マウスイベントハンドラは含まず、設定とIPCからのノード受信に専念する。
 *
 * 選択は3カテゴリ(道路 road / 軌道 rail / インフラ infra)に分かれる。
 *   - 道路・インフラ: path_type を選び、プルダウンで単一バリアントを選ぶ(selectedVariant)。
 *   - 軌道: 鉄道/モノレール/ロープウェイ + track_type(通常/橋梁/駅) + 電化スイッチ(鉄道のみ)で
 *     候補を絞り、プルダウンで単線バリアントを確定し、線路数・進行方向・線路間隔で複線を構成する。
 *     線路間隔の既定はバリアントのマスター幅で、広げる/縮めるボタンで一定範囲内を
 *     調整できる(パス1本確定ごとに既定へ戻る)。
 *     複線は中心線を平行な単線パス群へ展開して一括敷設する(railComponents)。
 *     ロープウェイは道路と同じ「両方向の単一パス1本」扱いで、複線展開しない。
 *
 * 状態:
 *   category: 選択中のカテゴリ("road" | "rail" | "infra")
 *   selectedType: 選択中の path_type(軌道では鉄道/モノレール/ロープウェイの type)
 *   selectedVariant: 敷設する単一(=軌道では中心線の代表)バリアントID
 *   curveMode: 描画モード (straight/curved)。真実源はUnity側(ラジアルメニューで切替)であり、
 *              edit_path.update_shape の is_curved 受信によってのみ更新される。
 *   height/embankmentHeight: 選択中(または次回配置)ノードの標高・盛土。既存ノードを選んで
 *              数値で微調整する用途のためパネルにも残す。select_node 受信で表示を同期し、
 *              ステッパー変更を change_node/change_embankment で Unity へ送り返す双方向同期。
 *   nodes: IPCから受信したノード配列（edit_path.update_shape）
 *
 * 曲線/直線・吸着の設定はUnity側の操作系(Ctrlラジアルメニュー)へ移管済みのため、このフックでは扱わない。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { invoke } from "@tauri-apps/api/core";
import type { MasterData, PathMasterEntry, CombinedPathComponent } from "../../types";
import type {
  BuildPoint,
  ControlPoint,
  CurveMode,
} from "./pathBuildConstants";
import {
  sendChangeState,
  sendSetPlatformSide,
  sendEditPathChangeNode,
  sendEditPathChangeEmbankment,
  sendEditPathChangeLateralOffsets,
  sendEditPathSetFollowTerrain,
  IPC_EVENT_EDIT_PATH_SELECT_NODE,
  IPC_EVENT_EDIT_PATH_UPDATE_SHAPE,
} from "../../utils/ipc";
import { fetchApi } from "../../utils/api";
import {
  DIR_BOTH,
  PATH_ROPEWAY,
  PLATFORM_SIDE_OUTSIDE,
  TRAFFIC_HAND_LEFT,
  TRACK_NORMAL,
} from "../../constants";
import { isInfraPathType } from "../../local-constants";
import {
  computeAnchorOffsetDirs,
  offsetControlPoint,
  offsetPolyline3D,
} from "./pathOffset";
import {
  MIN_TRACK_COUNT,
  MAX_TRACK_COUNT,
  FALLBACK_TRACK_SPACING,
  TRACK_SPACING_STEP,
  TRACK_SPACING_MIN_FACTOR,
  TRACK_SPACING_MAX_FACTOR,
  clampTrackSpacing,
  isRailType,
  variantTrackType,
  isElectrifiedVariant,
  resolveRailVariant,
  railVariantsForCombo,
  availableTrackTypes,
  electrificationOptions,
  defaultTravelDirections,
  cycleDirection,
  buildRailComponents,
  computeLateralOffsets,
} from "./railBuild";

/** 地形追従の個別設定の「未設定」(バリアント既定に従う)を表すセンチネル。バックエンド VALUE_NONE と一致。 */
export const FOLLOW_TERRAIN_UNSET = -1;

/** 建設パネルの3カテゴリ */
export type PathCategory = "road" | "rail" | "infra";

/** path_type からカテゴリを判定する(既存パス編集の読み込み時に使う)。 */
function categoryOfType(type: number | null): PathCategory | null {
  if (type == null) return null;
  if (isRailType(type)) return "rail";
  if (isInfraPathType(type)) return "infra";
  return "road";
}

function clampInt(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, Math.round(value)));
}

/* ---- 制御点計算 (Unity側 RecomputeAllControlPoints と同一ロジック) ---- */

function computeSplineOrBezierControls(
  points: BuildPoint[],
  curveMode: CurveMode
): { start: ControlPoint; end: ControlPoint } {
  if (curveMode === "curved") {
    return computeSplineControls(points);
  }
  return { start: null, end: null };
}

function computeSplineControls(points: BuildPoint[]): {
  start: ControlPoint;
  end: ControlPoint;
} {
  if (points.length < 2) {
    return { start: null, end: null };
  }

  if (points.length === 2) {
    const s = points[0];
    const e = points[1];
    return {
      start: { x: s.x + (e.x - s.x) / 3, z: s.z + (e.z - s.z) / 3 },
      end: { x: e.x - (e.x - s.x) / 3, z: e.z - (e.z - s.z) / 3 },
    };
  }

  let t0x = (points[1].x - points[0].x) * 0.5;
  let t0z = (points[1].z - points[0].z) * 0.5;
  if (points.length > 2) {
    t0x = (points[2].x - points[0].x) / 6;
    t0z = (points[2].z - points[0].z) / 6;
  }
  const start = { x: points[0].x + t0x, z: points[0].z + t0z };

  const n = points.length;
  let tnx = (points[n - 1].x - points[n - 2].x) * 0.5;
  let tnz = (points[n - 1].z - points[n - 2].z) * 0.5;
  if (n > 2) {
    tnx = (points[n - 1].x - points[n - 3].x) / 6;
    tnz = (points[n - 1].z - points[n - 3].z) / 6;
  }
  const end = { x: points[n - 1].x - tnx, z: points[n - 1].z - tnz };

  return { start, end };
}

/* ---- フック本体 ---- */

/**
 * ウェイポイント(中間アンカー)1個分のベジエ制御点。座標は [x, 相対高さ, z]。
 * null はUnity側で未設定(確定送信ではバックエンドが自動補完する)を意味する。
 */
export type WaypointControls = {
  control_in: number[] | null;
  control_out: number[] | null;
};

export function usePathConstruction(
  masterData: MasterData | null,
  apiBaseUrl?: string,
  pathId?: number | null,
) {
  const [category, setCategory] = useState<PathCategory | null>(null);
  const [selectedType, setSelectedType] = useState<number | null>(null);
  const [selectedVariant, setSelectedVariant] = useState<number | null>(null);
  // 曲線モードの真実源はUnity側。update_shape の is_curved 受信でのみ更新され、
  // 確定送信(curved)・形状プレビュー・コスト見積りが参照する。
  const [curveMode, setCurveMode] = useState<CurveMode>("curved");
  // 標高・盛土は「既存ノードを選択して数値で微調整する」用途があるためパネルにも残す。
  // 表示値の真実源はUnity(選択ノードの値を select_node で受信)で、ステッパー変更を
  // change_node/change_embankment でUnityへ送り返す双方向同期。selectedNodeIndex=-1 は次回配置ノード。
  const [height, setHeight] = useState(0);
  const [embankmentHeight, setEmbankmentHeight] = useState(0);
  const [selectedNodeIndex, setSelectedNodeIndex] = useState<number>(-1);
  // 駅パスのプラットフォーム設置側(PLATFORM_SIDE_OUTSIDE/INSIDE/BOTH)。
  // platform_width を持つバリアント(駅)を選択しているときだけ意味を持つ。
  // 建設時にのみ指定でき、建設後の変更はできない(バックエンド側の制約)。
  const [platformSide, setPlatformSide] = useState<number>(PLATFORM_SIDE_OUTSIDE);
  // 地形追従の個別設定(-1=未設定[バリアント既定]/0=非追従/1=追従)。真実源はUnity側で、
  // トグル操作は sendEditPathSetFollowTerrain で送り、値は edit_path:update_shape の
  // follow_terrain 受信で反映される。height_editable のバリアントでのみ意味を持つ。
  const [followTerrain, setFollowTerrainState] = useState<number>(FOLLOW_TERRAIN_UNSET);
  const [nodes, setNodes] = useState<BuildPoint[]>([]);
  const [rawPoints, setRawPoints] = useState<number[][]>([]);
  const [rawEmbankmentHeights, setRawEmbankmentHeights] = useState<number[]>([]);
  const [startControl, setStartControl] = useState<ControlPoint>(null);
  const [endControl, setEndControl] = useState<ControlPoint>(null);
  // Unityから受信した中間アンカーの制御点。rawPoints.slice(1, -1) と同順・同数。
  // 確定送信時に waypoints へ {position, control_in, control_out} 形式で含めることで、
  // ドラッグで調整した曲線形状がバックエンドに永続化される。
  const [rawWaypointControls, setRawWaypointControls] = useState<WaypointControls[]>([]);

  /* ---- 軌道(複線)敷設の設定 ---- */
  const [railType, setRailType] = useState<number | null>(null);
  const [trackType, setTrackType] = useState<number>(TRACK_NORMAL);
  const [electrified, setElectrified] = useState<boolean>(false);
  const [trackCount, setTrackCount] = useState<number>(2);
  // 線路間隔の調整量(m)。実効間隔 = マスター幅 + この値(クランプあり)。パス1本確定ごとに0へ戻す。
  const [trackSpacingDelta, setTrackSpacingDelta] = useState<number>(0);
  // 各線の進行方向のユーザー上書き(null=既定を使う)。長さは trackCount に一致させる。
  const [directionOverrides, setDirectionOverrides] = useState<(number | null)[]>([null, null]);
  // 都市の通行方向。既定の進行方向の左右割り当てに使う。既定は左側通行。
  const [trafficHand, setTrafficHand] = useState<number>(TRAFFIC_HAND_LEFT);

  const [estimatedCost, setEstimatedCost] = useState<number | null>(null);
  // 完全に平坦・標高ゼロで敷設した場合の標準コスト。推定コストが標準の何倍かの表示に使う。
  const [estimatedStandardCost, setEstimatedStandardCost] = useState<number | null>(null);
  const [estimatedDurationDays, setEstimatedDurationDays] = useState<number | null>(null);
  const [costPerSimulationDays, setCostPerSimulationDays] = useState<number | null>(null);
  const [costSimulationDays, setCostSimulationDays] = useState<number | null>(null);
  const [nextEpochConstructionBudget, setNextEpochConstructionBudget] = useState<number | null>(null);
  const [isCostLoading, setIsCostLoading] = useState(false);
  const costAbortControllerRef = useRef<AbortController | null>(null);

  /* ---- 派生データ ---- */

  const variantsForType = useCallback(
    (type: number): { id: number; data: PathMasterEntry }[] => {
      if (!masterData?.paths) return [];
      const variants: { id: number; data: PathMasterEntry }[] = [];
      for (const key in masterData.paths) {
        const entry = masterData.paths[key];
        if (entry.type === type) {
          variants.push({ id: parseInt(key), data: entry });
        }
      }
      return variants;
    },
    [masterData]
  );

  const isRail = category === "rail";

  const selectedVariantData = useMemo((): PathMasterEntry | null => {
    if (selectedVariant === null || !masterData?.paths) return null;
    return masterData.paths[selectedVariant] ?? null;
  }, [selectedVariant, masterData]);

  /**
   * Unity(グラフィックプログラム)に渡す代表バリアント。
   * 軌道複線でも Unity の敷設操作(ノード配置)は中心線1本を対象とし、
   * 確定・コスト見積りの平行展開は confirmPath / コスト見積り側で行う。
   * 3Dプレビューの複線表示は、別途 lateral_offsets(STATE_EDIT_PATH params と
   * edit_path:change_lateral_offsets)で各線のオフセットを渡して行う。
   */
  const unityPathVariant = selectedVariant;
  const unityPathType = useMemo((): number | null => {
    if (selectedVariant === null) return null;
    return masterData?.paths?.[selectedVariant]?.type ?? null;
  }, [selectedVariant, masterData]);

  const heightEditable = useMemo(() => {
    return selectedVariantData?.height_editable ?? false;
  }, [selectedVariantData]);

  // インフラパス（送電線・導水管など）かどうか。
  // インフラパスは地形変形を伴わず盛土・切土(embankment_heights)を使わないため、
  // パネル側で盛土入力を非表示にする判定に使う。
  const isInfrastructure = useMemo(() => {
    return isInfraPathType(selectedVariantData?.type);
  }, [selectedVariantData]);

  const curveAllowed = useMemo(() => {
    return selectedVariantData?.curve_allowed !== false;
  }, [selectedVariantData]);

  /**
   * 選択中バリアントがプラットフォーム(platform_width)を持つ駅パスか。
   * true のときだけパネルにホーム設置側(外側/内側/両側)のトグルを表示する。
   */
  const hasPlatform = useMemo(() => {
    const w = selectedVariantData?.platform_width;
    return typeof w === "number" && w > 0;
  }, [selectedVariantData]);

  /**
   * 各線の実効進行方向(既定 + ユーザー上書き)。
   * 既定は都市の通行方向と線路数から定まり、ユーザーが矢印をクリックした線だけ上書きする。
   * trafficHand や trackCount が変わると、上書きしていない線は自動的に既定へ追従する。
   */
  const trackDirections = useMemo(() => {
    const defaults = defaultTravelDirections(trackCount, trafficHand);
    return defaults.map((d, i) => directionOverrides[i] ?? d);
  }, [trackCount, trafficHand, directionOverrides]);

  /**
   * 線路間隔の既定値(m)。バリアントのマスター幅(width)をそのまま使う
   * (=各線のコリドーが隙間なく隣接する)。width 未定義の異常時のみフォールバック値。
   */
  const trackSpacingBase = useMemo(() => {
    const w = selectedVariantData?.width;
    return typeof w === "number" && w > 0 ? w : FALLBACK_TRACK_SPACING;
  }, [selectedVariantData]);

  /**
   * 実効の線路間隔(m)。既定値に広げる/縮めるボタンの調整量を加え、
   * 既定値の [TRACK_SPACING_MIN_FACTOR, TRACK_SPACING_MAX_FACTOR] 倍の範囲へクランプする。
   */
  const trackSpacing = useMemo(
    () => clampTrackSpacing(trackSpacingBase, trackSpacingDelta),
    [trackSpacingBase, trackSpacingDelta]
  );

  /** 線路間隔をボタン1回分(TRACK_SPACING_STEP)だけ広げる(+1)/縮める(-1)。 */
  const adjustTrackSpacing = useCallback((direction: 1 | -1) => {
    setTrackSpacingDelta(
      (prev) =>
        clampTrackSpacing(trackSpacingBase, prev + direction * TRACK_SPACING_STEP) -
        trackSpacingBase
    );
  }, [trackSpacingBase]);

  // クランプ端に達したら該当ボタンを無効化するための判定(浮動小数の等値比較を避ける)。
  const canShrinkTrackSpacing = trackSpacing > trackSpacingBase * TRACK_SPACING_MIN_FACTOR + 1e-9;
  const canWidenTrackSpacing = trackSpacing < trackSpacingBase * TRACK_SPACING_MAX_FACTOR - 1e-9;

  /**
   * 軌道複線を中心線から展開するためのコンポーネント群。
   * 新規敷設の軌道カテゴリでのみ生成し、道路・インフラ・既存パス編集では null。
   * null のときは単一パスとして扱う(confirmPath / コスト見積り / プレビュー共通)。
   * 単線(trackCount=1)でも travel_direction を送るためコンポーネント経路を使う。
   * ロープウェイは描画・敷設とも道路と同じ「両方向の単一パス1本」なので複線展開しない。
   */
  const railComponents = useMemo((): CombinedPathComponent[] | null => {
    if (!isRail || selectedVariant === null || pathId != null) return null;
    if (selectedVariantData?.type === PATH_ROPEWAY) return null;
    return buildRailComponents(selectedVariant, trackCount, trackSpacing, trackDirections);
  }, [isRail, selectedVariant, selectedVariantData, pathId, trackCount, trackSpacing, trackDirections]);

  /** 現在の軌道カテゴリで選べる track_type 一覧 */
  const railTrackTypeOptions = useMemo(() => {
    return railType != null ? availableTrackTypes(masterData, railType) : [];
  }, [masterData, railType]);

  /**
   * 現在の(鉄道種別, track_type, 電化)に一致する最終バリアントの候補一覧。
   * 同じ組み合わせでも複数の線路規格がありうるため、パネルはこれをプルダウンで表示し、
   * プレイヤーに最終バリアントを選ばせる。新規敷設の軌道カテゴリでのみ意味を持つ。
   */
  const railVariantOptions = useMemo(() => {
    if (!isRail || railType == null || pathId != null) return [];
    return railVariantsForCombo(masterData, railType, trackType, electrified);
  }, [isRail, railType, trackType, electrified, masterData, pathId]);

  /** 電化トグルの操作可否(電化・非電化の両バリアントが存在するときだけ切替可能) */
  const railElectrificationOptions = useMemo(() => {
    if (railType == null) return { hasElectrified: false, hasNonElectrified: false };
    return electrificationOptions(masterData, railType, trackType);
  }, [masterData, railType, trackType]);

  /* ---- 選択・敷設モードの制御 ---- */

  /**
   * Unityの3Dプレビューへ渡す複合パス(軌道複線)の各線横オフセット(m)を求める。
   * 軌道の新規敷設のみ非空で、道路・インフラ・ロープウェイ・既存パス編集は
   * 空配列(=中心線1本の単一プレビュー)。railComponents と同じ条件・同じ導出
   * (線路間隔=マスター幅+調整量)だが、STATE_EDIT_PATH 送信時点では
   * selectedVariant がまだ更新されていないため variantId 引数で直接計算する。
   * spacingDelta は継続配置(continuePlacement)が間隔リセット後の値を state 更新を
   * 待たずに同じ呼び出しで使うための上書き引数。省略時は現在の調整量を使う。
   */
  const lateralOffsetsForVariant = useCallback((variantId: number, spacingDelta: number = trackSpacingDelta): number[] => {
    const data = masterData?.paths?.[variantId];
    if (pathId != null || !data || !isRailType(data.type) || data.type === PATH_ROPEWAY) {
      return [];
    }
    const base = typeof data.width === "number" && data.width > 0 ? data.width : FALLBACK_TRACK_SPACING;
    return computeLateralOffsets(trackCount, clampTrackSpacing(base, spacingDelta));
  }, [masterData, pathId, trackCount, trackSpacingDelta]);

  /**
   * Unityの3Dプレビューへ渡す複合パス(軌道複線)の各線の進行方向(DIR_*)を求める。
   * lateralOffsetsForVariant と同じ条件で判定し、非空になる場合は同じ本数・同じ並びの
   * trackDirections を返す。駅バリアントのプレビューでホーム設置側の解決
   * (TrackPath.ResolvePlatformExtents)が走行方向を参照するため、オフセット列と対で渡す。
   */
  const travelDirectionsForVariant = useCallback((variantId: number): number[] => {
    const data = masterData?.paths?.[variantId];
    if (pathId != null || !data || !isRailType(data.type) || data.type === PATH_ROPEWAY) {
      return [];
    }
    return trackDirections;
  }, [masterData, pathId, trackDirections]);

  /**
   * バリアントを選び、Unity を敷設モード(STATE_EDIT_PATH)に遷移させる。
   * 道路・インフラは選択コンポーネントから直接呼ばれ、軌道は
   * 鉄道種別/track_type/電化の解決結果から下の useEffect が呼ぶ。
   * 曲線モードの初期化(曲線許可なら curved、不許可なら straight)はUnity側の
   * InitializeRoadPlacement が行い、update_shape の is_curved で同期される。
   */
  const selectVariant = useCallback((variantId: number) => {
    setSelectedVariant(variantId);
    const variantData = masterData?.paths?.[variantId];
    // 軌道バリアントを直接読み込んだとき(既存パス編集の復元など)は、
    // 鉄道種別/track_type/電化 の各ボタン状態を実バリアントに合わせて同期する。
    if (variantData && isRailType(variantData.type)) {
      setRailType(variantData.type);
      setTrackType(variantTrackType(variantData));
      setElectrified(isElectrifiedVariant(variantData));
    }
    sendChangeState("STATE_EDIT_PATH", {
      path_type: variantData?.type ?? null,
      path_variant: variantId,
      path_id: pathId ?? null,
      // 駅パスのホーム設置側の初期値。敷設モード初期化時にUnityへ渡し、
      // 以後の切り替えは下の useEffect が edit_path:change_platform_side で逐次反映する。
      platform_side: platformSide,
      // 軌道複線の各線横オフセット・進行方向の初期値。3Dプレビューを平行N本で描かせる。
      // 以後の線路数・進行方向の変更は下の useEffect が edit_path:change_lateral_offsets で逐次反映する。
      lateral_offsets: lateralOffsetsForVariant(variantId),
      travel_directions: travelDirectionsForVariant(variantId),
    }).catch((err) => {
      console.error("[usePathConstruction] sendChangeState failed:", err);
    });
  }, [masterData, pathId, platformSide, lateralOffsetsForVariant, travelDirectionsForVariant]);

  /**
   * 軌道: 鉄道種別/track_type/電化 が変わったら単線バリアントを解決して敷設モードを更新する。
   * 線路数・進行方向・線路間隔は同じ中心線バリアントのまま複線展開だけを変えるので、ここでは扱わない
   * (それらの変更で Unity 敷設モードを再初期化しないため、配置中のノードが消えない)。
   */
  useEffect(() => {
    // 既存パス編集(pathId!=null)は読み込んだ単一パスをそのまま編集するため、
    // 種別/track_type/電化 からの再解決は行わない(選択が上書きされるのを防ぐ)。
    if (pathId != null || category !== "rail" || railType == null) return;
    const resolved = resolveRailVariant(masterData, railType, trackType, electrified);
    if (resolved) {
      if (resolved.id !== selectedVariant) {
        selectVariant(resolved.id);
      }
    } else {
      setSelectedVariant(null);
    }
    // selectedVariant / selectVariant は意図的に依存に含めない(解決キーの変化時のみ再解決する)。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [category, railType, trackType, electrified, masterData, pathId]);

  /* ---- カテゴリ/種別の選択ハンドラ ---- */

  /** 選択・配置状態をクリアして Unity を通常状態へ戻す(カテゴリは変えない)。 */
  const clearSelection = useCallback(() => {
    setSelectedType(null);
    setSelectedVariant(null);
    setRailType(null);
    setTrackSpacingDelta(0);
    setHeight(0);
    setEmbankmentHeight(0);
    setSelectedNodeIndex(-1);
    setCurveMode("curved");
    setNodes([]);
    setRawPoints([]);
    setStartControl(null);
    setEndControl(null);
    setRawWaypointControls([]);
    setEstimatedCost(null);
    sendChangeState("STATE_NORMAL").catch((err) => {
      console.error("[usePathConstruction] sendChangeState (clear) failed:", err);
    });
  }, []);

  /** 3カテゴリ(道路/軌道/インフラ)の切り替え。選択・配置をリセットする。 */
  const onCategoryChange = useCallback((next: PathCategory | null) => {
    clearSelection();
    setCategory(next);
  }, [clearSelection]);

  /** 道路・インフラの path_type 選択(バリアントはプルダウンで別途選ぶ)。 */
  const onTypeChange = useCallback((type: number | null) => {
    setSelectedType(type);
    setSelectedVariant(null);
    // 既存パス編集の読み込み経路(onTypeChange → selectVariant)からも呼ばれるため、
    // 未設定ならカテゴリを type から補完する。
    setCategory((prev) => prev ?? categoryOfType(type));
    if (type != null && isRailType(type)) {
      setRailType(type);
    }
  }, []);

  /** 軌道カテゴリ: 鉄道種別(鉄道/モノレール/ロープウェイ)を選ぶ。 */
  const selectRailType = useCallback((type: number) => {
    const availTT = availableTrackTypes(masterData, type);
    const nextTrackType = availTT.includes(trackType) ? trackType : (availTT[0] ?? TRACK_NORMAL);
    const opts = electrificationOptions(masterData, type, nextTrackType);
    let nextElec = electrified;
    if (nextElec && !opts.hasElectrified) nextElec = false;
    if (!nextElec && !opts.hasNonElectrified) nextElec = opts.hasElectrified;
    setCategory("rail");
    setSelectedType(type);
    setRailType(type);
    setTrackType(nextTrackType);
    setElectrified(nextElec);
    setDirectionOverrides(Array(trackCount).fill(null));
  }, [masterData, trackType, electrified, trackCount]);

  /** 軌道カテゴリ: track_type(通常/橋梁/駅)を選ぶ。 */
  const selectTrackType = useCallback((tt: number) => {
    if (railType == null) return;
    const opts = electrificationOptions(masterData, railType, tt);
    let nextElec = electrified;
    if (nextElec && !opts.hasElectrified) nextElec = false;
    if (!nextElec && !opts.hasNonElectrified) nextElec = opts.hasElectrified;
    setTrackType(tt);
    setElectrified(nextElec);
  }, [masterData, railType, electrified]);

  /** 軌道カテゴリ: 電化トグル。 */
  const setElectrifiedChecked = useCallback((val: boolean) => {
    setElectrified(val);
  }, []);

  /** 軌道カテゴリ: 線路数(1〜10)。既定の進行方向を引き直す(上書きはクリア)。 */
  const changeTrackCount = useCallback((n: number) => {
    const c = clampInt(n, MIN_TRACK_COUNT, MAX_TRACK_COUNT);
    setTrackCount(c);
    setDirectionOverrides(Array(c).fill(null));
  }, []);

  /** 軌道カテゴリ: index番目の線の進行方向を 順→逆→両 と循環させる。 */
  const cycleTrackDirection = useCallback((index: number) => {
    setDirectionOverrides((prev) => {
      const defaults = defaultTravelDirections(trackCount, trafficHand);
      const current = prev[index] ?? defaults[index];
      const next = prev.slice();
      // 配列長を trackCount に合わせてから該当indexを上書きする
      while (next.length < trackCount) next.push(null);
      next[index] = cycleDirection(current);
      return next;
    });
  }, [trackCount, trafficHand]);

  /**
   * パネルの標高ステッパー変更を、選択中ノード(または次回配置ノード)へ反映する。
   * Unity へ change_node で送り返し、Unity 側で該当ノードの相対高さを更新させる。
   */
  const updateHeight = useCallback((value: number) => {
    setHeight(value);
    sendEditPathChangeNode(selectedNodeIndex, value).catch((err) => {
      console.error("[usePathConstruction] sendEditPathChangeNode failed:", err);
    });
  }, [selectedNodeIndex]);

  /**
   * パネルの盛土ステッパー変更を、選択中ノード(または次回配置ノード)へ反映する。
   * 盛土オフセットは -5〜+5m にクランプして Unity へ change_embankment で送り返す。
   */
  const updateEmbankmentHeight = useCallback((value: number) => {
    const clamped = Math.max(-5, Math.min(5, value));
    setEmbankmentHeight(clamped);
    sendEditPathChangeEmbankment(selectedNodeIndex, clamped).catch((err) => {
      console.error("[usePathConstruction] sendEditPathChangeEmbankment failed:", err);
    });
  }, [selectedNodeIndex]);

  /**
   * 地形追従トグルの変更を Unity へ送る。真実源はUnity側のため state は直接更新せず、
   * Unity が返す edit_path:update_shape の follow_terrain 受信で反映される。
   * 値は -1(未設定)/0(非追従)/1(追従)。
   */
  const setFollowTerrain = useCallback((value: number) => {
    sendEditPathSetFollowTerrain(value).catch((err) => {
      console.error("[usePathConstruction] sendEditPathSetFollowTerrain failed:", err);
    });
  }, []);

  const resetPath = useCallback(() => {
    setCategory(null);
    clearSelection();
  }, [clearSelection]);

  /**
   * 連続設置: 1本目のパス確定後に、選択中のバリアント・軌道設定を維持したまま
   * パス配置状態だけをクリアし、Unity を再び STATE_EDIT_PATH に戻す。
   * resetTrackSpacing=true(確定後)のときは線路間隔の調整量も既定へ戻す。
   * キャンセル(同じパスの引き直し)では維持するため false で呼ぶ。
   */
  const continuePlacement = useCallback((resetTrackSpacing: boolean = false) => {
    if (selectedVariant === null) return;
    const variantData = masterData?.paths?.[selectedVariant];
    // STATE_EDIT_PATH の初期オフセットにリセット後の間隔を使うため、
    // state 更新(非同期)とは別に、明示的な調整量を lateralOffsetsForVariant へ渡す。
    const nextSpacingDelta = resetTrackSpacing ? 0 : trackSpacingDelta;
    if (resetTrackSpacing) setTrackSpacingDelta(0);
    const defaultHeight = Math.max(0, variantData?.min_height ?? 0);
    setHeight(defaultHeight);
    setEmbankmentHeight(0);
    setSelectedNodeIndex(-1);
    setNodes([]);
    setRawPoints([]);
    setRawEmbankmentHeights([]);
    setStartControl(null);
    setEndControl(null);
    setRawWaypointControls([]);
    setEstimatedCost(null);
    setEstimatedDurationDays(null);
    setCostPerSimulationDays(null);
    setCostSimulationDays(null);
    setNextEpochConstructionBudget(null);
    sendChangeState("STATE_EDIT_PATH", {
      path_type: variantData?.type ?? null,
      path_variant: selectedVariant,
      path_id: pathId ?? null,
      platform_side: platformSide,
      lateral_offsets: lateralOffsetsForVariant(selectedVariant, nextSpacingDelta),
      travel_directions: travelDirectionsForVariant(selectedVariant),
    }).catch((err) => {
      console.error("[usePathConstruction] sendChangeState (continue) failed:", err);
    });
  }, [selectedVariant, masterData, pathId, platformSide, trackSpacingDelta, lateralOffsetsForVariant, travelDirectionsForVariant]);

  /**
   * 現在のパス形状をバックエンドに送信して確定する。
   * Tauri側で直接HTTP通信を行い、Unityを経由しない。
   *
   * 軌道複線(railComponents 非null)は、中心線を各コンポーネントの横/高さオフセットで
   * 平行パス群へ展開し、travel_direction を設定して1回のPOSTで一括作成する。
   * それ以外(道路・インフラ・単一パス・既存パス編集)は従来どおり単一パスとして送る。
   */
  const confirmPath = useCallback(async (): Promise<void> => {
    if (!apiBaseUrl || selectedVariant === null || rawPoints.length < 2) {
      throw new Error("Invalid state for path confirmation");
    }

    const isCurved = curveMode !== "straight";

    // 軌道複線: 中心線を各コンポーネントのオフセットで展開して一括作成する。
    if (railComponents) {
      const anchorDirs = computeAnchorOffsetDirs(
        rawPoints.map((p) => ({ x: p[0], z: p[2] }))
      );
      const tempIdBase = `build_${Date.now()}`;

      const pathsPayload = railComponents.map((comp, idx) => {
        const pts = offsetPolyline3D(
          rawPoints,
          comp.lateral_offset ?? 0,
          comp.height_offset ?? 0
        );
        const data: Record<string, unknown> = {
          path_variant: comp.path_variant,
          start_point: pts[0],
          end_point: pts[pts.length - 1],
          curved: isCurved,
          travel_direction: comp.travel_direction ?? DIR_BOTH,
          path_temp_id: `${tempIdBase}_${idx}`,
        };
        // 駅バリアント(platform_width>0)ならホーム設置側を全線共通で付与する。
        if (hasPlatform) {
          data.platform_side = platformSide;
        }
        if (isCurved && startControl) {
          const oc = offsetControlPoint(startControl, anchorDirs[0], comp.lateral_offset ?? 0);
          if (oc) data.start_control = [oc.x, pts[0][1], oc.z];
        }
        if (isCurved && endControl) {
          const oc = offsetControlPoint(
            endControl,
            anchorDirs[anchorDirs.length - 1],
            comp.lateral_offset ?? 0
          );
          if (oc) data.end_control = [oc.x, pts[pts.length - 1][1], oc.z];
        }
        // ウェイポイント。中心線の制御点を受信している場合は、アンカーと同じ
        // オフセット方向で制御点も平行移動し、各線の曲線形状を中心線と相似に保つ。
        const waypointPts = pts.slice(1, -1);
        if (waypointPts.length > 0) {
          data.waypoints = waypointPts.map((pt, i) => {
            const wc = isCurved ? rawWaypointControls[i] : undefined;
            if (!wc || (!wc.control_in && !wc.control_out)) return pt;
            const dir = anchorDirs[i + 1];
            const ci = wc.control_in
              ? offsetControlPoint(
                  { x: wc.control_in[0], z: wc.control_in[wc.control_in.length - 1] },
                  dir,
                  comp.lateral_offset ?? 0
                )
              : null;
            const co = wc.control_out
              ? offsetControlPoint(
                  { x: wc.control_out[0], z: wc.control_out[wc.control_out.length - 1] },
                  dir,
                  comp.lateral_offset ?? 0
                )
              : null;
            return {
              position: pt,
              control_in: ci ? [ci.x, pt[1], ci.z] : null,
              control_out: co ? [co.x, pt[1], co.z] : null,
            };
          });
        }
        if (rawEmbankmentHeights.length > 0) {
          data.embankment_heights = rawEmbankmentHeights;
        }
        // 地形追従の個別上書き（未設定=バリアント既定のときは送らない）
        if (followTerrain !== FOLLOW_TERRAIN_UNSET) {
          data.follow_terrain = followTerrain;
        }
        return data;
      });

      const res = await fetchApi<{ paths: Array<{ path_temp_id: string | null; path_id: number }> }>(
        `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ paths: pathsPayload }),
        }
      );
      if (res && res.paths && res.paths.length > 0) {
        const firstPath = res.paths[0];
        void invoke("open_registered_window", {
          windowType: "path_detail",
          entityId: String(firstPath.path_id),
        });
      }
      return;
    }

    const pathData: Record<string, unknown> = {
      path_variant: selectedVariant,
      start_point: [rawPoints[0][0], rawPoints[0][1], rawPoints[0][2]],
      end_point: [rawPoints[rawPoints.length - 1][0], rawPoints[rawPoints.length - 1][1], rawPoints[rawPoints.length - 1][2]],
      curved: isCurved,
    };

    // 新規作成時のみ一時IDを付与する
    if (pathId == null) {
      pathData.path_temp_id = `build_${Date.now()}`;
    }

    // 駅パスのホーム設置側。建設時にのみ指定できる(既存パスの更新では送らない)。
    if (pathId == null && hasPlatform) {
      pathData.platform_side = platformSide;
    }

    // 制御点は対応するアンカーポイントと同じ相対高さを持つ
    if (isCurved && startControl) {
      pathData.start_control = [startControl.x, rawPoints[0][1], startControl.z];
    }
    if (isCurved && endControl) {
      pathData.end_control = [endControl.x, rawPoints[rawPoints.length - 1][1], endControl.z];
    }

    // ウェイポイント（中間点）。Unityから制御点を受信している場合は
    // {position, control_in, control_out} 形式で送り、ドラッグ調整した曲線を永続化する。
    // 制御点が無い場合は位置のみ送り、バックエンドの自動補完(Catmull-Rom)に任せる。
    const waypoints = rawPoints.slice(1, -1);
    if (waypoints.length > 0) {
      pathData.waypoints = waypoints.map((pt, i) => {
        const wc = isCurved ? rawWaypointControls[i] : undefined;
        if (wc && (wc.control_in || wc.control_out)) {
          return {
            position: [pt[0], pt[1], pt[2]],
            control_in: wc.control_in,
            control_out: wc.control_out,
          };
        }
        return [pt[0], pt[1], pt[2]];
      });
    }

    // 盛土オフセット（各アンカーの盛土・切土高さ。-5〜+5m）
    if (rawEmbankmentHeights.length > 0) {
      pathData.embankment_heights = rawEmbankmentHeights;
    }

    // 地形追従の個別上書き（未設定=バリアント既定のときは送らない）
    if (followTerrain !== FOLLOW_TERRAIN_UNSET) {
      pathData.follow_terrain = followTerrain;
    }

    if (pathId != null) {
      // 既存パス更新
      await fetchApi(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/${pathId}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(pathData),
      });
    } else {
      // 新規作成
      const res = await fetchApi<{ paths: Array<{ path_temp_id: string | null; path_id: number }> }>(
        `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ paths: [pathData] }),
        }
      );
      if (res && res.paths && res.paths.length > 0) {
        const firstPath = res.paths[0];
        void invoke("open_registered_window", {
          windowType: "path_detail",
          entityId: String(firstPath.path_id),
        });
      }
    }
  }, [apiBaseUrl, selectedVariant, rawPoints, rawEmbankmentHeights, curveMode, startControl, endControl, rawWaypointControls, pathId, railComponents, hasPlatform, platformSide, followTerrain]);

  /* ---- 都市の通行方向を取得(既定の進行方向割り当てに使う) ---- */
  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/city/`, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<{ traffic_hand?: number }>;
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        if (typeof data.traffic_hand === "number") {
          setTrafficHand(data.traffic_hand);
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[usePathConstruction] fetch city traffic_hand failed:", err);
        }
      });
    return () => controller.abort();
  }, [apiBaseUrl]);

  /* ---- 軌道複線の横オフセット列・進行方向列の変更をUnityへ逐次通知 ---- */
  // 線路数(trackCount)・進行方向の変更は配置中ノードを保つため Unity 敷設モードを再初期化しない。
  // そのため STATE_EDIT_PATH の初期値とは別に、railComponents のオフセット列・進行方向列が
  // 変わるたび change_lateral_offsets を送り、3Dプレビュー(平行本数・ホーム設置側・
  // 進行方向矢印)を追従させる。
  // 非軌道(railComponents=null)は空配列で単一プレビューへ戻す(同値はUnity側が無視する)。
  useEffect(() => {
    if (selectedVariant === null) return;
    const offsets = railComponents
      ? railComponents.map((c) => c.lateral_offset ?? 0)
      : [];
    const directions = railComponents
      ? railComponents.map((c) => c.travel_direction ?? DIR_BOTH)
      : [];
    sendEditPathChangeLateralOffsets(offsets, directions).catch((err) => {
      console.error("[usePathConstruction] sendEditPathChangeLateralOffsets failed:", err);
    });
  }, [railComponents, selectedVariant]);

  /* ---- ホーム設置側の変更をUnityへ逐次通知 ---- */
  // 駅バリアント選択中にホーム設置側トグルを切り替えたら、敷設中プレビューメッシュの
  // プラットフォーム張り出し側を即座に更新させる。駅以外(hasPlatform=false)や
  // 未選択のときは送らない(初期値は selectVariant の STATE_EDIT_PATH で渡す)。
  useEffect(() => {
    if (selectedVariant === null || !hasPlatform) return;
    sendSetPlatformSide(platformSide).catch((err) => {
      console.error("[usePathConstruction] sendSetPlatformSide failed:", err);
    });
  }, [platformSide, selectedVariant, hasPlatform]);

  /* ---- Tauriイベントリスナー (Unity → UI) ---- */

  useEffect(() => {
    const appWindow = getCurrentWindow();

    let unlistenSelectNode: (() => void) | undefined;
    let unlistenUpdateShape: (() => void) | undefined;

    // ノード選択(または次回配置値)の標高・盛土を受信し、ステッパー表示と編集対象を同期する。
    // node_id>=0 は選択中ノード、node_id=-1 は「次に配置するノード」の値。
    appWindow
      .listen<{
        path_id: number;
        node_id: number;
        position: number[];
        node_type: string;
        height?: number;
        embankment_height?: number;
      }>(IPC_EVENT_EDIT_PATH_SELECT_NODE, (event) => {
        const payload = event.payload;
        setSelectedNodeIndex(payload.node_id >= 0 ? payload.node_id : -1);
        if (typeof payload.height === "number") {
          setHeight(payload.height);
        }
        if (typeof payload.embankment_height === "number") {
          setEmbankmentHeight(payload.embankment_height);
        }
      })
      .then((fn) => { unlistenSelectNode = fn; })
      .catch(console.error);

    appWindow
      .listen<{
        unsent_paths_count: number;
        path_type: number;
        is_curved: boolean;
        follow_terrain?: number;
        selected_points?: number[][];
        start_control?: number[];
        end_control?: number[];
        embankment_heights?: number[];
        waypoint_controls?: Array<{
          control_in?: number[] | null;
          control_out?: number[] | null;
        }>;
      }>(IPC_EVENT_EDIT_PATH_UPDATE_SHAPE, (event) => {
        console.log("[usePathConstruction] edit_path.update_shape", event.payload);

        // 曲線モードはUnity側が真実源。点列の有無にかかわらず必ず同期する
        // (敷設モード初期化直後は空点列+is_curvedだけが届く)。
        const isCurved = event.payload.is_curved === true;
        setCurveMode(isCurved ? "curved" : "straight");

        // 地形追従の個別設定もUnity側が真実源。常送されるので毎回同期する。
        if (typeof event.payload.follow_terrain === "number") {
          setFollowTerrainState(event.payload.follow_terrain);
        }

        const points = event.payload.selected_points;
        if (points && points.length > 0) {
          setRawPoints(points);
          if (event.payload.embankment_heights && Array.isArray(event.payload.embankment_heights)) {
            setRawEmbankmentHeights(event.payload.embankment_heights);
          }
          // 中間アンカーの制御点(selected_points の両端を除く各点と同順)。
          // 未送信(2点のみ等)のときは空にリセットする。
          const wcs = event.payload.waypoint_controls;
          setRawWaypointControls(
            Array.isArray(wcs)
              ? wcs.map((w) => ({
                  control_in: w?.control_in ?? null,
                  control_out: w?.control_out ?? null,
                }))
              : []
          );
          const newNodes: BuildPoint[] = points.map((pt, i) => ({
            x: pt[0],
            z: pt[2] ?? pt[1],
            role: i === 0 ? "start" as const : i === points.length - 1 ? "end" as const : "waypoint" as const,
          }));
          setNodes(newNodes);

          // 制御点はstateのcurveMode(前回受信値=stale closureの危険がある)ではなく、
          // 今回のペイロードの is_curved で判定する。
          if (newNodes.length >= 2 && isCurved) {
            const sc = event.payload.start_control;
            const ec = event.payload.end_control;
            if (sc && sc.length >= 2) {
              setStartControl({ x: sc[0], z: sc[2] ?? sc[1] });
            } else {
              const controls = computeSplineOrBezierControls(newNodes, "curved");
              setStartControl(controls.start);
            }
            if (ec && ec.length >= 2) {
              setEndControl({ x: ec[0], z: ec[2] ?? ec[1] });
            } else {
              const controls = computeSplineOrBezierControls(newNodes, "curved");
              setEndControl(controls.end);
            }
          } else {
            setStartControl(null);
            setEndControl(null);
          }
        } else {
          // 空点列(敷設モード初期化・配置リセット直後)は制御点も空へ戻す
          setStartControl(null);
          setEndControl(null);
        }
      })
      .then((fn) => { unlistenUpdateShape = fn; })
      .catch(console.error);

    return () => {
      unlistenSelectNode?.();
      unlistenUpdateShape?.();
    };
  }, []);

  /* ---- 推定建設コストの取得 ---- */
  useEffect(() => {
    if (!apiBaseUrl || selectedVariant === null || rawPoints.length < 2) {
      setEstimatedCost(null);
      setEstimatedDurationDays(null);
      setCostPerSimulationDays(null);
      setCostSimulationDays(null);
      setNextEpochConstructionBudget(null);
      setIsCostLoading(false);
      return;
    }

    costAbortControllerRef.current?.abort();
    const controller = new AbortController();
    costAbortControllerRef.current = controller;
    setIsCostLoading(true);

    const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/construction-cost`;

    // 軌道複線は各線をオフセット済み中心線で見積もり、合算する(工期は最長を採用)。
    const requests: { path_variant: number; points: number[][] }[] = railComponents
      ? railComponents.map((comp) => ({
          path_variant: comp.path_variant,
          points: offsetPolyline3D(rawPoints, comp.lateral_offset ?? 0, comp.height_offset ?? 0),
        }))
      : [{ path_variant: selectedVariant, points: rawPoints }];

    if (requests.length === 0) {
      setIsCostLoading(false);
      return;
    }

    Promise.all(
      requests.map((req) =>
        fetch(url, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            path_variant: req.path_variant,
            start_point: req.points[0],
            end_point: req.points[req.points.length - 1],
            waypoints: req.points.slice(1, -1),
            curved: curveMode !== "straight",
          }),
          signal: controller.signal,
        }).then(async (res) => {
          if (!res.ok) throw new Error(`HTTP ${res.status}`);
          return res.json();
        })
      )
    )
      .then((results) => {
        if (controller.signal.aborted) return;
        const sum = (key: string) =>
          results.reduce((acc, r) => acc + (typeof r[key] === "number" ? r[key] : 0), 0);
        const maxOf = (key: string) =>
          results.reduce((acc, r) => Math.max(acc, typeof r[key] === "number" ? r[key] : 0), 0);
        setEstimatedCost(results.some((r) => r.estimated_construction_cost != null)
          ? sum("estimated_construction_cost")
          : null);
        setEstimatedStandardCost(results.some((r) => r.standard_construction_cost != null)
          ? sum("standard_construction_cost")
          : null);
        setEstimatedDurationDays(results.some((r) => r.estimated_duration_days != null)
          ? maxOf("estimated_duration_days")
          : null);
        setCostPerSimulationDays(results.some((r) => r.cost_per_simulation_days != null)
          ? sum("cost_per_simulation_days")
          : null);
        setCostSimulationDays(results[0]?.simulation_days ?? null);
        setNextEpochConstructionBudget(results[0]?.next_epoch_construction_budget ?? null);
      })
      .catch((err) => {
        if ((err as Error).name !== "AbortError") {
          console.error("[usePathConstruction] fetch construction cost failed:", err);
        }
        if (!controller.signal.aborted) {
          setEstimatedCost(null);
          setEstimatedStandardCost(null);
          setEstimatedDurationDays(null);
          setCostPerSimulationDays(null);
          setCostSimulationDays(null);
          setNextEpochConstructionBudget(null);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setIsCostLoading(false);
        }
      });

    return () => controller.abort();
  }, [apiBaseUrl, selectedVariant, rawPoints, curveMode, railComponents]);

  return {
    category,
    selectedType,
    selectedVariant,
    height,
    embankmentHeight,
    selectedNodeIndex,
    curveMode,
    nodes,
    rawPoints,
    startControl,
    endControl,
    waypointControls: rawWaypointControls,
    variantsForType,
    selectedVariantData,
    unityPathType,
    unityPathVariant,
    heightEditable,
    isInfrastructure,
    curveAllowed,
    hasPlatform,
    platformSide,
    setPlatformSide,
    followTerrain,
    setFollowTerrain,
    // 軌道(複線)設定
    railType,
    trackType,
    electrified,
    trackCount,
    trackSpacing,
    canWidenTrackSpacing,
    canShrinkTrackSpacing,
    adjustTrackSpacing,
    trackDirections,
    trafficHand,
    railComponents,
    railTrackTypeOptions,
    railElectrificationOptions,
    railVariantOptions,
    selectRailType,
    selectTrackType,
    setElectrified: setElectrifiedChecked,
    changeTrackCount,
    cycleTrackDirection,
    // コスト
    estimatedCost,
    estimatedStandardCost,
    estimatedDurationDays,
    costPerSimulationDays,
    costSimulationDays,
    nextEpochConstructionBudget,
    isCostLoading,
    // 選択・敷設
    onCategoryChange,
    onTypeChange,
    selectVariant,
    updateHeight,
    updateEmbankmentHeight,
    resetPath,
    continuePlacement,
    confirmPath,
  };
}
