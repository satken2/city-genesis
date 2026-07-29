import { useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import type { AgentData } from "../../types";
import {
  STATUS_ACTIVE,
  STATUS_PAUSED,
  STATUS_TEMPORARY,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_SCHEDULED_DEMOLITION,
  STATUS_COLLAPSED,
  STATUS_PENDING,
  TYPE_DEFAULT,
  TYPE_CITY,
  TYPE_HOUSEHOLD,
  TYPE_TRADE,
} from "../../constants";
import purchaseIcon from "../../assets/images/purchase.png";
import bulldozeIcon from "../../assets/images/bulldoze.png";
import pauseIcon from "../../assets/images/ui/pause.png";
import playIcon from "../../assets/images/ui/play.png";
import trashIcon from "../../assets/images/ui/trash.png";
import repairIcon from "../../assets/images/ui/repair.png";
import editIcon from "../../assets/images/ui/edit.png";
import "./AgentOperationPanel.css";

async function openBuildingEdit(agentId: number) {
  await invoke("open_registered_window", {
    windowType: "building_construction",
    params: { extra: { editAgentId: agentId } },
  });
}

type AgentOperationPanelProps = {
  agent: AgentData;
  apiBaseUrl: string;
  onActionPerformed: () => void;
};

const PRE_CONSTRUCTION = new Set([STATUS_TEMPORARY, STATUS_SCHEDULED_CONSTRUCTION, STATUS_PENDING]);

const NO_OPERATION_TYPES = new Set([TYPE_DEFAULT, TYPE_CITY]);

export function AgentOperationPanel({
  agent,
  apiBaseUrl,
  onActionPerformed,
}: AgentOperationPanelProps) {
  const { t } = useTranslation();
  const [loading, setLoading] = useState(false);

  if (NO_OPERATION_TYPES.has(agent.type)) {
    return null;
  }

  const isPreConstruction = PRE_CONSTRUCTION.has(agent.status);
  const base = apiBaseUrl.replace(/\/+$/, "");

  async function handleAction(method: string, body?: Record<string, unknown>) {
    setLoading(true);
    try {
      const res = await fetch(`${base}/api/v1/agents/${agent.agent_id}`, {
        method,
        headers: body ? { "Content-Type": "application/json" } : undefined,
        body: body ? JSON.stringify(body) : undefined,
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      onActionPerformed();
    } catch (err) {
      console.error("Operation failed:", err);
    } finally {
      setLoading(false);
    }
  }

  async function handleDelete() {
    await handleAction("DELETE");
    try {
      await invoke("close_current_window");
    } catch {
      // ウィンドウが既に閉じている場合は無視
    }
  }

  async function handlePurchase() {
    await handleAction("PATCH", { under_aquisition: true });
  }

  async function handleBulldoze() {
    if (agent.status === STATUS_SCHEDULED_DEMOLITION) {
      await handleAction("PATCH", { status: STATUS_ACTIVE });
    } else {
      await handleAction("PATCH", { status: STATUS_SCHEDULED_DEMOLITION });
    }
  }

  async function handlePause() {
    await handleAction("PATCH", { status: STATUS_PAUSED });
  }

  async function handleResume() {
    await handleAction("PATCH", { status: STATUS_ACTIVE });
  }

  async function handleRepair() {
    await handleAction("PATCH", { under_maintenance: true });
  }

  // 倒壊した建物をその場で再建する。専用エンドポイント（/reconstruct）を叩くと
  // STATUS_CONSTRUCTING へ遷移し、以降は通常の建設と同じく建設サービス財を消費して
  // 工事が進み、完了で STATUS_ACTIVE・新品耐久度に戻る。
  async function handleReconstruct() {
    setLoading(true);
    try {
      const res = await fetch(
        `${base}/api/v1/agents/${agent.agent_id}/reconstruct`,
        { method: "POST" },
      );
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      onActionPerformed();
    } catch (err) {
      console.error("Reconstruct failed:", err);
    } finally {
      setLoading(false);
    }
  }

  if (isPreConstruction) {
    return (
      <div className="operation-panel">
        <button
          className="operation-btn"
          type="button"
          title={t("agent.action.delete")}
          disabled={loading || !agent.public_facility}
          onClick={() => void handleDelete()}
        >
          <img src={trashIcon} alt={t("agent.action.delete")} />
        </button>
        {agent.is_bd && (
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
      </div>
    );
  }

  const canPurchase =
    !agent.public_facility &&
    agent.type !== TYPE_HOUSEHOLD &&
    agent.type !== TYPE_TRADE;

  const canBulldoze =
    agent.public_facility ||
    agent.type === TYPE_TRADE;

  const canPause =
    agent.public_facility && agent.is_bd && agent.status === STATUS_ACTIVE;

  const canResume =
    agent.public_facility && agent.is_bd && agent.status === STATUS_PAUSED;

  const canRepair =
    agent.public_facility && !agent.under_maintenance;

  // 倒壊した建物は修繕ではなく「再建」の対象となる。市政府所有の公共施設に限る。
  const isCollapsed = agent.status === STATUS_COLLAPSED;
  const canReconstruct = agent.public_facility && agent.is_bd && isCollapsed;

  return (
    <div className="operation-panel">
      <button
        className="operation-btn"
        type="button"
        title={t("agent.action.purchase")}
        disabled={loading || !canPurchase}
        onClick={() => void handlePurchase()}
      >
        <img src={purchaseIcon} alt={t("agent.action.purchase")} />
      </button>
      <button
        className="operation-btn"
        type="button"
        title={agent.status === STATUS_SCHEDULED_DEMOLITION ? t("agent.action.cancelBulldoze") : t("agent.action.bulldoze")}
        disabled={loading || !canBulldoze}
        onClick={() => void handleBulldoze()}
      >
        <img src={bulldozeIcon} alt={t("agent.action.bulldoze")} />
      </button>
      <button
        className="operation-btn"
        type="button"
        title={t("agent.action.pause")}
        disabled={loading || !canPause}
        onClick={() => void handlePause()}
      >
        <img src={pauseIcon} alt={t("agent.action.pause")} />
      </button>
      <button
        className="operation-btn"
        type="button"
        title={t("agent.action.resume")}
        disabled={loading || !canResume}
        onClick={() => void handleResume()}
      >
        <img src={playIcon} alt={t("agent.action.resume")} />
      </button>
      {/* 倒壊時は修繕ボタンの位置に「再建」ボタンを表示する（両者は排他）。 */}
      {isCollapsed ? (
        <button
          className="operation-btn"
          type="button"
          title={t("agent.action.rebuild")}
          disabled={loading || !canReconstruct}
          onClick={() => void handleReconstruct()}
        >
          <img src={repairIcon} alt={t("agent.action.rebuild")} />
        </button>
      ) : (
        <button
          className="operation-btn"
          type="button"
          title={t("agent.action.repair")}
          disabled={loading || !canRepair}
          onClick={() => void handleRepair()}
        >
          <img src={repairIcon} alt={t("agent.action.repair")} />
        </button>
      )}
    </div>
  );
}
