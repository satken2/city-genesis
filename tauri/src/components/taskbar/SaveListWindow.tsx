/**
 * セーブデータ一覧ウィンドウコンポーネント
 *
 * SaveLoadWindowの「ロード」ボタンから別ウィンドウとして開く。
 * バックエンドAPIから全セーブデータのメタデータ一覧を取得し、
 * カード形式で表示する。各カードには都市名・人口・ゲーム内日付・保存日時を含み、
 * ロードボタンで選択したセーブデータを読み込む。削除ボタンでセーブデータを削除する。
 *
 * 現在読み込み中のセーブデータ(current_save_id)はハイライト表示される。
 * ロード・削除の実行前には確認ダイアログを表示する。
 */

import { useState, useCallback, useEffect } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { EpochDate } from "../common/EpochDate";
import "./SaveListWindow.css";

type SaveListWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

/**
 * セーブデータのメタデータエントリ
 * GET /api/v1/system/saves の各要素の型
 */
type SaveEntry = {
  id: string;
  city_name: string;
  population: number;
  current_date: number;
  epoch: number;
  simulation_days: number;
  saved_at: string;
  is_scenario: boolean;
  language: string;
};

/**
 * GET /api/v1/system/saves レスポンスの型
 */
type SavesResponse = {
  success: boolean;
  saves: Record<string, SaveEntry>;
};

/**
 * GET /api/v1/sessions レスポンスの型
 */
type SessionInfo = {
  current_save_id: string | null;
};

/**
 * ステータスメッセージの型
 */
type StatusMessage = {
  text: string;
  type: "success" | "error" | "info";
};

export function SaveListWindow({ apiBaseUrl, onClose }: SaveListWindowProps) {
  const { t } = useTranslation();
  const [saveEntries, setSaveEntries] = useState<SaveEntry[]>([]);
  const [currentSaveId, setCurrentSaveId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<StatusMessage | null>(null);
  const [confirmSaveId, setConfirmSaveId] = useState<string | null>(null);
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);

  /**
   * ウィンドウ初期化時にセーブデータ一覧とセッション情報を同時に取得する。
   * セッション情報は現在のセーブIDを取得するために使用する(ハイライト表示用)。
   */
  useEffect(() => {
    if (!apiBaseUrl) return;
    const base = apiBaseUrl.replace(/\/+$/, "");

    async function fetchData() {
      setLoading(true);
      try {
        const [savesRes, sessionRes] = await Promise.all([
          fetch(`${base}/api/v1/saves`),
          fetch(`${base}/api/v1/sessions`),
        ]);

        if (savesRes.ok) {
          const savesData: SavesResponse = await savesRes.json();
          if (savesData.success && savesData.saves) {
            const entries = Object.values(savesData.saves);
            setSaveEntries(entries);
          }
        }

        if (sessionRes.ok) {
          const sessionData: SessionInfo = await sessionRes.json();
          setCurrentSaveId(sessionData.current_save_id);
        }
      } catch {
        /* 取得失敗時は空配列のまま */
      } finally {
        setLoading(false);
      }
    }

    void fetchData();
  }, [apiBaseUrl]);

  /**
   * セーブデータのロード実行
   * Rust 側の start_load_game コマンドを呼び出し、
   * Unity 起動 → Loading 表示 → バックエンドロード → ポーリング待機 → HUD 表示
   * という一連のフローを Rust 側に委譲する。
   */
  const handleLoad = useCallback(
    async (saveId: string) => {
      setBusy(true);
      setConfirmSaveId(null);
      setStatus({ text: t("saveList.loading"), type: "info" });
      try {
        await invoke("start_load_game", { saveId });
        setCurrentSaveId(saveId);
        setStatus({ text: t("saveList.loadSuccess"), type: "success" });
        onClose();
      } catch (error) {
        setStatus({
          text: error instanceof Error ? error.message : t("saveList.loadFailed"),
          type: "error",
        });
      } finally {
        setBusy(false);
      }
    },
    [t, onClose],
  );

  /**
   * セーブデータの削除実行
   * DELETE /api/v1/saves/{save_id} を呼び出し、成功後は一覧から取り除く。
   */
  const handleDelete = useCallback(
    async (saveId: string) => {
      const base = apiBaseUrl.replace(/\/+$/, "");
      setBusy(true);
      setConfirmDeleteId(null);
      try {
        const res = await fetch(`${base}/api/v1/saves/${saveId}`, {
          method: "DELETE",
        });
        if (res.ok) {
          setSaveEntries((prev) => prev.filter((e) => e.id !== saveId));
          setStatus({ text: t("saveList.deleteSuccess"), type: "success" });
        } else {
          setStatus({ text: t("saveList.deleteFailed"), type: "error" });
        }
      } catch {
        setStatus({ text: t("saveList.deleteFailed"), type: "error" });
      } finally {
        setBusy(false);
      }
    },
    [apiBaseUrl, t],
  );

  /**
   * セーブフォルダをエクスプローラーで開く
   */
  const handleOpenFolder = useCallback(async () => {
    try {
      await invoke("open_save_directory");
    } catch (error) {
      console.error("Failed to open save directory", error);
    }
  }, []);

  /**
   * saved_at文字列("20240115_123045"形式)を表示用の日時文字列に変換する
   */
  function formatSavedAt(raw: string): string {
    if (!raw || raw.length < 15) return raw;
    const y = raw.slice(0, 4);
    const m = raw.slice(4, 6);
    const d = raw.slice(6, 8);
    const h = raw.slice(9, 11);
    const min = raw.slice(11, 13);
    const s = raw.slice(13, 15);
    return `${y}/${m}/${d} ${h}:${min}:${s}`;
  }

  return (
    <div className="save-list-body" style={{ position: "relative" }}>
      <div className="save-list-header-actions">
        <button
          className="save-list-open-folder-btn"
          onClick={handleOpenFolder}
          type="button"
        >
          {t("saveList.openFolderButton")}
        </button>
      </div>

      {loading ? (
        <div className="save-list-loading">{t("saveList.loading")}</div>
      ) : saveEntries.length === 0 ? (
        <div className="save-list-empty">{t("saveList.noSaves")}</div>
      ) : (
        <div className="save-list-items">
          {saveEntries.map((entry) => {
            const isCurrent = entry.id === currentSaveId;
            return (
              <div
                key={entry.id}
                className={`save-list-card ${isCurrent ? "save-list-card--current" : ""}`}
              >
                <div className="save-list-card-header">
                  <span className="save-list-card-name">
                    {entry.city_name || `Save ${entry.id.slice(0, 8)}`}
                    {isCurrent && (
                      <span className="save-list-card-current-badge">
                        {t("saveList.currentSave")}
                      </span>
                    )}
                  </span>
                  <div className="save-list-card-actions">
                    <button
                      className="save-list-card-load-btn"
                      onClick={() => setConfirmSaveId(entry.id)}
                      disabled={busy}
                      type="button"
                    >
                      {t("saveList.loadButton")}
                    </button>
                    <button
                      className="save-list-card-delete-btn"
                      onClick={() => setConfirmDeleteId(entry.id)}
                      disabled={busy}
                      type="button"
                    >
                      {t("saveList.deleteButton")}
                    </button>
                  </div>
                </div>
                <div className="save-list-card-meta">
                  <span>
                    {t("saveList.population")}: {entry.population.toLocaleString()}
                  </span>
                  <span>
                    {t("saveList.inGameDate")}:{" "}
                    <EpochDate daysSinceEpoch={entry.current_date} />
                  </span>
                </div>
                <div className="save-list-card-savedat">
                  {t("saveList.savedAt")}: {formatSavedAt(entry.saved_at)}
                </div>
              </div>
            );
          })}
        </div>
      )}

      {status && (
        <div className={`save-list-status save-list-status--${status.type}`}>
          {status.text}
        </div>
      )}

      {confirmSaveId && (
        <div
          className="save-list-confirm-overlay"
          onMouseDown={() => setConfirmSaveId(null)}
        >
          <div
            className="save-list-confirm-box"
            onMouseDown={(e) => e.stopPropagation()}
          >
            <div className="save-list-confirm-text">
              {t("saveList.confirmLoad")}
            </div>
            <div className="save-list-confirm-actions">
              <button
                className="save-list-confirm-yes"
                onClick={() => void handleLoad(confirmSaveId)}
                type="button"
              >
                {t("saveList.confirm")}
              </button>
              <button
                onClick={() => setConfirmSaveId(null)}
                type="button"
              >
                {t("saveList.cancel")}
              </button>
            </div>
          </div>
        </div>
      )}

      {confirmDeleteId && (
        <div
          className="save-list-confirm-overlay"
          onMouseDown={() => setConfirmDeleteId(null)}
        >
          <div
            className="save-list-confirm-box"
            onMouseDown={(e) => e.stopPropagation()}
          >
            <div className="save-list-confirm-text">
              {t("saveList.confirmDelete")}
            </div>
            <div className="save-list-confirm-actions">
              <button
                className="save-list-confirm-delete"
                onClick={() => void handleDelete(confirmDeleteId)}
                type="button"
              >
                {t("saveList.deleteButton")}
              </button>
              <button
                onClick={() => setConfirmDeleteId(null)}
                type="button"
              >
                {t("saveList.cancel")}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
