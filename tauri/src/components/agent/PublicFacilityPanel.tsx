/**
 * 公共/公営施設コントロールパネル
 *
 * 公共施設(public_facility=true)の詳細パネル上部に表示する操作カード。
 * 従来は「稼働率目標」専用カードだった領域を、公共施設の各種操作をまとめた
 * コントロールパネルへ拡張したもの。以下をひとつのカードに集約する。
 *
 * - 操作ボタン: 修繕/再建・停止/開始(トグル)・取り壊し。計画中は削除・編集。
 *   停止と開始は同一ボタンでアイコンを切り替えるトグルとして実装する。
 * - 稼働率目標スライダー(建設中は「建設スピード」、定常生産施設は「稼働率目標」)。
 * - 放棄チェックボックス(建物のみ)。放棄すると自動修繕の対象外になり、稼働中なら停止する。
 * - 修繕閾値の表示(放棄されていない建物のみ)。都市共通の設定値を参照表示するだけで、
 *   ここでは編集しない(編集は規制ウィンドウで行う)。
 *
 * 非建物の公共施設(公営サービス事業者など)は放棄できず、修繕・停止/開始・修繕閾値も
 * 対象外のため、取り壊し(計画中は削除)のみを表示する。
 * 市政府(TYPE_CITY)は恒久的な存在であり操作を持たないため、このカード自体を表示しない。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import type { AgentData, AgentResponse } from "../../types";
import {
  STATUS_ACTIVE,
  STATUS_PAUSED,
  STATUS_TEMPORARY,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_SCHEDULED_DEMOLITION,
  STATUSES_RUIN,
  STATUS_CONSTRUCTING,
  STATUS_PENDING,
  TYPE_CITY,
} from "../../constants";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import bulldozeIcon from "../../assets/images/bulldoze.png";
import pauseIcon from "../../assets/images/ui/pause.png";
import playIcon from "../../assets/images/ui/play.png";
import trashIcon from "../../assets/images/ui/trash.png";
import repairIcon from "../../assets/images/ui/repair.png";
import editIcon from "../../assets/images/ui/edit.png";
import rocketIcon from "../../assets/images/ui/rocket.png";
import pendingIcon from "../../assets/images/ui/pending.png";
import "./AgentOperationPanel.css";
import "./PublicFacilityPanel.css";

type PublicFacilityPanelProps = {
  agent: AgentData;
  apiBaseUrl: string;
  /** 操作後にパネルを再取得させるコールバック */
  onActionPerformed: () => void;
  /** 稼働率目標(0〜500%)。親コンポーネントが保持する編集中値 */
  utilizationTarget: number;
  /** スライダー操作中の値更新 */
  setUtilizationTarget: (value: number) => void;
  /** スライダー確定時(mouseUp/touchEnd)にサーバへ反映する */
  onUtilizationCommit: () => void;
  /** 建設中の建物の1エポック建設コスト(建設中以外は null) */
  constructionCostPerEpoch: number | null;
  /** 建設中の建物の残り建設期間(エポック。建設中以外は null) */
  remainingEpochs: number | null;
};

const PRE_CONSTRUCTION = new Set([STATUS_TEMPORARY, STATUS_SCHEDULED_CONSTRUCTION, STATUS_PENDING]);

async function openBuildingEdit(agentId: number) {
  await invoke("open_registered_window", {
    windowType: "building_construction",
    params: { extra: { editAgentId: agentId } },
  });
}

export function PublicFacilityPanel({
  agent,
  apiBaseUrl,
  onActionPerformed,
  utilizationTarget,
  setUtilizationTarget,
  onUtilizationCommit,
  constructionCostPerEpoch,
  remainingEpochs,
}: PublicFacilityPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const [loading, setLoading] = useState(false);
  // 都市共通の修繕閾値(0〜1)。修繕閾値ラベルの表示にのみ使う参照値。
  const [cityRepairThreshold, setCityRepairThreshold] = useState<number | null>(null);

  const base = apiBaseUrl.replace(/\/+$/, "");

  // 修繕閾値ラベル用に都市共通の設定値を取得する。放棄されていない建物にのみ
  // 表示するため、その条件を満たすときだけ取得すれば十分だが、状態遷移で表示条件が
  // 変わっても値を持っておけるよう、マウント時に一度だけ取得しておく。
  useEffect(() => {
    const controller = new AbortController();
    fetchApi<{ city: { repair_threshold?: number } }>(
      buildApiUrl(apiBaseUrl, "/api/v1/city"),
      { signal: controller.signal },
    )
      .then((res) => {
        if (!controller.signal.aborted) {
          setCityRepairThreshold(res.city.repair_threshold ?? null);
        }
      })
      .catch(() => {
        if (!controller.signal.aborted) setCityRepairThreshold(null);
      });
    return () => controller.abort();
  }, [apiBaseUrl]);

  // 市政府(TYPE_CITY)は恒久的な存在であり操作を持たないため、カードを表示しない。
  if (agent.type === TYPE_CITY) {
    return null;
  }

  const agentUrl = `${base}/api/v1/agents/${agent.agent_id}`;

  async function patchAgent(body: Record<string, unknown>) {
    setLoading(true);
    try {
      await fetchApi<AgentResponse>(agentUrl, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      onActionPerformed();
    } catch (err) {
      console.error("Operation failed:", err);
    } finally {
      setLoading(false);
    }
  }

  async function handleDelete() {
    setLoading(true);
    try {
      await fetchApi(agentUrl, { method: "DELETE" });
      onActionPerformed();
      try {
        await invoke("close_current_window");
      } catch {
        // ウィンドウが既に閉じている場合は無視
      }
    } catch (err) {
      console.error("Delete failed:", err);
    } finally {
      setLoading(false);
    }
  }

  async function handleBulldoze() {
    // 取り壊し予定のときはトグルで解除、それ以外は取り壊し予定に遷移する。
    if (agent.status === STATUS_SCHEDULED_DEMOLITION) {
      await patchAgent({ status: STATUS_ACTIVE });
    } else {
      await patchAgent({ status: STATUS_SCHEDULED_DEMOLITION });
    }
  }

  // 停止/開始トグル。稼働中なら停止、停止中なら再開する。
  async function handleToggleActive() {
    if (agent.status === STATUS_PAUSED) {
      await patchAgent({ status: STATUS_ACTIVE });
    } else {
      await patchAgent({ status: STATUS_PAUSED });
    }
  }

  // 着工/保留トグル。着工指示中なら保留へ、保留中なら着工へ切り替える
  async function handleToggleStatus() {
    const isPlannedOrConstructing = status === STATUS_SCHEDULED_CONSTRUCTION || status === STATUS_CONSTRUCTING;
    const newStatus = isPlannedOrConstructing ? STATUS_PENDING : STATUS_SCHEDULED_CONSTRUCTION;
    await patchAgent({ status: newStatus });
  }

  async function handleRepair() {
    await patchAgent({ under_maintenance: true });
  }

  // 倒壊した建物をその場で再建する(専用エンドポイント)。
  async function handleReconstruct() {
    setLoading(true);
    try {
      await fetchApi(`${agentUrl}/reconstruct`, { method: "POST" });
      onActionPerformed();
    } catch (err) {
      console.error("Reconstruct failed:", err);
    } finally {
      setLoading(false);
    }
  }

  // 放棄フラグのトグル。放棄すると稼働中の施設は停止(STATUS_PAUSED)になる
  // (状態遷移はバックエンドが行う)。
  async function handleAbandonToggle(next: boolean) {
    await patchAgent({ abandoned: next });
  }

  const status = agent.status;
  const isPreConstruction = PRE_CONSTRUCTION.has(status);
  const isCollapsed = STATUSES_RUIN.includes(status);
  const isBuilding = agent.is_bd;
  const isAbandoned = !!agent.abandoned;
  // 稼働中/停止中は放棄・修繕・停止開始トグルの操作対象となる状態。
  const isRunningState = status === STATUS_ACTIVE || status === STATUS_PAUSED;
  const isPaused = status === STATUS_PAUSED;

  // 稼働率目標スライダーの表示条件(従来のカードと同一)。
  // 建設中は「建設スピード」(最大500%)、余力方式の施設は「稼働率目標」(最大100%)。
  const agentMaster = masterData?.agents[agent.variant];
  const isHeadroomFacility = (agentMaster?.production_headroom ?? 0) > 0;
  const isConstructing = status === STATUS_CONSTRUCTING;
  const showSlider = isConstructing || isHeadroomFacility;
  const sliderMax = isConstructing ? 500 : 100;

  // 放棄チェックボックスと修繕閾値ラベルは建物かつ稼働中/停止中のときのみ表示する。
  const showAbandon = isBuilding && isRunningState;
  const showThreshold = isBuilding && isRunningState && !isAbandoned;

  return (
    <div className="pf-panel">
      <div className="pf-panel__title">
        {t("agent.publicFacilityPanel.title", "公共/公営施設")}
      </div>

      <div className="operation-panel">
        {isPreConstruction ? (
          <>
            {isBuilding && (status === STATUS_PENDING || status === STATUS_SCHEDULED_CONSTRUCTION) && (
              <button
                className="operation-btn"
                type="button"
                title={status === STATUS_PENDING ? t("agent.action.startConstruction") : t("agent.action.stopConstruction")}
                disabled={loading}
                onClick={() => void handleToggleStatus()}
              >
                <img
                  src={status === STATUS_PENDING ? rocketIcon : pendingIcon}
                  alt="toggle construction"
                />
              </button>
            )}
            {isBuilding && (
              <button
                className="operation-btn"
                type="button"
                title={t("agent.action.edit")}
                disabled={loading}
                onClick={() => void openBuildingEdit(agent.agent_id)}
              >
                <img src={editIcon} alt={t("agent.action.edit")} />
              </button>
            )}
            <button
              className="operation-btn operation-btn--delete"
              type="button"
              title={t("agent.action.delete")}
              disabled={loading}
              onClick={() => void handleDelete()}
            >
              <img src={trashIcon} alt={t("agent.action.delete")} />
            </button>
          </>
        ) : (
          <>
            {/* 倒壊時は修繕ではなく「再建」ボタンを表示する(両者は排他)。 */}
            {isCollapsed ? (
              <button
                className="operation-btn"
                type="button"
                title={t("agent.action.rebuild")}
                disabled={loading || !isBuilding}
                onClick={() => void handleReconstruct()}
              >
                <img src={repairIcon} alt={t("agent.action.rebuild")} />
              </button>
            ) : (
              <button
                className="operation-btn"
                type="button"
                title={t("agent.action.repair")}
                disabled={
                  loading ||
                  !isBuilding ||
                  !isRunningState ||
                  isAbandoned ||
                  !!agent.under_maintenance
                }
                onClick={() => void handleRepair()}
              >
                <img src={repairIcon} alt={t("agent.action.repair")} />
              </button>
            )}

            {/* 着工トグルボタン（着工指示中・建設中） */}
            {isBuilding && status === STATUS_CONSTRUCTING && (
              <button
                className="operation-btn"
                type="button"
                title={t("agent.action.stopConstruction")}
                disabled={loading}
                onClick={() => void handleToggleStatus()}
              >
                <img src={pendingIcon} alt={t("agent.action.stopConstruction")} />
              </button>
            )}

            {/* 停止/開始トグル。建物のみ。放棄中は無効(先に放棄を解除する)。 */}
            {isBuilding && (
              <button
                className="operation-btn"
                type="button"
                title={
                  isPaused ? t("agent.action.resume") : t("agent.action.pause")
                }
                disabled={loading || !isRunningState || isAbandoned}
                onClick={() => void handleToggleActive()}
              >
                <img
                  src={isPaused ? playIcon : pauseIcon}
                  alt={
                    isPaused
                      ? t("agent.action.resume")
                      : t("agent.action.pause")
                  }
                />
              </button>
            )}

            <button
              className="operation-btn"
              type="button"
              title={
                status === STATUS_SCHEDULED_DEMOLITION
                  ? t("agent.action.cancelBulldoze")
                  : t("agent.action.bulldoze")
              }
              disabled={loading}
              onClick={() => void handleBulldoze()}
            >
              <img src={bulldozeIcon} alt={t("agent.action.bulldoze")} />
            </button>
          </>
        )}
      </div>

      {showSlider && (
        <div className="agent-detail-utilization-target">
          <div className="agent-detail-utilization-target-header">
            <span className="agent-detail-utilization-target-title">
              {isConstructing
                ? t("agent.constructionSpeed", "建設スピード")
                : t("agent.utilizationTarget", "稼働率目標")}
            </span>
            <span className="agent-detail-utilization-target-value">
              {utilizationTarget}%
            </span>
          </div>
          <div className="agent-detail-utilization-target-slider-container">
            <input
              type="range"
              min="0"
              max={sliderMax}
              value={utilizationTarget}
              onChange={(e) => setUtilizationTarget(Number(e.target.value))}
              onMouseUp={onUtilizationCommit}
              onTouchEnd={onUtilizationCommit}
              className="agent-detail-utilization-target-slider"
            />
          </div>
          {isConstructing && (
            <div className="agent-detail-grid" style={{ marginTop: 6 }}>
              <span className="agent-detail-label">
                {t("agent.constructionCostPerEpoch", "1エポックの建設コスト")}
              </span>
              <span className="agent-detail-value">
                {constructionCostPerEpoch != null
                  ? t("common.currency", {
                      value: Math.round(constructionCostPerEpoch),
                    })
                  : "—"}
              </span>
              <span className="agent-detail-label">
                {t("agent.remainingConstruction", "残り建設期間")}
              </span>
              <span className="agent-detail-value">
                {remainingEpochs != null
                  ? t("agent.remainingEpochs", {
                      defaultValue: "約{{n}}エポック",
                      n: Math.ceil(remainingEpochs),
                    })
                  : "—"}
              </span>
            </div>
          )}
        </div>
      )}

      {showAbandon && (
        <label className="pf-panel__abandon">
          <input
            type="checkbox"
            checked={isAbandoned}
            disabled={loading}
            onChange={(e) => void handleAbandonToggle(e.target.checked)}
          />
          <span className="pf-panel__abandon-text">
            {t("agent.abandon", "放棄")}
          </span>
          <span className="pf-panel__abandon-hint">
            {t(
              "agent.abandonHint",
              "維持を放棄し、修繕を行わず老朽化に任せます",
            )}
          </span>
        </label>
      )}

      {showThreshold && (
        <div className="pf-panel__threshold">
          <span className="pf-panel__threshold-label">
            {t("agent.repairThreshold", "修繕閾値")}
          </span>
          <span className="pf-panel__threshold-value">
            {cityRepairThreshold != null
              ? `${Math.round(cityRepairThreshold * 100)}%`
              : "—"}
          </span>
        </div>
      )}
    </div>
  );
}
