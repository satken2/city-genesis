/**
 * 地形セル開放パネル
 *
 * window_type="terrain_unlock" のポップアップウィンドウで表示する専用パネル。
 * 地形セルを選択してバックエンドの POST /api/v1/terrain/acquire-cell で開放する。
 *
 * 操作フロー:
 *   1. コックピットの pixels アイコンをクリックしてパネルを開く
 *   2. 同時に Unity に STATE_UNLOCK_CELL を送信し、地形セル選択モードに入る
 *   3. Unity で地形セルをクリックすると unlock_cell:select_cell イベントが通知される
 *   4. 未開放セルの場合は座標と確定ボタンを表示する
 *   5. 確定ボタンでバックエンドに開放リクエストを送信する
 *   6. 開放済みセルの場合は「解放済みです」と表示する
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { IPC_EVENT_UNLOCK_CELL_SELECT, sendSessionReload } from "../../utils/ipc";
import { setHintbarText } from "../../utils/hintbar";
import "./TerrainUnlockPanel.css";

type CellInfo = {
  x: number;
  z: number;
  alreadyAcquired: boolean;
};

type TerrainUnlockPanelProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

export function TerrainUnlockPanel({ apiBaseUrl, onClose }: TerrainUnlockPanelProps) {
  const { t } = useTranslation();
  const appWindow = useMemo(() => getCurrentWindow(), []);

  const [cell, setCell] = useState<CellInfo | null>(null);
  const [loading, setLoading] = useState(false);
  const [message, setMessage] = useState<string>("");
  const [error, setError] = useState<string>("");

  /**
   * パネルが開いたときにヒントバーに地形セル開放の操作説明を表示し、
   * 閉じられたときにヒントをクリアする。
   */
  useEffect(() => {
    let cancelled = false;
    (async () => {
      await setHintbarText(t("hint.terrainUnlock"));
    })().catch((err) => {
      if (!cancelled) console.error("[TerrainUnlock] setHintbarText failed:", err);
    });
    return () => {
      cancelled = true;
      setHintbarText("").catch(() => {});
    };
  }, [t]);

  /**
   * Unity からのセル選択イベントを購読する。
   * unlock_cell:select_cell イベントを受信すると、
   * セル座標と開放済みフラグを state に保存する。
   */
  useEffect(() => {
    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{ cell_x: number; cell_z: number; already_acquired: boolean }>(
        IPC_EVENT_UNLOCK_CELL_SELECT,
        (event) => {
          const payload = event.payload;
          const info: CellInfo = {
            x: payload.cell_x,
            z: payload.cell_z,
            alreadyAcquired: payload.already_acquired,
          };
          setCell(info);
          setError("");
          setMessage("");
        }
      )
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);

    return () => {
      unlisten?.();
    };
  }, [appWindow]);

  /**
   * バックエンドの POST /api/v1/terrain/acquire-cell でセルを開放する。
   * 成功したら TerrainManager のローカルキャッシュも更新されるため、
   * TerrainUnlockOverlay のグレーアウトが自動的に解除される。
   */
  const handleConfirm = useCallback(async () => {
    if (cell == null || cell.alreadyAcquired) return;
    setLoading(true);
    setError("");
    setMessage("");

    try {
      const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/terrain/acquire-cell`;
      const res = await fetch(url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ cell_x: cell.x, cell_z: cell.z }),
      });

      if (!res.ok) {
        const body = await res.text().catch(() => "");
        throw new Error(`HTTP ${res.status}: ${body}`);
      }

      setMessage(t("terrainUnlock.acquireSuccess"));
      setCell(null);
      // Unity側のTerrainManagerキャッシュを更新し、TerrainUnlockOverlayのグレーアウトを解除する
      await sendSessionReload();
    } catch (err) {
      console.error("[TerrainUnlock] acquire failed:", err);
      setError(t("terrainUnlock.acquireError"));
    } finally {
      setLoading(false);
    }
  }, [apiBaseUrl, cell, t]);

  return (
    <div className="terrain-unlock-panel">
      <h2 className="terrain-unlock-title">{t("terrainUnlock.title")}</h2>

      {cell == null ? (
        <p className="terrain-unlock-hint">{t("terrainUnlock.selectCell")}</p>
      ) : cell.alreadyAcquired ? (
        <p className="terrain-unlock-hint terrain-unlock-warn">
          {t("terrainUnlock.alreadyAcquired", { x: cell.x, z: cell.z })}
        </p>
      ) : (
        <div className="terrain-unlock-info">
          <p className="terrain-unlock-cell">
            {t("terrainUnlock.cellSelected", { x: cell.x, z: cell.z })}
          </p>
          <button
            className="terrain-unlock-confirm-btn"
            type="button"
            disabled={loading}
            onClick={() => void handleConfirm()}
          >
            {loading ? t("terrainUnlock.acquiring") : t("terrainUnlock.confirm")}
          </button>
        </div>
      )}

      {message && <p className="terrain-unlock-message">{message}</p>}
      {error && <p className="terrain-unlock-error">{error}</p>}

      <button className="terrain-unlock-close-btn" type="button" onClick={onClose}>
        {t("terrainUnlock.cancel")}
      </button>
    </div>
  );
}
