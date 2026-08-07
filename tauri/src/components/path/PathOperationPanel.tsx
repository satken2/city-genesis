import { useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import type { PathData } from "../../types";
import {
  STATUS_TEMPORARY,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_ACTIVE,
  STATUS_COLLAPSED,
  STATUS_PENDING,
  STATUS_CONSTRUCTING,
  STATUS_DEMOLISHING,
} from "../../constants";
import { sendSessionReload } from "../../utils/ipc";
import trashIcon from "../../assets/images/ui/trash.png";
import bulldozeIcon from "../../assets/images/bulldoze.png";
import editIcon from "../../assets/images/ui/edit.png";
import repairIcon from "../../assets/images/ui/repair.png";
import rocketIcon from "../../assets/images/ui/rocket.png";
import pendingIcon from "../../assets/images/ui/pending.png";
import "./PathOperationPanel.css";

async function openPathEdit(pathId: number) {
  await invoke("open_registered_window", {
    windowType: "path_build",
    params: { extra: { editPathId: pathId } },
  });
}

type PathOperationPanelProps = {
  path: PathData;
  apiBaseUrl: string;
  onActionPerformed: () => void;
};

const PRE_CONSTRUCTION = new Set([STATUS_TEMPORARY, STATUS_SCHEDULED_CONSTRUCTION, STATUS_PENDING]);

export function PathOperationPanel({
  path,
  apiBaseUrl,
  onActionPerformed,
}: PathOperationPanelProps) {
  const { t } = useTranslation();
  const [loading, setLoading] = useState(false);

  const base = apiBaseUrl.replace(/\/+$/, "");
  const isPreConstruction = PRE_CONSTRUCTION.has(path.status);
  // 倒壊したパスは修繕ではなく「再建」の対象となる。
  const isCollapsed = path.status === STATUS_COLLAPSED;

  async function handleToggleStatus() {
    setLoading(true);
    const isPlannedOrConstructing = path.status === STATUS_SCHEDULED_CONSTRUCTION || path.status === STATUS_CONSTRUCTING;
    const newStatus = isPlannedOrConstructing ? STATUS_PENDING : STATUS_SCHEDULED_CONSTRUCTION;
    try {
      const res = await fetch(`${base}/api/v1/paths/${path.path_id}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ status: newStatus }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      onActionPerformed();
    } catch (err) {
      console.error("Toggle status failed:", err);
    } finally {
      setLoading(false);
    }
  }

  async function handleDelete() {
    setLoading(true);
    try {
      const res = await fetch(`${base}/api/v1/paths/${path.path_id}`, {
        method: "DELETE",
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      // Unity側にデータの再ロードを指示する
      await sendSessionReload();
      await invoke("close_current_window");
    } catch (err) {
      console.error("Delete failed:", err);
    } finally {
      setLoading(false);
    }
  }

  async function handleRepair() {
    setLoading(true);
    try {
      const res = await fetch(`${base}/api/v1/paths/${path.path_id}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ under_maintenance: true }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
    } catch (err) {
      console.error("Repair failed:", err);
    } finally {
      setLoading(false);
    }
  }

  // 稼働中のパスを取り壊す（市政府所有のためプレイヤーが自由に解体できる）。専用
  // エンドポイント（POST /demolish）を叩くと STATUS_DEMOLISHING へ遷移し、以降は建設
  // サービス財を消費して解体工事が進み、完了でパスが削除される。取り壊し中に再度押すと
  // DELETE /demolish で解体を取りやめ、STATUS_ACTIVE へ戻す（両者をトグルする）。
  // ステータス（＝3D描画）が変わるため Unity にデータの再ロードを指示する。
  async function handleDemolish() {
    setLoading(true);
    const isDemolishing = path.status === STATUS_DEMOLISHING;
    try {
      const res = await fetch(`${base}/api/v1/paths/${path.path_id}/demolish`, {
        method: isDemolishing ? "DELETE" : "POST",
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await sendSessionReload();
      onActionPerformed();
    } catch (err) {
      console.error("Demolish failed:", err);
    } finally {
      setLoading(false);
    }
  }

  // 倒壊したパスをその場で再建する。専用エンドポイント（/reconstruct）を叩くと
  // STATUS_CONSTRUCTING へ遷移し、以降は通常の敷設と同じく建設サービス財を消費して
  // 工事が進み、完了で STATUS_ACTIVE・新品耐久度に戻る。ステータス（＝3D描画）が
  // 変わるため Unity にデータの再ロードを指示し、詳細パネルも再取得させる。
  async function handleReconstruct() {
    setLoading(true);
    try {
      const res = await fetch(
        `${base}/api/v1/paths/${path.path_id}/reconstruct`,
        { method: "POST" },
      );
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await sendSessionReload();
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
          title={t("path.action.delete")}
          disabled={loading}
          onClick={() => void handleDelete()}
        >
          <img src={trashIcon} alt={t("path.action.delete")} />
        </button>
        {path.status === STATUS_PENDING ? (
          <button
            className="operation-btn"
            type="button"
            title={t("path.action.startConstruction")}
            disabled={loading}
            onClick={() => void handleToggleStatus()}
          >
            <img src={rocketIcon} alt={t("path.action.startConstruction")} />
          </button>
        ) : (
          <button
            className="operation-btn"
            type="button"
            title={t("path.action.stopConstruction")}
            disabled={loading}
            onClick={() => void handleToggleStatus()}
          >
            <img src={pendingIcon} alt={t("path.action.stopConstruction")} />
          </button>
        )}
        <button
          className="operation-btn"
          type="button"
          title={t("path.action.editShape")}
          disabled={loading}
          onClick={() => {
            void openPathEdit(path.path_id);
          }}
        >
          <img src={editIcon} alt={t("path.action.editShape")} />
        </button>
      </div>
    );
  }

  return (
    <div className="operation-panel">
      {/* 倒壊時は修繕ボタンの位置に「再建」ボタンを表示する（両者は排他）。 */}
      {isCollapsed ? (
        <button
          className="operation-btn"
          type="button"
          title={t("path.action.rebuild")}
          disabled={loading}
          onClick={() => void handleReconstruct()}
        >
          <img src={repairIcon} alt={t("path.action.rebuild")} />
        </button>
      ) : (
        <>
          {path.status === STATUS_CONSTRUCTING && (
            <button
              className="operation-btn"
              type="button"
              title={t("path.action.stopConstruction")}
              disabled={loading}
              onClick={() => void handleToggleStatus()}
            >
              <img src={pendingIcon} alt={t("path.action.stopConstruction")} />
            </button>
          )}
          {/* 取り壊し: 市政府所有のパスをプレイヤーが解体する。稼働中↔取り壊し中を
              トグルし、取り壊し中は「取り壊し解除」として機能する。 */}
          {(path.status === STATUS_ACTIVE || path.status === STATUS_DEMOLISHING) && (
            <button
              className="operation-btn"
              type="button"
              title={path.status === STATUS_DEMOLISHING ? t("path.action.cancelBulldoze") : t("path.action.bulldoze")}
              disabled={loading}
              onClick={() => void handleDemolish()}
            >
              <img src={bulldozeIcon} alt={t("path.action.bulldoze")} />
            </button>
          )}
          {path.status !== STATUS_DEMOLISHING && (
            <button
              className="operation-btn"
              type="button"
              title={t("path.action.repair")}
              disabled={loading || !!path.under_maintenance}
              onClick={() => void handleRepair()}
            >
              <img src={repairIcon} alt={t("path.action.repair")} />
            </button>
          )}
        </>
      )}
    </div>
  );
}
