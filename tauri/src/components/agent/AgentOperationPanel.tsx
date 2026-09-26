import { useEffect, useRef, useState } from "react";
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
import { sendBuildingExploded, sendSessionReload } from "../../utils/ipc";
import { fetchApi } from "../../utils/api";
import { requestConfirm } from "../../utils/confirmDialog";
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

/**
 * 爆破ボタンのアイコン(導火線付きの爆弾)。
 * 他の操作アイコンと違い外部素材ではなく自前のSVGで描く(素材のクレジット表記を
 * 増やさないため)。塗りは currentColor なのでボタン側の文字色に追従する。
 */
function BombIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <circle cx="10.5" cy="15.5" r="6.5" fill="currentColor" />
      <path
        d="M14.8 10.8 C16.2 9.4 17 8.4 17.6 7.4"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
      />
      <path
        d="M19.5 1.5 L20.4 4.1 L23 5 L20.4 5.9 L19.5 8.5 L18.6 5.9 L16 5 L18.6 4.1 Z"
        fill="currentColor"
      />
    </svg>
  );
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
  const mountedRef = useRef(true);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  if (NO_OPERATION_TYPES.has(agent.type)) {
    return null;
  }

  const isPreConstruction = PRE_CONSTRUCTION.has(agent.status);
  const base = apiBaseUrl.replace(/\/+$/, "");

  /** 成功したら true を返す(呼び出し側が後続処理の実行可否を判断できるようにする)。 */
  async function handleAction(
    method: string,
    body?: Record<string, unknown>,
    notify: boolean = true,
  ): Promise<boolean> {
    setLoading(true);
    try {
      const res = await fetch(`${base}/api/v1/agents/${agent.agent_id}`, {
        method,
        headers: body ? { "Content-Type": "application/json" } : undefined,
        body: body ? JSON.stringify(body) : undefined,
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      if (notify) onActionPerformed();
      return true;
    } catch (err) {
      console.error("Operation failed:", err);
      return false;
    } finally {
      setLoading(false);
    }
  }

  // 計画中の主体を削除する。バックエンドから消えても Unity 側の3Dオブジェクトは
  // データを取り直すまで残り続けるため、削除成功後に session:reload を送って差分同期
  // させる(取得結果に含まれない建物は Unity 側で破棄される)。
  // 削除済みのエージェントを再取得しても 404 になるだけなので onActionPerformed は
  // 呼ばず(notify=false)、バックエンドに拒否された場合はウィンドウも閉じない。
  async function handleDelete() {
    const ok = await handleAction("DELETE", undefined, false);
    if (!ok) return;
    await sendSessionReload();
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

  // 民間の建物を事故に見せかけて爆破する。専用エンドポイント（/explode）を叩くと
  // エポック実行を待たずその場で STATUS_COLLAPSED へ移り、廃墟として残る。
  // 中に居た住民・従業員は次のエポック実行で事故死として確定し、都市スコアが下がる。
  // 取り返しがつかないうえ誤操作の代償が大きいので、実行前に必ず確認を取る。
  async function handleExplode() {
    const confirmed = await requestConfirm({
      titleKey: "agent.action.explodeTitle",
      message: t("agent.action.explodeConfirm"),
      confirmLabel: t("agent.action.explodeConfirmAction"),
      tone: "danger",
    });
    // 確認中に詳細パネルが別の建物へ切り替わっていたら、確認文の「この建物」が
    // 画面上の建物と食い違うので実行しない
    if (!confirmed || !mountedRef.current) return;
    setLoading(true);
    try {
      await fetchApi(`${base}/api/v1/agents/${agent.agent_id}/explode`, {
        method: "POST",
      });
      // API成功時だけ通知する。3D通知が失敗しても、確定済みの状態はパネルへ反映する。
      try {
        await sendBuildingExploded(agent.agent_id);
      } catch (err) {
        console.error("Explosion graphics notification failed:", err);
      }
      onActionPerformed();
    } catch (err) {
      console.error("Explode failed:", err);
    } finally {
      setLoading(false);
    }
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

  // 爆破は民間の稼働中建物だけを対象にする。公共施設と輸出入拠点は市政府が自前で
  // 取り壊せるため事故を装う意味がなく、計画中・建設中・廃墟には倒壊させる構造物がない。
  const canExplode =
    !agent.public_facility &&
    agent.is_bd &&
    agent.type !== TYPE_TRADE &&
    (agent.status === STATUS_ACTIVE || agent.status === STATUS_PAUSED);

  // 廃墟(倒壊・焼失)は修繕ではなく「再建」の対象となる。市政府所有の公共施設に限る。
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
      {/* 非合法な取り壊し。買収・解体の手順を踏まずに建物を廃墟にできるが、
          中に居た市民が事故死して市民感情が大きく悪化する。 */}
      {canExplode && (
        <button
          className="operation-btn operation-btn--danger"
          type="button"
          title={t("agent.action.explode")}
          disabled={loading}
          onClick={() => void handleExplode()}
        >
          <BombIcon />
        </button>
      )}
    </div>
  );
}
