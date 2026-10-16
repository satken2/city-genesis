/**
 * 天然資源詳細パネル
 *
 * window_type="resource_detail" のポップアップウィンドウで表示する専用パネル。
 * 選択された天然資源のアイコン・基本情報カードと、残量推移・採取量推移グラフを表示する。
 *
 * 構成:
 * 1. 情報カード ... アイコン + 資源名・現在量・上限量・回復量(マスターデータ解決)
 * 2. 残量推移グラフ ... current_amount 履歴(max_amount の基準線付き)
 * 3. 採取量推移グラフ ... consumption_amount 履歴(1エポックあたりの回復量の基準線付き)
 *
 * グラフは API が返す新しい順の配列からゼロパディングをトリムしたうえで reverse() し、
 * 古い→新しいの時系列順で描画する。
 */
import { PLAY_MODE_SCEARIO } from "../../constants";


import { useEffect, useMemo, useState, useRef } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { invoke } from "@tauri-apps/api/core";
import type { ResourceData } from "../../types/generated";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { usePlayMode } from "../../hooks/usePlayMode";
import { resolveResourceIcon } from "../../utils/resourceIcon";
import { useMasterData } from "../../contexts/MasterDataContext";
import { TimeSeriesChart } from "../common/TimeSeriesChart";
import { CameraFocusButton } from "../common/CameraFocusButton";
import {
  sendCameraFocus,
  sendChangeDisplay,
  sendViewQueryState,
  IPC_EVENT_VIEW_CURRENT_STATE,
} from "../../utils/ipc";
import "./ResourceDetailPanel.css";

type ResourceDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

/**
 * API が返す新しい順の配列末尾にあるゼロパディングを除去する。
 * 実行済みエポック数より多くの枠が確保されている場合に末尾が 0 埋めされるため、
 * グラフがほぼ 0 に張り付く問題を防ぐ。
 */
function trimTrailingZeros(arr: number[]): number[] {
  let end = arr.length;
  while (end > 0 && arr[end - 1] === 0) end--;
  return arr.slice(0, end);
}

export function ResourceDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: ResourceDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const godMode = usePlayMode(apiBaseUrl) === PLAY_MODE_SCEARIO;
  const [resourceData, setResourceData] = useState<ResourceData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

  // 元の天然資源表示モードを保持するRef ("hidden" | "visible" | null)。
  // パネルを開いた時の表示モードを記憶し、閉じる際（アンマウント時）に元の状態へ復元するために使用する。
  const originalResourceModeRef = useRef<string | null>(null);
  
  // 既に表示モードを visible に変更したかどうかのフラグ。
  // マウント時に一度だけ切り替えを実行するために管理する。
  const isSwitchedRef = useRef<boolean>(false);

  // マウント時に現在の天然資源表示モードを取得し、自動的に表示状態にする。
  // アンマウント時には、元の状態が非表示であれば非表示へ復元する。
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    const appWindow = getCurrentWindow();
    let cancelled = false;

    const setup = async () => {
      unlisten = await appWindow.listen<{ layers: Record<string, { mode: string }> }>(
        IPC_EVENT_VIEW_CURRENT_STATE,
        (event) => {
          const resourceState = event.payload?.layers?.resource;
          if (resourceState && originalResourceModeRef.current === null && !isSwitchedRef.current) {
            originalResourceModeRef.current = resourceState.mode;
            isSwitchedRef.current = true;

            // 天然資源が非表示の場合は、詳細表示のために表示モードへ切り替える。
            if (resourceState.mode !== "visible") {
              sendChangeDisplay("resource", "visible").catch((err) => {
                console.error("[ResourceDetailPanel] Failed to show resource:", err);
              });
            }
          }
        }
      );

      if (cancelled) {
        unlisten();
        unlisten = undefined;
        return;
      }

      // 現在の表示状態をUnityへ問い合わせて初期状態を特定する。
      await sendViewQueryState();
    };

    setup().catch((err) => {
      console.warn("[ResourceDetailPanel] setup failed:", err);
    });

    return () => {
      cancelled = true;
      unlisten?.();

      // パネルを開く前が非表示状態だった場合、パネルを閉じる（アンマウント）タイミングで非表示に戻す。
      if (originalResourceModeRef.current === "hidden") {
        sendChangeDisplay("resource", "hidden").catch((err) => {
          console.error("[ResourceDetailPanel] Failed to restore resource display:", err);
        });
      }
    };
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetchApi<{ resource: ResourceData }>(url, { signal: controller.signal })
      .then((response) => {
        if (!controller.signal.aborted) {
          setResourceData(response.resource);
          setLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
          setLoading(false);
        }
      });

    return () => {
      controller.abort();
    };
  }, [url]);

  // 採取量の履歴はエポックあたりの量なので、比べる回復量もエポック長を掛けてそろえる。
  const [simulationDays, setSimulationDays] = useState<number | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    fetchApi<{ city: { simulation_days: number } }>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), {
      signal: controller.signal,
    })
      .then((response) => {
        if (!controller.signal.aborted) setSimulationDays(response.city.simulation_days);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) console.error("[ResourceDetailPanel] Failed to fetch city:", err);
      });
    return () => controller.abort();
  }, [apiBaseUrl]);

  // グラフ用に末尾ゼロをトリムしてから古い→新しい順へ整える。
  const reserveSeries = useMemo(
    () => (resourceData ? trimTrailingZeros([...resourceData.current_amount]).reverse() : []),
    [resourceData]
  );
  const consumptionSeries = useMemo(
    () => (resourceData ? trimTrailingZeros([...resourceData.consumption_amount]).reverse() : []),
    [resourceData]
  );

  if (loading) {
    return <p className="resource-detail__loading">{t("common.loading")}</p>;
  }

  if (error || !resourceData) {
    return (
      <div className="resource-detail__error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  const iconSrc = resolveResourceIcon(resourceData.product_id);
  const productMaster = masterData?.products[resourceData.product_id];
  const displayName = productMaster?.display_name ?? t("resource.unknownResource");
  const unit = productMaster?.unit ?? "";

  const formatWithUnit = (value: number): string =>
    `${Math.round(value).toLocaleString()} ${unit}`;

  // 残量履歴は古い→新しい順なので末尾が最新残量。
  const latestReserve =
    reserveSeries.length > 0 ? reserveSeries[reserveSeries.length - 1] : null;
  const reserveRatioPct =
    resourceData.max_amount > 0 && latestReserve != null
      ? Math.round((latestReserve / resourceData.max_amount) * 100)
      : null;

  /** 1日あたりの回復量 = 上限量 × 回復率 */
  const recoveryAmount = resourceData.max_amount * resourceData.recovery_rate;
  // 1エポックで回復するのは上限量まで(それ以上は満タンで打ち切られる)。
  const recoveryPerEpoch =
    simulationDays != null ? Math.min(resourceData.max_amount, recoveryAmount * simulationDays) : null;

  return (
    <div className="resource-detail">
      {/* ===== 情報カード ===== */}
      <div className="resource-detail__card">
        {iconSrc && (
          <img
            src={iconSrc}
            alt=""
            className="resource-detail__card-icon"
            draggable={false}
          />
        )}
        <div className="resource-detail__card-body">
          <div className="resource-detail__card-name">{displayName}</div>
          <div className="resource-detail__card-stats">
            <span className="resource-detail__card-label">
              {t("resource.currentAmount")}
            </span>
            <span className="resource-detail__card-value">
              {latestReserve != null ? formatWithUnit(latestReserve) : "—"}
            </span>

            <span className="resource-detail__card-label">
              {t("resource.maxAmount")}
            </span>
            <span className="resource-detail__card-value">
              {formatWithUnit(resourceData.max_amount)}
            </span>

            <span className="resource-detail__card-label">
              {t("statistics.resourceReserveRatio")}
            </span>
            <span className="resource-detail__card-value">
              {reserveRatioPct != null ? `${reserveRatioPct}%` : "—"}
            </span>

            <span className="resource-detail__card-label">
              {t("statistics.resourceRecoveryPerDay")}
            </span>
            <span className="resource-detail__card-value">
              {formatWithUnit(recoveryAmount)}
            </span>
          </div>
        </div>
        <CameraFocusButton
          onClick={() => void sendCameraFocus("resource_detail", resourceData.polygon).catch(console.error)}
        />
      </div>

      {/* ===== 残量推移グラフ ===== */}
      <section className="resource-detail__chart">
        <h3 className="resource-detail__chart-title">
          {t("resource.amountTrend")}
        </h3>
        <TimeSeriesChart
          data={reserveSeries}
          color="#4ea1ff"
          height={180}
          referenceLine={{
            value: resourceData.max_amount,
            label: `${t("resource.maxAmount")}: ${resourceData.max_amount.toLocaleString()} ${unit}`,
          }}
          formatter={formatWithUnit}
        />
      </section>

      {/* ===== 採取量推移グラフ ===== */}
      <section className="resource-detail__chart">
        <h3 className="resource-detail__chart-title">
          {t("resource.consumptionTrend")}
        </h3>
        <TimeSeriesChart
          data={consumptionSeries}
          color="#f87171"
          height={180}
          referenceLine={
            recoveryPerEpoch != null
              ? {
                  value: Math.round(recoveryPerEpoch),
                  label: `${t("resource.recoveryAmount")}: ${Math.round(recoveryPerEpoch).toLocaleString()} ${unit}`,
                }
              : undefined
          }
          formatter={formatWithUnit}
        />
      </section>

      {godMode && (
        <button
          className="god-edit-open-btn"
          type="button"
          onClick={() => {
            void invoke("open_registered_window", {
              windowType: "god_edit",
              params: { extra: { entityType: "resource", entityId: String(resourceData.resource_id) } },
            });
          }}
        >
          {t("god_edit.openEditor", "Edit properties")}
        </button>
      )}
    </div>
  );
}
