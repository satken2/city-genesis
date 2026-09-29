/**
 * 公共交通 新設・経路編集パネル
 *
 * window_type="transit_creation" の左ドックパネル。建物建設パネル
 * (BuildingConstructionPanel) と同じデザイン・位置で、公共交通の路線を新設する。
 * editLineId を受け取ったときは既存路線の経路(巡回点)を編集するモードになる(路線詳細の「経路を編集」から開く)。
 *
 * 路線は停留所と経由点を巡回順に並べた閉じたループで、最後の停留所の次は先頭の停留所に戻る。
 * 往復運行は「行きと帰りの停留所を並べたループ」として作る。1本で上下線を持つ複線一体の線路では
 * 行きも帰りも同じ停留所に止まり(走る軌道は進む向きで決まる)、単線を並べた複線では帰りの停留所を
 * 反対向きのホームに置く。車両は自分が止まるホームの上でだけ、通行方向によらず向きを変えられる。
 *
 * 操作フロー(新設):
 *   1. 路線種別をプルダウンで選ぶ → 種別のアイコンと説明カードが表示される
 *      (選択肢・表示名・説明は transit_master =公共交通マスターから取得する)
 *   2. 種別を選ぶとグラフィック(Unity)が路線設置モード(STATE_EDIT_TRANSIT)に入り、
 *      3Dマップ上で停留所と経由点を置けるようになる。巡回点が変わるたびに
 *      "edit_transit:update_points" イベントで本パネルに送られてくる。
 *   3. 運賃・目標運行間隔・1編成の車両数と、車両の外観(描画専用。候補は TransitVehicleSelector)を設定する。
 *      停留所が2つ以上そろうと、巡回点や設定の変更のたびに収支見込み(TransitEstimateCard)を取り直す
 *   4. [新設する] で POST /api/v1/transit/lines を呼び、路線を新設する
 *      (API の path_variant には選択した種別の transit_path_id =区間パスのバリアントを、
 *       points には Unity から受け取った巡回点を渡す)
 * 経路編集では種別と運賃などの設定は変えず(設定は路線詳細の設定タブが担う)、現在の巡回点を Unity に渡して
 * 編集させ、[経路を更新する] で PUT /api/v1/transit/lines/{id}/route に新しい巡回点を送る。
 *
 * 停留所は路線種別の stop_path_variants に属するパス(駅・街路など)、経由点は走行網のパスの上にのみ
 * 設置できるため、Unity 側がその制約に沿って吸着する(制約はここで渡す stop/route_path_variants で伝える)。
 * LRT(車種が VEHICLE_TRAM の種別)はホームをパスとして敷かず、路線の作成後に Unity が停留所の
 * 下地パス上の線路の両外側(街路では車道内の安全地帯)へ自動で描くので、その旨を注記する。
 * 配置中の仮の停留所にはホームが出ないため、注記は「作成すると作られる」と書く。
 * 作成・更新に成功したら "transit:lines_changed" イベントを発火し、公共交通ウィンドウ
 * (transit_management)と路線詳細に再取得を促す。本パネルを開いている間は公共交通の表示をONにする。
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit, listen } from "@tauri-apps/api/event";
import type { TransitMasterEntry, TransitRoutePoint } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { resolveTransitIcon } from "../../utils/pathIcon";
import { useMasterData } from "../../contexts/MasterDataContext";
import { NumberStepper } from "../common/NumberStepper";
import {
  TransitVehicleSelector,
  defaultTransitVehicleModel,
  transitVehicleOptions,
} from "./TransitVehicleSelector";
import {
  TRANSIT_DEFAULT_FARE,
  TRANSIT_DEFAULT_HEADWAY_MIN,
  TRANSIT_DEFAULT_CARS_PER_TRAIN,
} from "../../local-constants";
import { STATUS_ACTIVE, VEHICLE_TRAM } from "../../constants";
import { useTransitEstimate } from "../../hooks/useTransitEstimate";
import { TransitEstimateCard } from "./TransitEstimateCard";
import {
  sendChangeState,
  sendViewApplyPreset,
  sendEditTransitFinish,
  VIEW_PRESET_TRANSIT,
  IPC_EVENT_EDIT_TRANSIT_UPDATE_POINTS,
} from "../../utils/ipc";
// 建物建設パネルと同一のデザイン(文字サイズ・カード・セレクト)を用いるため、
// そのスタイル定義(bc-*)を共有する。
import "../building/BuildingConstructionPanel.css";

/** edit_transit:update_points イベントのペイロード。巡回順の巡回点。 */
type TransitPointsPayload = {
  points: TransitRoutePoint[];
};

/** 経路編集の対象路線(GET /api/v1/transit/lines/{id} の必要な項目だけ) */
type EditedLine = {
  path_variant: number;
  fare_price: number;
  target_headway: number;
  cars_per_train: number;
  route_points: TransitRoutePoint[];
};

/** 路線の作成・経路更新の応答のうち、運休の警告に使う項目 */
type RouteSaveResponse = { status?: number; pause_reason?: string | null };

export function TransitCreationPanel({
  apiBaseUrl,
  onClose,
  editLineId,
}: {
  apiBaseUrl: string;
  onClose: () => void;
  /** 経路を編集する既存路線の line_agent_id。未指定なら路線の新設 */
  editLineId?: number | null;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const isEdit = editLineId != null;

  // 選択中の路線種別(transit_master のキー)。経路編集では路線の種別から決まるので使わない。
  const [selectedTransitId, setSelectedTransitId] = useState<number | null>(null);
  const [editedLine, setEditedLine] = useState<EditedLine | null>(null);
  const [fare, setFare] = useState<number>(TRANSIT_DEFAULT_FARE);
  const [headway, setHeadway] = useState<number>(TRANSIT_DEFAULT_HEADWAY_MIN);
  // 1編成の車両数(cars_per_train)。編成長 = cars_per_train × 1車両長(種別マスター)が
  // 停留所の乗る下地パス(駅のホームや、交差点で区切られた道路・軌道の区間)の長さを超えると運休になる。
  const [carsPerTrain, setCarsPerTrain] = useState<number>(TRANSIT_DEFAULT_CARS_PER_TRAIN);
  // 車両の外観(vehicle_model)。候補は種別ごとに違うため、種別を選び直すとその種別の先頭候補に戻す。
  const [vehicleModel, setVehicleModel] = useState<number>(0);
  // Unity(グラフィック)側で配置中の巡回点。
  const [points, setPoints] = useState<TransitRoutePoint[]>([]);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const stopCount = points.filter((p) => p.kind === "stop").length;
  const waypointCount = points.length - stopCount;

  /** 公共交通マスター(transit_master)の路線種別一覧。 */
  const transitEntries = useMemo(() => {
    if (!masterData) return [];
    return Object.entries(masterData.transit) as unknown as [string, TransitMasterEntry][];
  }, [masterData]);

  /** 選択中(経路編集では編集中の路線)の路線種別のマスターデータ。 */
  const selectedEntry: TransitMasterEntry | null = useMemo(() => {
    if (!masterData) return null;
    if (isEdit) {
      if (!editedLine) return null;
      return transitEntries.find(([, e]) => e.transit_path_id === editedLine.path_variant)?.[1] ?? null;
    }
    if (selectedTransitId === null) return null;
    return masterData.transit[selectedTransitId] ?? null;
  }, [isEdit, editedLine, selectedTransitId, masterData, transitEntries]);

  useEffect(() => {
    void setHintbarText(t("hint.transitCreation"));
    return () => {
      void setHintbarText("");
    };
  }, [t]);

  // 本パネルを開いている間は3Dビューを公共交通向けの表示(全路線+計画中の道路・建物)にする。
  // 種別を選ぶと STATE_EDIT_TRANSIT が同じプリセットを適用するが、種別選択前から
  // 既設の路線が見えている必要があるのでマウント時にも送る。
  // 元の表示へ戻すのはウィンドウを閉じたときの STATE_NORMAL が担う。
  useEffect(() => {
    void sendViewApplyPreset(VIEW_PRESET_TRANSIT, "transit_creation").catch(console.error);
  }, []);

  // 経路編集では、対象路線の種別・設定・現在の巡回点を取得する。
  useEffect(() => {
    if (!isEdit) return;
    const controller = new AbortController();
    fetchApi<EditedLine>(buildApiUrl(apiBaseUrl, `/api/v1/transit/lines/${editLineId}`), {
      signal: controller.signal,
    })
      .then((line) => {
        if (!controller.signal.aborted) setEditedLine(line);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) setError(err instanceof Error ? err.message : String(err));
      });
    return () => controller.abort();
  }, [apiBaseUrl, isEdit, editLineId]);

  // 種別が決まると、Unity を路線設置モード(STATE_EDIT_TRANSIT)にする。
  // 停留所の設置制約(stop_path_variants)と走行網(route_path_variants)を渡し、Unity 側が吸着対象の
  // パスを絞れるようにする。新設で種別を変えたら巡回点と車両の外観はリセットする。経路編集では
  // 現在の巡回点を渡して編集させ、編集中の路線の既存の路線色帯は隠させる(line_agent_id)。
  useEffect(() => {
    if (!selectedEntry) return;
    const initialPoints = isEdit && editedLine ? editedLine.route_points : [];
    setPoints(initialPoints);
    if (!isEdit) {
      setVehicleModel(defaultTransitVehicleModel(selectedEntry.transit_path_id));
      setCarsPerTrain((cars) => Math.max(selectedEntry.freight ? 2 : 1, cars));
    }
    void sendChangeState(
      "STATE_EDIT_TRANSIT",
      {
        path_variant: selectedEntry.transit_path_id,
        stop_path_variants: selectedEntry.stop_path_variants,
        route_path_variants: selectedEntry.route_path_variants,
        points: initialPoints.map((p) => ({ kind: p.kind, position: p.position })),
        line_agent_id: isEdit ? editLineId : null,
      },
      "transit_creation",
    );
  }, [selectedEntry, isEdit, editedLine, editLineId]);

  // パネルを閉じるとき(アンマウント時)は通常モードに戻し、路線設置ギズモを片付ける。
  useEffect(() => {
    return () => {
      void sendChangeState("STATE_NORMAL", {}, "transit_creation");
    };
  }, []);

  // Unity 側で配置中の巡回点を受信し、state に反映する。
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    listen<TransitPointsPayload>(IPC_EVENT_EDIT_TRANSIT_UPDATE_POINTS, (event) => {
      setPoints(event.payload?.points ?? []);
    })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
    };
  }, []);

  // 経路編集の試算は、路線の現在の設定(運賃・運行間隔・1編成の車両数)で行う。
  const estimateFare = isEdit ? editedLine?.fare_price ?? 0 : fare;
  const estimateHeadway = isEdit ? editedLine?.target_headway ?? TRANSIT_DEFAULT_HEADWAY_MIN : headway;
  const estimateCars = isEdit ? editedLine?.cars_per_train ?? 1 : carsPerTrain;

  // 停留所が2つそろった時点で下地経路(=1周の距離と時間)が決まるので、そこから必要車両数と採算を試算する。
  const { estimate, loading: estimateLoading, error: estimateError } = useTransitEstimate(
    apiBaseUrl,
    selectedEntry !== null && stopCount >= 2
      ? {
          kind: "planned",
          pathVariant: selectedEntry.transit_path_id,
          points,
          farePrice: estimateFare,
          targetHeadway: estimateHeadway,
          carsPerTrain: estimateCars,
          ...(isEdit ? { editedLineId: editLineId as number } : {}),
        }
      : null,
  );

  const handleConfirm = useCallback(async () => {
    if (selectedEntry === null) return;
    if (stopCount < 2) {
      setError(t("transit.needTwoStops"));
      return;
    }
    setIsSaving(true);
    setError(null);
    try {
      const saved = isEdit
        ? await fetchApi<RouteSaveResponse>(buildApiUrl(apiBaseUrl, `/api/v1/transit/lines/${editLineId}/route`), {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ points }),
          })
        : await fetchApi<RouteSaveResponse>(buildApiUrl(apiBaseUrl, "/api/v1/transit/lines"), {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              // 路線種別は、区間パスのバリアント(= transit_master の transit_path_id)で指定する。
              path_variant: selectedEntry.transit_path_id,
              fare_price: fare,
              target_headway: headway,
              cars_per_train: carsPerTrain,
              vehicle_model: vehicleModel,
              points,
            }),
          });
      await emit("transit:lines_changed", {});
      // Unity の路線設置モードを終了し、データをリロードして新しい経路を反映させる。
      await sendEditTransitFinish();
      // 作成・更新できても停留所間が下地でつながらない場合は運休になる。成功ではなく警告として知らせる。
      const paused = saved != null && saved.status != null && saved.status !== STATUS_ACTIVE;
      const message = paused
        ? t(isEdit ? "transit.routeUpdatedButSuspended" : "transit.createdButSuspended")
        : t(isEdit ? "transit.routeUpdateSuccess" : "transit.createSuccess");
      await showNotification({ message, durationMs: paused ? 5000 : 3000 }).catch(() => {});
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save line");
    } finally {
      setIsSaving(false);
    }
  }, [apiBaseUrl, isEdit, editLineId, selectedEntry, fare, headway, carsPerTrain, vehicleModel, points, stopCount, onClose, t]);

  if (!masterData) {
    return <p className="bc-muted">{t("common.loading")}</p>;
  }

  // マスターの description が未設定("nan"や空)の場合はフォールバック表示にする。
  const description =
    selectedEntry && selectedEntry.description && selectedEntry.description !== "nan"
      ? selectedEntry.description
      : "—";

  return (
    <div className="bc-layout">
      {/* 路線種別の選択(プルダウン)。経路編集では種別を変えない */}
      {!isEdit && (
        <div className="bc-section">
          {transitEntries.length === 0 ? (
            <div className="bc-empty-variants">{t("transit.noVariants")}</div>
          ) : (
            <select
              className="bc-select"
              data-guide="transit.type"
              value={selectedTransitId ?? ""}
              onChange={(e) => setSelectedTransitId(e.target.value ? parseInt(e.target.value) : null)}
            >
              <option value="">{t("transit.selectMode")}</option>
              {transitEntries.map(([idStr, entry]) => (
                <option key={idStr} value={idStr}>
                  {entry.display_name}
                </option>
              ))}
            </select>
          )}
        </div>
      )}

      {/* 種別のアイコンと説明カード(アイコンは区間パスのバリアントから解決する) */}
      {selectedEntry && (
        <div className="bc-section">
          <div className="bc-description-frame">
            <img
              src={resolveTransitIcon(selectedEntry.transit_path_id)}
              alt={selectedEntry.display_name}
              className="bc-description-icon"
              draggable={false}
            />
            <div className="bc-description-body">
              <div className="bc-description-name">{selectedEntry.display_name}</div>
              <div className="bc-description-text">{description}</div>
            </div>
          </div>
        </div>
      )}

      {/* 車両の外観(車両を描かない種別では出さない。経路編集では路線詳細の設定タブで変える) */}
      {!isEdit && selectedEntry && transitVehicleOptions(selectedEntry.transit_path_id).length > 0 && (
        <div className="bc-section">
          <div className="bc-section-title">{t("transit.vehicleModel")}</div>
          <TransitVehicleSelector
            pathVariant={selectedEntry.transit_path_id}
            value={vehicleModel}
            onChange={setVehicleModel}
            disabled={isSaving}
          />
          <div className="bc-description-text" style={{ marginTop: 6, opacity: 0.8 }}>
            {t("transit.vehicleModelNote")}
          </div>
        </div>
      )}

      {/* 運賃・運行間隔(新設だけ)と、配置中の停留所・経由点の数 */}
      {selectedEntry && (
        <div className="bc-section">
          {!isEdit && (
            <>
              <div className="bc-split-row">
                <div className="bc-split-col">
                  <div className="bc-section-title">{t("transit.fare")}</div>
                  <div className="bc-stepper-row">
                    <NumberStepper value={fare} onChange={setFare} min={0} max={100000} step={10} integerOnly={true} />
                    <span className="bc-stepper-unit">{t("transit.currencyUnit")}</span>
                  </div>
                </div>
                <div className="bc-split-col">
                  <div className="bc-section-title">{t("transit.headway")}</div>
                  <div className="bc-stepper-row">
                    <NumberStepper value={headway} onChange={setHeadway} min={1} max={120} step={1} integerOnly={true} />
                    <span className="bc-stepper-unit">{t("transit.minutesUnit")}</span>
                  </div>
                </div>
              </div>
              <div className="bc-split-row" style={{ marginTop: 8 }}>
                <div className="bc-split-col">
                  <div className="bc-section-title">{t("transit.carsPerTrain")}</div>
                  <div className="bc-stepper-row">
                    <NumberStepper value={carsPerTrain} onChange={setCarsPerTrain} min={selectedEntry.freight ? 2 : 1} max={20} step={1} integerOnly={true} />
                    <span className="bc-stepper-unit">{t("transit.carsUnit")}</span>
                  </div>
                  {selectedEntry.freight && <p className="bc-description-text">{t("transit.freightConsistNote")}</p>}
                </div>
              </div>
            </>
          )}
          <div className="bc-split-row" style={{ marginTop: isEdit ? 0 : 8 }}>
            <div className="bc-split-col">
              <div className="bc-section-title">{t("transit.stopCountLabel")}</div>
              <div className="bc-stepper-row">
                <span className="bc-description-name">{stopCount}</span>
                <span className="bc-stepper-unit">{t("transit.stopsUnit")}</span>
              </div>
            </div>
            <div className="bc-split-col">
              <div className="bc-section-title">{t("transit.waypointCountLabel")}</div>
              <div className="bc-stepper-row">
                <span className="bc-description-name">{waypointCount}</span>
                <span className="bc-stepper-unit">{t("transit.stopsUnit")}</span>
              </div>
            </div>
          </div>
          {!isEdit && selectedEntry.vehicle_type === VEHICLE_TRAM && (
            <div className="bc-description-text" style={{ marginTop: 8, opacity: 0.8 }}>
              {t("transit.autoPlatformNote")}
            </div>
          )}
        </div>
      )}

      {/* 収支見込み(停留所が2つそろうまでは案内だけ) */}
      {selectedEntry && (
        <div className="bc-section">
          <div className="bc-section-title">{t("transit.estimate.title")}</div>
          <TransitEstimateCard
            estimate={estimate}
            loading={estimateLoading}
            error={estimateError}
            placeholder={t("transit.estimate.placeholder")}
          />
        </div>
      )}

      {error && (
        <div className="bc-section">
          <div className="bc-description-text" style={{ color: "#fca5a5" }}>{error}</div>
        </div>
      )}

      {/* アクションボタン(パネル下部に固定) */}
      {selectedEntry && (
        <div className="bc-section bc-action-bar">
          <div className="bc-actions">
            <button className="bc-btn" type="button" onClick={onClose}>
              {t("transit.cancel")}
            </button>
            <button
              className="bc-btn bc-btn-confirm"
              type="button"
              disabled={isSaving || stopCount < 2}
              onClick={() => void handleConfirm()}
            >
              {isSaving ? t("common.processing") : t(isEdit ? "transit.confirmRouteUpdate" : "transit.confirmCreate")}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
