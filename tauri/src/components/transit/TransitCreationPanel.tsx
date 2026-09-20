/**
 * 公共交通 新設パネル
 *
 * window_type="transit_creation" の左ドックパネル。建物建設パネル
 * (BuildingConstructionPanel) と同じデザイン・位置で、公共交通の路線を新設する。
 *
 * 操作フロー:
 *   1. 路線種別をプルダウンで選ぶ → 種別のアイコンと説明カードが表示される
 *      (選択肢・表示名・説明は transit_master =公共交通マスターから取得する)
 *   2. 種別を選ぶとグラフィック(Unity)が停留所配置モード(STATE_EDIT_TRANSIT)に入り、
 *      3Dマップ上で停留所を設置できるようになる。設置した停留所座標は
 *      "edit_transit:update_stops" イベントで本パネルに送られてくる。
 *   3. 運賃・目標運行間隔を設定する
 *   4. [新設する] で POST /api/v1/transit/lines を呼び、路線を新設する
 *      (API の path_variant には選択した種別の transit_path_id =区間パスのバリアントを、
 *       stops には Unity から受け取った停留所座標列を渡す)
 *
 * 停留所は路線種別の stop_path_variants に属するパス(駅・街路など)の上にのみ設置できるため、
 * Unity 側がその制約に沿って吸着する(制約はここで渡す stop_path_variants で伝える)。
 * 作成に成功したら "transit:lines_changed" イベントを発火し、公共交通ウィンドウ
 * (transit_management) に一覧の再取得を促す。本パネルを開いている間は公共交通の表示をONにする。
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit, listen } from "@tauri-apps/api/event";
import type { TransitMasterEntry } from "../../types";
import { buildApiUrl } from "../../utils/api";
import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { resolveTransitIcon } from "../../utils/pathIcon";
import { useMasterData } from "../../contexts/MasterDataContext";
import { NumberStepper } from "../common/NumberStepper";
import {
  TRANSIT_DEFAULT_FARE,
  TRANSIT_DEFAULT_HEADWAY_MIN,
  TRANSIT_DEFAULT_CARS_PER_TRAIN,
} from "../../local-constants";
import { STATUS_ACTIVE } from "../../constants";
import {
  sendChangeState,
  sendViewApplyPreset,
  sendEditTransitFinish,
  VIEW_PRESET_TRANSIT,
  IPC_EVENT_EDIT_TRANSIT_UPDATE_STOPS,
} from "../../utils/ipc";
// 建物建設パネルと同一のデザイン(文字サイズ・カード・セレクト)を用いるため、
// そのスタイル定義(bc-*)を共有する。
import "../building/BuildingConstructionPanel.css";

/** edit_transit:update_stops イベントのペイロード。停留所座標列。 */
type TransitStopsPayload = {
  stops: number[][];
};

export function TransitCreationPanel({
  apiBaseUrl,
  onClose,
}: {
  apiBaseUrl: string;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  // 選択中の路線種別(transit_master のキー)。
  const [selectedTransitId, setSelectedTransitId] = useState<number | null>(null);
  const [fare, setFare] = useState<number>(TRANSIT_DEFAULT_FARE);
  const [headway, setHeadway] = useState<number>(TRANSIT_DEFAULT_HEADWAY_MIN);
  // 1編成の車両数(cars_per_train)。編成長 = cars_per_train × 1車両長(種別マスター)が
  // 停留所のプラットフォーム長を超えると運休になる。
  const [carsPerTrain, setCarsPerTrain] = useState<number>(TRANSIT_DEFAULT_CARS_PER_TRAIN);
  // Unity(グラフィック)側で配置中の停留所座標列([x, y, z] の配列)。
  const [stops, setStops] = useState<number[][]>([]);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  /** 公共交通マスター(transit_master)の路線種別一覧。 */
  const transitEntries = useMemo(() => {
    if (!masterData) return [];
    return Object.entries(masterData.transit) as unknown as [string, TransitMasterEntry][];
  }, [masterData]);

  /** 選択中の路線種別のマスターデータ。 */
  const selectedEntry: TransitMasterEntry | null = useMemo(() => {
    if (selectedTransitId === null || !masterData) return null;
    return masterData.transit[selectedTransitId] ?? null;
  }, [selectedTransitId, masterData]);

  useEffect(() => {
    void setHintbarText(t("transit.placeStopHint"));
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

  // 種別を選ぶと、Unity を停留所配置モード(STATE_EDIT_TRANSIT)にする。
  // 停留所の設置制約(stop_path_variants)と走行網(route_path_variants)を渡し、
  // Unity 側が吸着対象のパスを絞れるようにする。種別を変えたら停留所はリセットする。
  useEffect(() => {
    if (!selectedEntry) return;
    setStops([]);
    void sendChangeState(
      "STATE_EDIT_TRANSIT",
      {
        path_variant: selectedEntry.transit_path_id,
        stop_path_variants: selectedEntry.stop_path_variants,
        route_path_variants: selectedEntry.route_path_variants,
      },
      "transit_creation",
    );
  }, [selectedEntry]);

  // パネルを閉じるとき(アンマウント時)は通常モードに戻し、停留所配置ギズモを片付ける。
  useEffect(() => {
    return () => {
      void sendChangeState("STATE_NORMAL", {}, "transit_creation");
    };
  }, []);

  // Unity 側で配置中の停留所座標列を受信し、state に反映する。
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    listen<TransitStopsPayload>(IPC_EVENT_EDIT_TRANSIT_UPDATE_STOPS, (event) => {
      setStops(event.payload?.stops ?? []);
    })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
    };
  }, []);

  const handleConfirm = useCallback(async () => {
    if (selectedEntry === null) return;
    if (stops.length < 2) {
      setError(t("transit.needTwoStops"));
      return;
    }
    setIsSaving(true);
    setError(null);
    try {
      const res = await fetch(buildApiUrl(apiBaseUrl, "/api/v1/transit/lines"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          // 路線種別は、区間パスのバリアント(= transit_master の transit_path_id)で指定する。
          path_variant: selectedEntry.transit_path_id,
          fare_price: fare,
          target_headway: headway,
          cars_per_train: carsPerTrain,
          // Unity 側で配置した停留所座標列。
          stops,
        }),
      });
      if (!res.ok) {
        const body = await res.json().catch(() => null);
        throw new Error(body?.message ?? `HTTP ${res.status}`);
      }
      const created = (await res.json().catch(() => null)) as
        | { status?: number; pause_reason?: string | null }
        | null;
      await emit("transit:lines_changed", {});
      // Unity の停留所配置モードを終了し、データをリロードして新設路線を反映させる。
      await sendEditTransitFinish();
      // 作成できても下地の通行方向で往復できない場合は運休で作られる。成功ではなく警告として知らせる。
      const createdPaused =
        created != null && created.status != null && created.status !== STATUS_ACTIVE;
      const message = createdPaused
        ? created?.pause_reason === "return_leg_blocked"
          ? t("transit.createdButSuspendedReturnBlocked")
          : t("transit.createdButSuspended")
        : t("transit.createSuccess");
      await showNotification({ message, durationMs: createdPaused ? 5000 : 3000 }).catch(() => {});
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to create line");
    } finally {
      setIsSaving(false);
    }
  }, [apiBaseUrl, selectedEntry, fare, headway, stops, onClose, t]);

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
      {/* 路線種別の選択(プルダウン) */}
      <div className="bc-section">
        {transitEntries.length === 0 ? (
          <div className="bc-empty-variants">{t("transit.noVariants")}</div>
        ) : (
          <select
            className="bc-select"
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

      {/* 選択した種別のアイコンと説明カード(アイコンは区間パスのバリアントから解決する) */}
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

      {/* 運賃・運行間隔(種別選択後に表示) */}
      {selectedEntry && (
        <div className="bc-section">
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
                <NumberStepper value={carsPerTrain} onChange={setCarsPerTrain} min={1} max={20} step={1} integerOnly={true} />
                <span className="bc-stepper-unit">{t("transit.carsUnit")}</span>
              </div>
            </div>
          </div>
          <div className="bc-split-row" style={{ marginTop: 8 }}>
            <div className="bc-section-title">{t("transit.stopCountLabel")}</div>
            <div className="bc-stepper-row">
              <span className="bc-description-name">{stops.length}</span>
              <span className="bc-stepper-unit">{t("transit.stopsUnit")}</span>
            </div>
          </div>
          <div className="bc-description-text" style={{ marginTop: 8 }}>
            {t("transit.placeStopHint")}
          </div>
          <div className="bc-description-text" style={{ marginTop: 6, opacity: 0.8 }}>
            {t("transit.roundTripNote")}
          </div>
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
              disabled={isSaving || stops.length < 2}
              onClick={() => void handleConfirm()}
            >
              {isSaving ? t("common.processing") : t("transit.confirmCreate")}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
