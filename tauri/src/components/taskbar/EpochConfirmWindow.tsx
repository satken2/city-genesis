/**
 * エポック実行確認ウィンドウ
 *
 * シミュレーション実行前に現在のゲーム内日付と実行後の日付を表示し、
 * ユーザーの最終確認を行うポップアップウィンドウ。
 *
 * エポック数セレクターにより1〜10エポックの連続実行が可能。
 * 複数エポックを選択した場合は「実行後の日付」がその分だけ先の日付になる。
 *
 * 都市が大きくなるとエポック実行(POST /api/v1/epochs)に時間がかかりうるため、
 * 実行を確定したらこの確認ウィンドウは一旦 hide() で非表示にし、右下に「エポック実行中」
 * の通知を出す。実行(fetch)はこの非表示ウィンドウ内で継続させる(webviewは破棄しない)。
 * 実行中はウィンドウ間イベント `epoch:executing` をブロードキャストし、タスクバーの
 * ボタンを無効化させる(受信側は Taskbar.tsx)。
 *
 * エポック実行完了後は、結果や達成クエストを**別の独立したウィンドウ**として開く
 * (HUDから他のウィンドウを開くのと同じ open_registered_window 経由)。
 * こうすることで各ウィンドウは window-registry のサイズで Rust 側により中央配置される。
 * 1. epoch_result ウィンドウ … レスポンスに epoch_report が含まれる場合。達成クエストも引き継ぐ
 *    (結果を確認して閉じた後、notify="modal" のクエストがあればお祝いウィンドウが続く)。
 * 2. quest_complete ウィンドウ … レポートが無く notify="modal" のクエストが1件以上ある場合。
 * また、notify="toast" の達成クエストと期限切れで失敗したクエスト(failed_quests)は、
 * ウィンドウを開かず通知ポップアップ(showNotification)で軽く知らせる。
 * いずれの場合も、確認ウィンドウ自身は役目を終えて onClose で閉じる。
 *
 * バックエンドが実行を拒否した場合(403=Steam未認証のエポック上限、503=シャットダウン中 等)や
 * 通信エラーの場合は、通知とタスクバー無効化を解除して確認ウィンドウを再表示する。
 * エラーレスポンス時はさらに、その理由を通知ポップアップで表示する。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { emit } from "@tauri-apps/api/event";
import { invoke } from "@tauri-apps/api/core";
import { sendSessionReload } from "../../utils/ipc";
import { logInfoToFile } from "../../utils/log";
import { showNotification, dismissNotification } from "../../utils/notification";
import { formatEpochDate } from "../../utils/date";
import { resolveIntlLocale } from "../../utils/locale";
import type { QuestNotification } from "../../types";
import nextIcon from "../../assets/images/next.png";
import "./EpochConfirmWindow.css";

type CityData = {
  name: string;
  current_date: number;
  simulation_days: number;
};

function formatDate(daysSinceEpoch: number, locale: string): string {
  return formatEpochDate(daysSinceEpoch, locale);
}

type EpochConfirmWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

/** 選択可能なエポック数の上限 */
const MAX_EPOCH_COUNT = 10;

export function EpochConfirmWindow({ apiBaseUrl, onClose }: EpochConfirmWindowProps) {
  const { i18n, t } = useTranslation();
  const [city, setCity] = useState<CityData | null>(null);
  const [executing, setExecuting] = useState(false);
  const [epochCount, setEpochCount] = useState(1);

  useEffect(() => {
    const baseUrl = apiBaseUrl.replace(/\/+$/, "");
    fetch(`${baseUrl}/api/v1/city`)
      .then((res) => res.json())
      .then((data) => setCity(data.city))
      .catch(console.error);
  }, [apiBaseUrl]);

  const locale = resolveIntlLocale(i18n.language);
  const currentDateStr = city ? formatDate(city.current_date, locale) : "—";
  const nextDateStr = city
    ? formatDate(city.current_date + city.simulation_days * epochCount, locale)
    : "—";

  async function handleConfirm() {
    setExecuting(true);
    // このフローは失敗しても画面に何も出ないまま止まりうるため、
    // どの await まで到達したかをファイルログに記録して本番で追跡できるようにする。
    logInfoToFile(`epoch confirm: start count=${epochCount}`);
    // 実行中はタスクバーのボタンを無効化させるため、ウィンドウ間イベントを送る。
    await emit("epoch:executing", { executing: true });
    // 確認ウィンドウを一旦消し、右下に実行中通知を出す。
    // ウィンドウは破棄せず非表示にするだけなので、下の fetch はこのまま継続する。
    // durationMs を指定しないので、完了時にこちらが明示的に消すまで通知は表示され続ける。
    await getCurrentWindow().hide().catch((error) => {
      console.warn("failed to hide epoch_confirm window", error);
    });
    await showNotification({ message: t("epochConfirm.executing") });

    try {
      const baseUrl = apiBaseUrl.replace(/\/+$/, "");
      logInfoToFile(`epoch confirm: POST ${baseUrl}/api/v1/epochs?count=${epochCount}`);
      const res = await fetch(`${baseUrl}/api/v1/epochs?count=${epochCount}`, { method: "POST" });
      logInfoToFile(`epoch confirm: response status=${res.status}`);
      if (!res.ok) {
        // バックエンドが実行を拒否した場合。レポート無しの成功として扱うと結果ウィンドウも
        // エラーも出ない無言の失敗になるため、ここで理由を必ずユーザーに可視化する。
        const body = await res.json().catch(() => null);
        const reason = typeof body?.error === "string" ? body.error : `HTTP ${res.status}`;
        // console.error は main.tsx によりファイルログへERROR転送されるので本番でも追跡できる。
        console.error(`epoch confirm: rejected status=${res.status} reason=${reason}`);
        await dismissNotification();
        await emit("epoch:executing", { executing: false });
        await getCurrentWindow().show().catch((err) => {
          console.warn("failed to show epoch_confirm window", err);
        });
        // 403はSteam未認証時のエポック数上限に固有のステータスなので専用文言で案内する。
        void showNotification({
          message:
            res.status === 403
              ? t("epochConfirm.errorSteamLimit")
              : t("epochConfirm.errorFailed", { reason }),
          durationMs: 8000,
        });
        setExecuting(false);
        return;
      }
      const data = await res.json();
      await sendSessionReload();
      logInfoToFile(`epoch confirm: done epoch=${data.epoch ?? "?"}`);

      // 実行完了。通知を消し、タスクバーを再有効化する。
      await dismissNotification();
      await emit("epoch:executing", { executing: false });

      const quests: QuestNotification[] = data.completed_quests ?? [];
      const failedQuests: QuestNotification[] = data.failed_quests ?? [];
      const modalQuests = quests.filter((q) => q.notify === "modal");
      const toastQuests = quests.filter((q) => q.notify === "toast");

      // notify="toast" の達成クエストはウィンドウを開かず通知ポップアップで軽く知らせる。
      // 期限切れで失敗したクエストも同様に通知する(お祝いではないため常にトースト)。
      for (const q of toastQuests) {
        void showNotification({
          message: t("questComplete.toastCompleted", { name: q.name }),
          durationMs: 5000,
        });
      }
      for (const q of failedQuests) {
        void showNotification({
          message: t("questComplete.toastFailed", { name: q.name }),
          durationMs: 5000,
        });
      }

      // 結果レポートがあれば結果ウィンドウを新規に開く。達成クエストは結果ウィンドウを
      // 閉じた後に表示するため、一緒に渡して引き継がせる。
      if (data.epoch_report) {
        await invoke("open_registered_window", {
          windowType: "epoch_result",
          params: {
            extra: {
              report: data.epoch_report,
              epochCount,
              currentDate: Number(data.current_date),
              completedQuests: quests,
              failedQuests,
            },
          },
        });
        logInfoToFile("epoch confirm: epoch_result window opened");
      } else if (modalQuests.length > 0) {
        // レポートが無くお祝い対象のクエストだけある場合は、クエストウィンドウを直接開く。
        await invoke("open_registered_window", {
          windowType: "quest_complete",
          params: { extra: { quests } },
        });
      }

      // 確認ウィンドウは役目を終えたので閉じる。
      onClose();
    } catch (error) {
      console.error("failed to execute epoch", error);
      // 失敗時は通知を消し、タスクバーを再有効化したうえで確認ウィンドウを再表示し、
      // ユーザーが再操作できるようにする。
      await dismissNotification();
      await emit("epoch:executing", { executing: false });
      await getCurrentWindow().show().catch((err) => {
        console.warn("failed to show epoch_confirm window", err);
      });
      setExecuting(false);
    }
  }

  return (
    <div className="epoch-confirm-root">
      <div className="epoch-confirm-dates">
        <div className="epoch-confirm-date-block">
          <span className="epoch-confirm-date-label">
            {t("epochConfirm.currentDate")}
          </span>
          <span className="epoch-confirm-date-value">{currentDateStr}</span>
        </div>
        <img
          className="epoch-confirm-arrow-img"
          src={nextIcon}
          alt="→"
          draggable={false}
        />
        <div className="epoch-confirm-date-block">
          <span className="epoch-confirm-date-label">
            {t("epochConfirm.afterDate")}
          </span>
          <span className="epoch-confirm-date-value">{nextDateStr}</span>
        </div>
      </div>

      {/* エポック数セレクター: 1〜MAX_EPOCH_COUNT の範囲で連続実行数を指定できる */}
      <div className="epoch-confirm-count-row">
        <span className="epoch-confirm-count-label">{t("epochConfirm.epochCount")}</span>
        <div className="epoch-confirm-count-controls">
          <button
            className="epoch-confirm-count-btn"
            onClick={() => setEpochCount((c) => Math.max(1, c - 1))}
            disabled={executing || epochCount <= 1}
            type="button"
          >
            −
          </button>
          <span className="epoch-confirm-count-value">{epochCount}</span>
          <button
            className="epoch-confirm-count-btn"
            onClick={() => setEpochCount((c) => Math.min(MAX_EPOCH_COUNT, c + 1))}
            disabled={executing || epochCount >= MAX_EPOCH_COUNT}
            type="button"
          >
            ＋
          </button>
        </div>
      </div>

      <p className="epoch-confirm-message">{t("epochConfirm.message")}</p>

      <div className="epoch-confirm-buttons">
        <button
          className="epoch-confirm-btn epoch-confirm-no"
          onClick={onClose}
          disabled={executing}
          type="button"
        >
          {t("epochConfirm.no")}
        </button>
        <button
          className="epoch-confirm-btn epoch-confirm-yes"
          onClick={handleConfirm}
          disabled={executing}
          type="button"
        >
          {executing ? t("common.saving") : t("epochConfirm.yes")}
        </button>
      </div>
    </div>
  );
}
