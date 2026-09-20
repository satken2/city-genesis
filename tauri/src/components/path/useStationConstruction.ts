/**
 * 駅パッケージ(プラットフォーム複線 + 駅舎)の一括配置フック
 *
 * 軌道建設パネルの簡易モード「駅タブ」専用。詳細モードのように折れ線を引くのではなく、
 * Unity(StationPlanManager)のゴーストを地形クリックで固定し、そこへ次の3本を一度に発注する。
 *
 *   - プラットフォーム2本: 中心線の左右へ線路間隔の半分ずつ振った平行な駅パス。
 *     ホーム設置側は外側固定なので、必ず相対式(対面ホーム)になる。
 *   - 駅舎1本: 中心線と直交してプラットフォーム群を跨ぐ直線。両端に建屋が建つ。
 *
 * 形状(中心線)の真実源はUnityで、`edit_station:placement_state` で `[始点, 終点]` の
 * `[x, 相対高さ, z]` として届く。UI側で幾何計算を二重に持たないため、受け取った値を
 * そのまま確定送信のパスへ載せる。逆にホーム長・高さ・角度・規格の変更は
 * `edit_station:placement_command` でUnityへ送り、ゴーストへ反映させる。
 *
 * 検証・コスト見積り・確定送信は詳細モードと同じエンドポイント(`/paths/validate`,
 * `/paths/construction-cost`, `POST /paths`)を使う。プラットフォームの共通標高は
 * `align_station_tracks` でバックエンドに任せる。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";

import type { MasterData, PathMasterEntry, PathAlignmentSummary } from "../../types";
import { fetchApi } from "../../utils/api";
import {
  sendChangeState,
  sendStationPlacementCommand,
  sendHighlightObjects,
  sendClearHighlights,
  IPC_EVENT_EDIT_STATION_PLACEMENT_STATE,
  type StationPlacementState,
} from "../../utils/ipc";
import { describePathValidation, type PathValidationResult } from "./pathValidation";
import type { PathAlignmentResponse } from "./usePathConstruction";
import { PLATFORM_SIDE_OUTSIDE, TRAFFIC_HAND_LEFT } from "../../constants";
import {
  STATION_DEFAULT_CAR_COUNT,
  STATION_MAX_CAR_COUNT,
  STATION_MIN_CAR_COUNT,
  stationPlatformLength,
} from "../../local-constants";
import { FALLBACK_TRACK_SPACING, defaultTravelDirections } from "./railBuild";

/** POST /paths/construction-cost のうち、費用カードが使うフィールド。 */
type PathCostResponse = {
  estimated_construction_cost?: number | null;
  standard_construction_cost?: number | null;
  estimated_duration_days?: number | null;
  next_epoch_construction_budget?: number | null;
  available_funds?: number | null;
  construction_supply_ratio?: number | null;
  expected_duration_days?: number | null;
  alignment?: PathAlignmentResponse;
};

/** 複線の各線の線形指標を最悪値で1つにまとめる(usePathConstruction と同じ規則)。 */
function summarizeAlignment(items: Array<PathAlignmentResponse | undefined>): PathAlignmentSummary | null {
  const valid = items.filter((a): a is PathAlignmentResponse => a != null);
  if (valid.length === 0) return null;
  const radii = valid.map((a) => a.min_curve_radius).filter((r): r is number => r != null);
  return {
    baseSpeed: Math.min(...valid.map((a) => a.base_speed)),
    maxSpeed: valid[0].max_speed,
    minCurveRadius: radii.length > 0 ? Math.min(...radii) : null,
    maxGrade: Math.max(...valid.map((a) => a.max_grade)),
    curveRadiusLimit: valid[0].curve_radius_limit,
    gradeLimit: valid[0].grade_limit,
    curveViolated: valid.some((a) => a.curve_violations.length > 0),
    gradeViolated: valid.some((a) => a.grade_violations.length > 0),
  };
}

export function useStationConstruction(
  masterData: MasterData | null,
  apiBaseUrl: string | undefined,
  /** 駅タブが表示されている間だけ Unity を駅配置モードにする。 */
  enabled: boolean,
) {
  const { t } = useTranslation();

  const [platformVariant, setPlatformVariantState] = useState<number | null>(null);
  const [houseEnds, setHouseEnds] = useState("both");
  const [passageMode, setPassageMode] = useState("elevated");
  const [houseVariant, setHouseVariantState] = useState<number | null>(null);
  const [carCount, setCarCountState] = useState(STATION_DEFAULT_CAR_COUNT);
  const [height, setHeightState] = useState(0);
  const [angle, setAngleState] = useState(0);
  const [trafficHand, setTrafficHand] = useState(TRAFFIC_HAND_LEFT);
  /** Unityから届いた最新の配置状態。position が null の間はカーソル追従中。 */
  const [placement, setPlacement] = useState<StationPlacementState | null>(null);

  const platformData = useMemo(
    (): PathMasterEntry | null =>
      platformVariant != null ? masterData?.paths?.[platformVariant] ?? null : null,
    [masterData, platformVariant],
  );
  const houseData = useMemo(
    (): PathMasterEntry | null =>
      houseVariant != null ? masterData?.paths?.[houseVariant] ?? null : null,
    [masterData, houseVariant],
  );

  /** ホーム長(m)。両数からの換算で、Unityのゴーストとバックエンドの発注で同じ値を使う。 */
  const platformLength = stationPlatformLength(carCount);
  /**
   * 線路間隔(m)。相対式ホームの複線なのでバリアントのマスター幅(=コリドーが隙間なく隣接)に固定する。
   * 詳細モードのように広げ縮めはしない(簡易モードの「固定」の一部)。
   */
  const trackSpacing =
    typeof platformData?.width === "number" && platformData.width > 0
      ? platformData.width
      : FALLBACK_TRACK_SPACING;
  const heightEditable = platformData?.height_editable ?? false;
  const minHeight = platformData?.min_height ?? 0;

  const travelDirections = useMemo(
    () => defaultTravelDirections(2, trafficHand),
    [trafficHand],
  );

  /* ---- 都市の通行方向(既定の進行方向割り当てに使う) ---- */
  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    fetchApi<{ traffic_hand?: number }>(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/city/`, {
      signal: controller.signal,
    })
      .then((data) => {
        if (!controller.signal.aborted && typeof data.traffic_hand === "number") {
          setTrafficHand(data.traffic_hand);
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[useStationConstruction] fetch city traffic_hand failed:", err);
        }
      });
    return () => controller.abort();
  }, [apiBaseUrl]);

  /* ---- Unity からの配置状態受信 ---- */
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlisten: (() => void) | undefined;
    appWindow
      .listen<StationPlacementState>(IPC_EVENT_EDIT_STATION_PLACEMENT_STATE, (event) => {
        setPlacement(event.payload);
        // Unityの3D操作(中ボタン回転・PageUp/PageDown)で変わった値をステッパーへ書き戻す。
        // パネルからUnityへエコーし返さないための一方向同期(プロップ配置と同じ扱い)。
        if (typeof event.payload.angle === "number") setAngleState(event.payload.angle);
        if (typeof event.payload.height === "number") setHeightState(event.payload.height);
      })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
      void sendClearHighlights("path_build");
    };
  }, []);

  /**
   * Unity を駅配置モード(STATE_EDIT_STATION)にする。現在の設定をまとめて初期値として渡す。
   * 敷設セッションの開始と、確定後に連続設置へ戻すときの両方で使う。
   */
  const enterPlacementMode = useCallback(() => {
    if (platformVariant == null || houseVariant == null) return;
    sendChangeState(
      "STATE_EDIT_STATION",
      {
        platform_variant: platformVariant,
        house_variant: houseVariant,
        platform_length: platformLength,
        track_spacing: trackSpacing,
        height,
        angle,
        travel_directions: travelDirections,
        house_ends: houseEnds,
        passage_mode: passageMode,
      },
      "track_build",
    ).catch((err) => console.error("[useStationConstruction] sendChangeState failed:", err));
  }, [platformVariant, houseVariant, platformLength, trackSpacing, height, angle, travelDirections, houseEnds, passageMode]);

  /* ---- 駅タブの表示中だけ Unity を駅配置モードにする ---- */
  // 寸法・角度・高さの変更は placement_command で逐次送るため、モードに入り直すのは
  // 「タブに入った」「規格が変わった」ときだけにする(配置中の地点を保つため)。
  useEffect(() => {
    if (!enabled) return;
    enterPlacementMode();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, platformVariant, houseVariant]);

  /* ---- 設定変更の逐次送信(ゴーストへの即時反映) ---- */
  const commandKey = JSON.stringify({ platformLength, trackSpacing, height, angle, travelDirections, houseEnds, passageMode });
  useEffect(() => {
    if (!enabled || platformVariant == null) return;
    sendStationPlacementCommand({
      platform_length: platformLength,
      track_spacing: trackSpacing,
      height,
      angle,
      travel_directions: travelDirections,
      house_ends: houseEnds,
      passage_mode: passageMode,
    }).catch((err) => console.error("[useStationConstruction] sendStationPlacementCommand failed:", err));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, platformVariant, commandKey]);

  /* ---- 確定送信の本体(プラットフォーム2本 + 駅舎1本) ---- */
  const constructionBody = useMemo(() => {
    if (platformVariant == null || houseVariant == null) return null;
    if (!placement?.position || !placement.platforms || !placement.house) return null;
    const paths: Record<string, unknown>[] = placement.platforms.map((line, index) => ({
      path_variant: platformVariant,
      start_point: line[0],
      end_point: line[1],
      curved: false,
      travel_direction: travelDirections[index],
      // 相対式(対面ホーム)固定。複線の両線とも外側へホームを張り出させる。
      platform_side: PLATFORM_SIDE_OUTSIDE,
      path_temp_id: `station_track_${index}`,
    }));
    paths.push({
      path_variant: houseVariant,
      house_ends: houseEnds,
        passage_mode: passageMode,
      start_point: placement.house[0],
      end_point: placement.house[1],
      curved: false,
      path_temp_id: "station_house",
    });
    // プラットフォームの共通標高の解決はバックエンドに任せる(詳細モードの複線と同じ)。
    return { paths, align_station_tracks: true };
  }, [platformVariant, houseVariant, placement, travelDirections, houseEnds, passageMode]);

  /* ---- 事前検証(干渉・川横断・線形) ---- */
  const [validation, setValidation] = useState<{
    key: string; result?: PathValidationResult; error?: string;
  } | null>(null);
  const [validationAttempt, setValidationAttempt] = useState(0);
  const validationKey = JSON.stringify({ ...constructionBody, exclude_path_id: null });
  const currentValidationKey = useRef(validationKey);
  currentValidationKey.current = validationKey;

  const retryValidation = useCallback(() => {
    setValidation(null);
    setValidationAttempt((value) => value + 1);
  }, []);

  useEffect(() => {
    void sendClearHighlights("path_build");
    if (!apiBaseUrl || !constructionBody) return;
    let cancelled = false;
    const timer = setTimeout(async () => {
      try {
        const result = await fetchApi<PathValidationResult>(
          `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/validate`,
          { method: "POST", headers: { "Content-Type": "application/json" }, body: validationKey },
        );
        if (!cancelled && currentValidationKey.current === validationKey) {
          setValidation({ key: validationKey, result });
          void sendHighlightObjects(result, "path_build").catch(console.error);
        }
      } catch (error) {
        if (!cancelled && currentValidationKey.current === validationKey) {
          setValidation({ key: validationKey, error: error instanceof Error ? error.message : String(error) });
        }
      }
    }, 150);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [apiBaseUrl, constructionBody, validationKey, validationAttempt]);

  const canConfirm = constructionBody !== null
    && validation?.key === validationKey && validation.result?.is_valid === true;
  const validationMessages = constructionBody === null
    ? [t("track_construction.station.pickPointFirst")]
    : !apiBaseUrl
      ? [t("path_construction.validationUnavailable")]
      : validation?.key !== validationKey
        ? [t("path_construction.collisionChecking")]
        : validation.error !== undefined
          ? [t("path_construction.validationFailed", { reason: validation.error })]
          : validation.result ? describePathValidation(validation.result, t) : [];
  const canRetryValidation = constructionBody !== null && !!apiBaseUrl
    && validation?.key === validationKey && validation.error !== undefined;

  /* ---- 建設費用の見積り ---- */
  const [estimatedCost, setEstimatedCost] = useState<number | null>(null);
  const [estimatedStandardCost, setEstimatedStandardCost] = useState<number | null>(null);
  const [estimatedDurationDays, setEstimatedDurationDays] = useState<number | null>(null);
  const [nextEpochConstructionBudget, setNextEpochConstructionBudget] = useState<number | null>(null);
  const [availableFunds, setAvailableFunds] = useState<number | null>(null);
  const [supplyRatio, setSupplyRatio] = useState<number | null>(null);
  const [expectedDurationDays, setExpectedDurationDays] = useState<number | null>(null);
  const [alignment, setAlignment] = useState<PathAlignmentSummary | null>(null);
  const [isCostLoading, setIsCostLoading] = useState(false);

  const clearCost = useCallback(() => {
    setEstimatedCost(null);
    setEstimatedStandardCost(null);
    setEstimatedDurationDays(null);
    setNextEpochConstructionBudget(null);
    setAvailableFunds(null);
    setSupplyRatio(null);
    setExpectedDurationDays(null);
    setAlignment(null);
    setIsCostLoading(false);
  }, []);

  useEffect(() => {
    if (!apiBaseUrl || !constructionBody) {
      clearCost();
      return;
    }
    const controller = new AbortController();
    setIsCostLoading(true);
    const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/construction-cost`;
    Promise.all(
      constructionBody.paths.map((req) =>
        fetchApi<PathCostResponse>(url, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(req),
          signal: controller.signal,
        }),
      ),
    )
      .then((results) => {
        if (controller.signal.aborted) return;
        // 費用は3本の合計、工期は最長の1本に合わせる(詳細モードの複線と同じ集約)。
        const sum = (key: keyof PathCostResponse) =>
          results.reduce((acc, r) => acc + (typeof r[key] === "number" ? (r[key] as number) : 0), 0);
        const maxOf = (key: keyof PathCostResponse) =>
          results.reduce((acc, r) => Math.max(acc, typeof r[key] === "number" ? (r[key] as number) : 0), 0);
        setEstimatedCost(results.some((r) => r.estimated_construction_cost != null) ? sum("estimated_construction_cost") : null);
        setEstimatedStandardCost(results.some((r) => r.standard_construction_cost != null) ? sum("standard_construction_cost") : null);
        setEstimatedDurationDays(results.some((r) => r.estimated_duration_days != null) ? maxOf("estimated_duration_days") : null);
        setNextEpochConstructionBudget(results[0]?.next_epoch_construction_budget ?? null);
        setAvailableFunds(results[0]?.available_funds ?? null);
        setSupplyRatio(
          results.some((r) => r.construction_supply_ratio != null)
            ? results.reduce((acc: number, r) => Math.min(acc, typeof r.construction_supply_ratio === "number" ? r.construction_supply_ratio : 1), 1)
            : null,
        );
        setExpectedDurationDays(results.some((r) => r.expected_duration_days != null) ? maxOf("expected_duration_days") : null);
        setAlignment(summarizeAlignment(results.map((r) => r.alignment)));
      })
      .catch((err) => {
        if ((err as Error).name !== "AbortError") {
          console.error("[useStationConstruction] fetch construction cost failed:", err);
        }
        if (!controller.signal.aborted) clearCost();
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsCostLoading(false);
      });
    return () => controller.abort();
  }, [apiBaseUrl, constructionBody, clearCost]);

  /**
   * 全線検証で得た線形指標を優先する(個別の construction-cost は駅の共通標高を含まないため)。
   * 駅舎は線形評価の対象外で alignment を返さないので、集約は自然にプラットフォーム側だけになる。
   */
  const validatedAlignment = validation?.key === validationKey && validation.result?.alignments
    ? summarizeAlignment(validation.result.alignments) : null;
  const displayAlignment = validatedAlignment ?? alignment;

  /* ---- 設定の変更ハンドラ(Unityへは commandKey の effect がまとめて送る) ---- */
  const setCarCount = useCallback((value: number) => {
    setCarCountState(Math.max(STATION_MIN_CAR_COUNT, Math.min(STATION_MAX_CAR_COUNT, Math.round(value))));
  }, []);
  const setHeight = useCallback((value: number) => {
    setHeightState(heightEditable ? Math.max(minHeight, value) : 0);
  }, [heightEditable, minHeight]);
  const setAngle = useCallback((value: number) => {
    setAngleState(((value % 360) + 360) % 360);
  }, []);
  const selectPlatformVariant = useCallback((variantId: number) => setPlatformVariantState(variantId), []);
  const selectHouseVariant = useCallback((variantId: number) => setHouseVariantState(variantId), []);

  /** 配置地点の固定だけを解除して置き直す(パネルのキャンセルボタン)。 */
  const resetPlacement = useCallback(() => {
    setPlacement(null);
    setValidation(null);
    clearCost();
    sendStationPlacementCommand({ reset: true }).catch((err) =>
      console.error("[useStationConstruction] reset failed:", err));
  }, [clearCost]);

  /**
   * 連続設置: 発注後の後始末(edit_path:finish)で Unity 側の配置モードが閉じるため、
   * 設定を保ったまま駅配置モードへ入り直す(詳細モードの continuePlacement と同じ役割)。
   */
  const continuePlacement = useCallback(() => {
    setPlacement(null);
    setValidation(null);
    clearCost();
    enterPlacementMode();
  }, [clearCost, enterPlacementMode]);

  /** プラットフォーム2本と駅舎1本を1回のPOSTで発注する。 */
  const confirmStation = useCallback(async (): Promise<void> => {
    if (!apiBaseUrl || !constructionBody || !canConfirm || currentValidationKey.current !== validationKey) {
      throw new Error(validationMessages.join("\n") || t("path_construction.collisionChecking"));
    }
    // 送信直前の再検証。他の編集による都市側の変化を拾う(詳細モードと同じ手順)。
    let checked: PathValidationResult;
    try {
      checked = await fetchApi<PathValidationResult>(
        `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/validate`,
        { method: "POST", headers: { "Content-Type": "application/json" }, body: validationKey },
      );
    } catch (error) {
      const reason = error instanceof Error ? error.message : String(error);
      if (currentValidationKey.current === validationKey) setValidation({ key: validationKey, error: reason });
      throw new Error(t("path_construction.validationFailed", { reason }));
    }
    if (currentValidationKey.current !== validationKey) {
      throw new Error(t("path_construction.collisionChecking"));
    }
    setValidation({ key: validationKey, result: checked });
    void sendHighlightObjects(checked, "path_build").catch(console.error);
    if (!checked.is_valid) {
      throw new Error(describePathValidation(checked, t).join("\n"));
    }

    const response = await fetchApi<{ paths: Array<{ path_id: number }> }>(
      `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/`,
      { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(constructionBody) },
    );
    void sendClearHighlights("path_build");
    // 駅=駅舎パスそのものなので、作成した駅舎(最後の要素)の詳細を開いて駅名を確認できるようにする。
    const housePathId = response?.paths?.[response.paths.length - 1]?.path_id;
    if (housePathId != null) {
      void invoke("open_registered_window", { windowType: "path_detail", entityId: String(housePathId) })
        .catch(console.error);
    }
  }, [apiBaseUrl, constructionBody, canConfirm, validationKey, validationMessages, t]);

  return {
    houseEnds, setHouseEnds, passageMode, setPassageMode,
    platformVariant,
    platformData,
    houseVariant,
    houseData,
    selectPlatformVariant,
    selectHouseVariant,
    carCount,
    setCarCount,
    platformLength,
    trackSpacing,
    height,
    setHeight,
    heightEditable,
    minHeight,
    angle,
    setAngle,
    travelDirections,
    /** 配置地点が固定済みか(未固定の間は確定できない)。 */
    hasPlacement: !!placement?.position,
    // コスト
    estimatedCost,
    estimatedStandardCost,
    estimatedDurationDays,
    nextEpochConstructionBudget,
    availableFunds,
    supplyRatio,
    expectedDurationDays,
    isCostLoading,
    alignment: displayAlignment,
    alignmentOk: displayAlignment == null || (!displayAlignment.curveViolated && !displayAlignment.gradeViolated),
    // 確定
    canConfirm,
    validationMessages,
    canRetryValidation,
    retryValidation,
    resetPlacement,
    continuePlacement,
    confirmStation,
  };
}
