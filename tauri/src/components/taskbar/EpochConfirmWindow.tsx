/**
 * エポック実行確認ウィンドウ
 *
 * シミュレーション実行前に現在のゲーム内日付と実行後の日付を表示し、
 * ユーザーの最終確認を行うポップアップウィンドウ。
 *
 * エポック数セレクターにより1〜10エポックの連続実行が可能。
 * 複数エポックを選択した場合は「実行後の日付」がその分だけ先の日付になる。
 * テンプレートモード(シナリオ制作)では上限を外し、数値を直接入力できる
 * (都市を育てるために数百エポックを一気に回す用途。1回のHTTPリクエストで完了まで待つ)。
 *
 * 確定直後にHTTP実行と日没演出を並行して開始する。確認ウィンドウは非表示で実行を維持する。
 * 日没1.5秒・暗転0.5秒の描画完了後、実行済みなら結果へ、未完了ならTipsへ進む。
 * Tipsは実行完了時に自動で閉じる。結果画面を閉じるまでは背景を黒く保ち、HUDも無効にする。
 * 朝への復帰は結果画面の共通クローズ処理が担当する(×・閉じるボタンの両方)。
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
 * 通信エラーの場合は、演出とタスクバー無効化を解除して確認ウィンドウを再表示する。
 * エラーレスポンス時はさらに、その理由を通知ポップアップで表示する。
 */

import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { invoke } from "@tauri-apps/api/core";
import { sendSessionReload } from "../../utils/ipc";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { beginEpochPresentation } from "../../utils/epochPresentation";
import { runEpochWithPresentation } from "../../utils/epochFlow";
import type { EpochReport } from "./EpochResultWindow";
import { logInfoToFile } from "../../utils/log";
import { showNotification } from "../../utils/notification";
import { formatEpochDate } from "../../utils/date";
import { resolveIntlLocale } from "../../utils/locale";
import { useTemplateMode } from "../../hooks/useTemplateMode";
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

type EpochResponse = {
  epoch?: number;
  current_date: number;
  epoch_report?: EpochReport;
  completed_quests?: QuestNotification[];
  failed_quests?: QuestNotification[];
};

/** 選択可能なエポック数の上限(通常プレイ)。テンプレートモードでは適用しない */
const MAX_EPOCH_COUNT = 10;

/** テンプレートモードで直接入力できるエポック数の上限(誤入力で数万エポック回すのを防ぐ安全弁) */
const TEMPLATE_MAX_EPOCH_COUNT = 10000;

export function EpochConfirmWindow({ apiBaseUrl, onClose }: EpochConfirmWindowProps) {
  const { i18n, t } = useTranslation();
  const [city, setCity] = useState<CityData | null>(null);
  const [executing, setExecuting] = useState(false);
  const executingRef = useRef(false);
  const [epochCount, setEpochCount] = useState(1);
  const templateMode = useTemplateMode(apiBaseUrl);
  const maxEpochCount = templateMode ? TEMPLATE_MAX_EPOCH_COUNT : MAX_EPOCH_COUNT;

  /** 直接入力(テンプレートモード)。空欄や範囲外は1〜上限に丸める */
  function handleCountInput(raw: string) {
    const parsed = Number.parseInt(raw, 10);
    if (Number.isNaN(parsed)) {
      setEpochCount(1);
      return;
    }
    setEpochCount(Math.max(1, Math.min(maxEpochCount, parsed)));
  }

  useEffect(() => {
    fetchApi<{ city: CityData }>(buildApiUrl(apiBaseUrl, "/api/v1/city"))
      .then((data) => setCity(data.city))
      .catch(console.error);
  }, [apiBaseUrl]);

  const locale = resolveIntlLocale(i18n.language);
  const currentDateStr = city ? formatDate(city.current_date, locale) : "—";
  const nextDateStr = city
    ? formatDate(city.current_date + city.simulation_days * epochCount, locale)
    : "—";

  async function handleConfirm() {
    if (executingRef.current) return;
    executingRef.current = true;
    setExecuting(true);
    logInfoToFile(`epoch confirm: start count=${epochCount}`);
    let presentation: Awaited<ReturnType<typeof beginEpochPresentation>> | undefined;
    let simulationCompleted = false;
    try {
      presentation = await beginEpochPresentation();
      void getCurrentWindow().hide().catch(console.error);
      const url = buildApiUrl(apiBaseUrl, `/api/v1/epochs?count=${epochCount}`);
      logInfoToFile(`epoch confirm: POST ${url}`);
      const data = await runEpochWithPresentation(
        () => fetchApi<EpochResponse>(url, { method: "POST" }),
        presentation.darkened,
        presentation.setTipsVisible,
      );
      simulationCompleted = true;
      if (!await presentation.isCurrent()) return;
      await sendSessionReload().catch(console.error);
      logInfoToFile(`epoch confirm: done epoch=${data.epoch ?? "?"}`);

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
              epochPresentationId: presentation.id,
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
          params: { extra: { quests, openedAt: Date.now() } },
        });
      }

      if (!data.epoch_report) await presentation.finish();

      // 確認ウィンドウは役目を終えたので閉じる。
      onClose();
    } catch (error) {
      console.error("failed to execute epoch", error);
      if (presentation && !await presentation.isCurrent().catch(() => true)) return;
      await presentation?.finish().catch(console.error);
      // 実行済みの処理を、結果表示の失敗だけで再実行させない。
      if (simulationCompleted) onClose();
      else await getCurrentWindow().show().catch(console.error);
      void showNotification({
        message: error instanceof ApiError && error.status === 403
          ? t("epochConfirm.errorSteamLimit")
          : t("epochConfirm.errorFailed", { reason: error instanceof Error ? error.message : String(error) }),
        durationMs: 8000,
      });
      setExecuting(false);
      executingRef.current = false;
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

      {/* エポック数セレクター: 1〜MAX_EPOCH_COUNT の範囲で連続実行数を指定できる(テンプレートモードは上限なし・直接入力可) */}
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
          {templateMode ? (
            <input
              className="epoch-confirm-count-input"
              type="number"
              min={1}
              max={TEMPLATE_MAX_EPOCH_COUNT}
              value={epochCount}
              disabled={executing}
              onChange={(e) => handleCountInput(e.target.value)}
            />
          ) : (
            <span className="epoch-confirm-count-value">{epochCount}</span>
          )}
          <button
            className="epoch-confirm-count-btn"
            onClick={() => setEpochCount((c) => Math.min(maxEpochCount, c + 1))}
            disabled={executing || epochCount >= maxEpochCount}
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
