import type { TFunction } from "i18next";
import { invoke } from "@tauri-apps/api/core";
import type { EpochReport } from "../components/taskbar/EpochResultWindow";
import type { QuestNotification } from "../types";
import { ApiError, buildApiUrl, fetchApi } from "./api";
import { beginEpochPresentation } from "./epochPresentation";
import { runEpochWithPresentation } from "./epochFlow";
import { sendSessionReload } from "./ipc";
import { resolveLocalizedText } from "./locale";
import { logInfoToFile } from "./log";
import { showNotification } from "./notification";

/** 一度に実行できるエポック数の上限(通常プレイ)。テンプレートモードでは適用しない */
export const MAX_EPOCH_COUNT = 10;

/** テンプレートモードで一度に実行できるエポック数の上限(誤入力で数万エポック回すのを防ぐ安全弁) */
export const TEMPLATE_MAX_EPOCH_COUNT = 10000;

type EpochResponse = {
  epoch?: number;
  current_date: number;
  epoch_report?: EpochReport;
  completed_quests?: QuestNotification[];
  failed_quests?: QuestNotification[];
};

/**
 * エポック実行の結末。completed は実行済み(結果表示に失敗した場合も含む。実行済みの処理を再実行させないため)、
 * failed は実行されなかった(理由は通知済み)、superseded は実行中に別の都市へ切り替わり、結果を出さなかった。
 */
export type EpochExecutionOutcome = "completed" | "failed" | "superseded";

/**
 * count エポックを日没・暗転の演出つきで実行し、結果を表示する。
 *
 * HTTP の実行と演出を並行して始め、暗転後もまだ実行中なら Tips を出す。完了したら描画へセッションの
 * 再読み込みを送り、結果レポートがあれば結果ウィンドウ(epoch_result)を開く。達成クエストは結果ウィンドウへ
 * 引き継ぎ、レポートが無くお祝い対象(notify="modal")があればクエストウィンドウを直接開く。
 * notify="toast" の達成と期限切れの失敗は通知ポップアップで知らせる。朝への復帰は結果ウィンドウを閉じる処理が担当し、
 * 結果ウィンドウを開かないときはここで演出を終える。
 *
 * コアが実行を拒否した場合(403=Steam未認証のエポック上限、409 EPOCH_ALREADY_RUNNING=別の実行の最中 等)や
 * 通信エラーでは、演出を解除して理由を通知する。コアの本文は英語固定なので、理由の分かる拒否は専用の翻訳文にする。
 * onStarted は演出を始めた(実行を確定した)ときに呼ばれる。
 */
export async function executeEpochs(options: {
  apiBaseUrl: string;
  count: number;
  t: TFunction;
  language: string;
  onStarted?: () => void;
}): Promise<EpochExecutionOutcome> {
  const { apiBaseUrl, count, t, language } = options;
  logInfoToFile(`epoch execution: start count=${count}`);
  let presentation: Awaited<ReturnType<typeof beginEpochPresentation>> | undefined;
  let simulationCompleted = false;
  try {
    presentation = await beginEpochPresentation();
    options.onStarted?.();
    const url = buildApiUrl(apiBaseUrl, `/api/v1/epochs?count=${count}`);
    logInfoToFile(`epoch execution: POST ${url}`);
    const data = await runEpochWithPresentation(
      () => fetchApi<EpochResponse>(url, { method: "POST" }),
      presentation.darkened,
      presentation.setTipsVisible,
    );
    simulationCompleted = true;
    if (!await presentation.isCurrent()) return "superseded";
    await sendSessionReload().catch(console.error);
    logInfoToFile(`epoch execution: done epoch=${data.epoch ?? "?"}`);

    const quests: QuestNotification[] = data.completed_quests ?? [];
    const failedQuests: QuestNotification[] = data.failed_quests ?? [];
    const modalQuests = quests.filter((q) => q.notify === "modal");
    const toastQuests = quests.filter((q) => q.notify === "toast");
    for (const q of toastQuests) {
      void showNotification({
        message: t("questComplete.toastCompleted", { name: resolveLocalizedText(q.name, language) }),
        durationMs: 5000,
      });
    }
    for (const q of failedQuests) {
      void showNotification({
        message: t("questComplete.toastFailed", { name: resolveLocalizedText(q.name, language) }),
        durationMs: 5000,
      });
    }

    if (data.epoch_report) {
      await invoke("open_registered_window", {
        windowType: "epoch_result",
        params: {
          extra: {
            epochPresentationId: presentation.id,
            report: data.epoch_report,
            epochCount: count,
            currentDate: Number(data.current_date),
            completedQuests: quests,
            failedQuests,
          },
        },
      });
      logInfoToFile("epoch execution: epoch_result window opened");
    } else if (modalQuests.length > 0) {
      await invoke("open_registered_window", {
        windowType: "quest_complete",
        params: { extra: { quests, openedAt: Date.now() } },
      });
    }

    if (!data.epoch_report) await presentation.finish();
    return "completed";
  } catch (error) {
    console.error("failed to execute epoch", error);
    if (presentation && !await presentation.isCurrent().catch(() => true)) return "superseded";
    await presentation?.finish().catch(console.error);
    void showNotification({
      message: error instanceof ApiError && error.status === 403
        ? t("epochConfirm.errorSteamLimit")
        : error instanceof ApiError && error.code === "EPOCH_ALREADY_RUNNING"
          ? t("epochConfirm.errorAlreadyRunning")
          : t("epochConfirm.errorFailed", { reason: error instanceof Error ? error.message : String(error) }),
      durationMs: 8000,
    });
    return simulationCompleted ? "completed" : "failed";
  }
}
