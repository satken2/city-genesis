import { describeError } from "./errors";
import type { TFunction } from "i18next";
import { invoke } from "@tauri-apps/api/core";
import type { EpochReport } from "../components/taskbar/EpochResultWindow";
import type { QuestNotification } from "../types";
import { showUnlockedAchievements, takeUnlockedAchievements } from "./achievements";
import { buildApiUrl, fetchApi } from "./api";
import { beginEpochPresentation } from "./epochPresentation";
import { runEpochWithPresentation } from "./epochFlow";
import { sendSessionReload } from "./ipc";
import { resolveLocalizedText } from "./locale";
import { logInfoToFile } from "./log";
import { showNotification } from "./notification";

/** Maximum epochs per execution in normal play. Does not apply in scenario mode. */
export const MAX_EPOCH_COUNT = 10;

/** Maximum epochs per execution in scenario mode (safeguard against accidentally running tens of thousands). */
export const SCENARIO_MAX_EPOCH_COUNT = 10000;

/** Delay before Tips appear for an execution still running. */
const TIPS_DELAY_MS = 2000;

type EpochResponse = {
  epoch?: number;
  current_date: number;
  epoch_report?: EpochReport;
  completed_quests?: QuestNotification[];
  failed_quests?: QuestNotification[];
};

/**
 * Epoch execution outcome: completed means executed, even if results display failed, to prevent rerunning
 * completed work. failed means not executed (reason already notified). superseded means the city switched during
 * execution and results were not displayed.
 */
export type EpochExecutionOutcome = "completed" | "failed" | "superseded";

/**
 * Execute count epochs and show results. From start until results close, the HUD is disabled and Unity ignores world
 * input while the city stays visible. Start HTTP once Unity reports that its city cache matches the core, because it
 * renders from that cache without reading the core during execution. On completion, send a session reload to graphics
 * and open epoch_result if a report exists. Pass completed quests to results; without a report, open the quest
 * window directly for notify="modal" celebrations. Use popups for notify="toast" completions and deadline
 * failures. Closing results finishes the presentation; finish it here if no results window opens.
 *
 * On core rejection (403: Steam-unverified population limit; 409 EPOCH_ALREADY_RUNNING: another execution
 * active, etc.) or connection error, cancel presentation and translate the reason through the common error handler.
 * Call onStarted when presentation begins (execution is confirmed).
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
    await presentation.ready;
    if (!await presentation.isCurrent()) return "superseded";
    const url = buildApiUrl(apiBaseUrl, `/api/v1/epochs?count=${count}`);
    logInfoToFile(`epoch execution: POST ${url}`);
    const data = await runEpochWithPresentation(
      () => fetchApi<EpochResponse>(url, { method: "POST" }),
      new Promise<void>((resolve) => setTimeout(resolve, TIPS_DELAY_MS)),
      presentation.setTipsVisible,
    );
    simulationCompleted = true;
    if (!await presentation.isCurrent()) return "superseded";
    await sendSessionReload().catch(console.error);
    logInfoToFile(`epoch execution: done epoch=${data.epoch ?? "?"}`);

    const quests: QuestNotification[] = data.completed_quests ?? [];
    const achievements = await takeUnlockedAchievements(apiBaseUrl).catch((error: unknown) => {
      console.error("failed to take unlocked achievements", error);
      return [];
    });
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
            unlockedAchievements: achievements,
          },
        },
      });
      logInfoToFile("epoch execution: epoch_result window opened");
    } else {
      if (modalQuests.length > 0) {
        await invoke("open_registered_window", {
          windowType: "quest_complete",
          params: { extra: { quests, openedAt: Date.now() } },
        });
      }
      await showUnlockedAchievements(achievements).catch(console.error);
    }

    if (!data.epoch_report) await presentation.finish();
    return "completed";
  } catch (error) {
    console.error("failed to execute epoch", error);
    if (presentation && !await presentation.isCurrent().catch(() => true)) return "superseded";
    await presentation?.finish().catch(console.error);
    void showNotification({
      message: t("epochConfirm.errorFailed", { reason: describeError(error, t) }),
      durationMs: 8000,
    });
    return simulationCompleted ? "completed" : "failed";
  }
}
