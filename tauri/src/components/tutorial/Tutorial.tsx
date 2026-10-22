/**
 * Tutorial window component
 *
 * Step-based tutorial shown immediately after starting a new game.
 * The dedicated `useTutorialGuide` manages instructions and completion checks. Next verifies conditions and
 * advances when satisfied.
 *
 * - Simple hint-bar-like display without a title bar or close button.
 * - Fixed at top center below the hint bar.
 * - Text and buttons only, no images.
 * - Progress persists in localStorage per save ID.
 * - No Quit button; players can exit/save through the system tray.
 * - After all steps, show completion. Finish exits scenario mode and hides this window, preserving the city for
 *   normal play.
 *
 * Visibility:
 * - Rust controls display through tutorial.show() and tutorial:show.
 * - On game exit, Rust calls hide() and emits tutorial:hide.
 * - session-state supplies only API URL/save ID; repeated polling events with the same save ID do not restore
 *   progress.
 */
import { PLAY_MODE_NORMAL } from "../../constants";


import { useEffect, useMemo, useState, useCallback, useRef } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { listen } from "@tauri-apps/api/event";
import { invoke } from "@tauri-apps/api/core";
import { TUTORIAL_SCENARIOS } from "./tutorialSteps";
import { useTutorialGuide } from "../../tutorial/useTutorialGuide";
import { fetchApi } from "../../utils/api";
import { showNotification } from "../../utils/notification";
import type { InfoDialogContent } from "../common/InfoDialogWindow";
import "./Tutorial.css";

/** localStorage key prefix for tutorial progress */
const TUTORIAL_STORAGE_KEY_PREFIX = "tutorial_step_";

/** Key prefix recording dismissal of the opening introduction, per scenario/save ID */
const TUTORIAL_INTRO_KEY_PREFIX = "tutorial_intro_seen_";

/**
 * Return the localStorage key for a save ID.
 */
function getStorageKey(saveId: string, tutorialId: string): string {
  return `${TUTORIAL_STORAGE_KEY_PREFIX}${tutorialId}_${saveId}`;
}

/**
 * Read tutorial progress from localStorage.
 * Return step 0 before the save ID is known.
 */
function loadProgress(saveId: string | null, tutorialId: string, totalSteps: number): number {
  if (!saveId) return 0;
  const stored = localStorage.getItem(getStorageKey(saveId, tutorialId));
  if (stored !== null) {
    const parsed = parseInt(stored, 10);
    if (!isNaN(parsed) && parsed >= 0 && parsed < totalSteps) {
      return parsed;
    }
  }
  return 0;
}

/**
 * Save tutorial progress to localStorage.
 */
function saveProgress(saveId: string | null, tutorialId: string, step: number): void {
  if (!saveId) return;
  localStorage.setItem(getStorageKey(saveId, tutorialId), String(step));
}

/**
 * Remove tutorial progress from localStorage on completion.
 */
function clearProgress(saveId: string | null, tutorialId: string): void {
  if (!saveId) return;
  localStorage.removeItem(getStorageKey(saveId, tutorialId));
}

/** Session state event payload type */
type SessionStatePayload = {
  current_save_id: string | null;
  apiBaseUrl?: string;
  tutorialId?: string | null;
};

/** Completion button validation state */
type CheckState = "idle" | "checking" | "failed" | "success";

export function Tutorial() {
  const { t } = useTranslation();
  const appWindow = useMemo(() => getCurrentWindow(), []);
  const [tutorialId, setTutorialId] = useState<string>("controls");
  const [currentStep, setCurrentStep] = useState(0);
  const [checkState, setCheckState] = useState<CheckState>("idle");
  const [apiBaseUrl, setApiBaseUrl] = useState("");
  const [saveId, setSaveId] = useState<string | null>(null);
  const [completed, setCompleted] = useState(false);
  const [visible, setVisible] = useState(false);
  /** Whether the completion message is shown after all steps */
  const [showComplete, setShowComplete] = useState(false);
  /** Whether Finish is being sent to exit scenario mode after completion */
  const [finishing, setFinishing] = useState(false);
  /** Whether the opening introduction was dismissed. Do not start guidance or control restrictions before dismissal. */
  const [introDone, setIntroDone] = useState(true);
  const introOpenedRef = useRef<string | null>(null);

  const currentScenario = useMemo(() => {
    return TUTORIAL_SCENARIOS.find((s) => s.id === tutorialId) || TUTORIAL_SCENARIOS[0];
  }, [tutorialId]);

  const steps = currentScenario.steps;
  const totalSteps = steps.length;
  const guide = useTutorialGuide(visible && !completed && !showComplete && introDone, tutorialId, currentStep + 1, saveId, apiBaseUrl);

  /**
   * Ref holding the previous save ID.
   * Polling repeatedly emits session-state with the same ID; restore progress only when the ID actually changes.
   */
  const prevSaveIdRef = useRef<string | null>(null);

  /** Open the introduction once per session. Start guidance after dismissal through Start or ×. */
  useEffect(() => {
    if (!visible || introDone || !saveId || completed) return;
    const key = `${saveId}_${tutorialId}`;
    if (introOpenedRef.current === key) return;
    introOpenedRef.current = key;
    const content: InfoDialogContent = {
      titleKey: "tutorial.intro.windowTitle",
      image: currentScenario.image,
      headingKey: currentScenario.titleKey,
      subheadingKey: "tutorial.intro.heading",
      textKey: currentScenario.introKey,
      buttonKey: "tutorial.startButton",
    };
    void invoke("open_registered_window", { windowType: "info_dialog", params: { extra: content } }).catch(console.error);
  }, [visible, introDone, saveId, tutorialId, completed, currentScenario]);

  useEffect(() => {
    if (introDone || !saveId) return;
    let stop: (() => void) | undefined, disposed = false;
    void listen<{ window: string; visible: boolean }>("tutorial:window_visibility", (event) => {
      if (event.payload.window !== "info_dialog" || event.payload.visible) return;
      localStorage.setItem(`${TUTORIAL_INTRO_KEY_PREFIX}${tutorialId}_${saveId}`, "1");
      setIntroDone(true);
    }).then((fn) => { if (disposed) fn(); else stop = fn; });
    return () => { disposed = true; stop?.(); };
  }, [introDone, saveId, tutorialId]);

  useEffect(() => {
    let unlistenSession: (() => void) | undefined;

    // Get API URL and save ID from the session state event.
    // Events arrive periodically through polling; restore progress only when the save ID changes,
    // retaining existing UI state for repeated events with the same ID.
    appWindow
      .listen<SessionStatePayload>("session-state", (event) => {
        const { current_save_id, tutorialId: nextTutorialId } = event.payload;

        let activeTutorialId = tutorialId;
        if (nextTutorialId) {
          setTutorialId(nextTutorialId);
          activeTutorialId = nextTutorialId;
        }

        const scenario = TUTORIAL_SCENARIOS.find((s) => s.id === activeTutorialId) || TUTORIAL_SCENARIOS[0];
        const activeTotalSteps = scenario.steps.length;

        if (current_save_id) {
          setSaveId(current_save_id);

          // Restore progress only when the save ID changes.
          if (current_save_id !== prevSaveIdRef.current) {
            prevSaveIdRef.current = current_save_id;
            setFinishing(false);
            setCheckState("idle");
            const storedStep = loadProgress(current_save_id, activeTutorialId, activeTotalSteps);
            if (storedStep >= activeTotalSteps) {
              // Hide immediately if already completed.
              setCompleted(true);
              setVisible(false);
              appWindow.hide().catch(() => {});
            } else {
              setCurrentStep(storedStep);
              setCompleted(false);
              setShowComplete(false);
              // Show the introduction only when starting from the first step, not when resuming midway.
              setIntroDone(storedStep > 0 || localStorage.getItem(`${TUTORIAL_INTRO_KEY_PREFIX}${activeTutorialId}_${current_save_id}`) === "1");
            }
          }
        } else {
          prevSaveIdRef.current = null;
          setSaveId(null);
          setVisible(false);
          setFinishing(false);
          setCheckState("idle");
        }

        // Get API URL from the session payload.
        const url = (event.payload as Record<string, unknown>).apiBaseUrl;
        if (typeof url === "string" && url) {
          setApiBaseUrl(url.replace(/\/+$/, ""));
        }
      })
      .then((fn) => {
        unlistenSession = fn;
      })
      .catch(console.error);

    // Receive visibility changes through Tauri's tutorial:show / tutorial:hide events.
    // Besides Rust directly calling show() / hide(),
    // also allow event-based visibility control.
    let unlistenShow: (() => void) | undefined;
    let unlistenHide: (() => void) | undefined;

    appWindow
      .listen<string>("tutorial:show", (event) => {
        if (event.payload) {
          setTutorialId(event.payload);
        }
        if (!completed) {
          setVisible(true);
        } else {
          // Hide the completed tutorial again so its transparent window cannot intercept clicks at the top of the screen.
          appWindow.hide().catch(() => {});
        }
      })
      .then((fn) => {
        unlistenShow = fn;
      })
      .catch(console.error);

    appWindow
      .listen("tutorial:hide", () => {
        setVisible(false);
      })
      .then((fn) => {
        unlistenHide = fn;
      })
      .catch(console.error);

    return () => {
      unlistenSession?.();
      unlistenShow?.();
      unlistenHide?.();
    };
  }, [appWindow, completed]);

  /** Next validation. The progression manager checks conditions and performs delegated actions such as skipping construction time. */
  const handleComplete = useCallback(async () => {
    if (checkState === "checking" || !apiBaseUrl || !guide.ready) return;

    const step = steps[currentStep];
    if (!step) return;

    setCheckState("checking");
    try {
      // During page transitions, advance only to the next phase without completing the step.
      if (await guide.advance()) {
        setCheckState("idle");
        return;
      }
      const passed = await guide.check();
      if (passed) {
        setCheckState("success");
        const nextStep = currentStep + 1;

        if (nextStep >= totalSteps) {
          // All steps complete → show the completion message.
          clearProgress(saveId, tutorialId);

          // Complete the backend tutorial achievement quest (forced-completion type).
          try {
            const res = await fetch(`${apiBaseUrl}/api/v1/quests`);
            if (res.ok) {
              const data = await res.json();
              const quests = data.quests || [];
              const tutorialQuest = quests.find((q: any) => q.is_force_completable);
              if (tutorialQuest && tutorialQuest.status !== "completed") {
                await fetch(`${apiBaseUrl}/api/v1/quests/${tutorialQuest.id}/complete`, {
                  method: "PUT",
                });

                // Show the quest completion celebration window.
                // QuestCompleteWindow displays only notify="modal", so
                // pass the GET /quests quest directly as the notification payload,
                // including fields such as notify/tier/achieved_values.
                await invoke("open_registered_window", {
                  windowType: "quest_complete",
                  params: {
                    extra: {
                      quests: [{ ...tutorialQuest, notify: "modal", status: "completed" }],
                      openedAt: Date.now(),
                    },
                  },
                });
              }
            }
          } catch (e) {
            console.error("Failed to complete tutorial quest on backend:", e);
          }

          setCompleted(true);
          setShowComplete(true);
        } else {
          // Advance to the next step.
          saveProgress(saveId, tutorialId, nextStep);
          setCurrentStep(nextStep);
          setCheckState("idle");
        }
      } else {
        setCheckState("failed");
        // Show the failure message for three seconds, then reset.
        setTimeout(() => setCheckState("idle"), 3000);
      }
    } catch {
      setCheckState("failed");
      setTimeout(() => setCheckState("idle"), 3000);
    }
  }, [checkState, apiBaseUrl, currentStep, saveId, guide, steps, tutorialId, totalSteps]);

  /**
   * Finish handler on the completion message.
   * The tutorial city runs in scenario mode to support delegated operations such as construction time skips.
   * Exit that mode before hiding this window, allowing the city to continue in normal play.
   */
  const handleFinish = useCallback(async () => {
    if (finishing || !apiBaseUrl) return;
    setFinishing(true);
    try {
      await fetchApi(`${apiBaseUrl}/api/v1/sessions`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ play_mode: PLAY_MODE_NORMAL }),
      });
      setVisible(false);
      await appWindow.hide().catch(() => {});
    } catch {
      void showNotification({ message: t("tutorial.guide.finishFailed") });
    } finally {
      setFinishing(false);
    }
  }, [finishing, apiBaseUrl, appWindow, t]);

  if (!visible || !introDone) {
    return null;
  }

  /** Render progress dots at the bottom inside the frame. */
  const renderProgress = () => (
    <div className="tutorial-progress">
      {Array.from({ length: totalSteps }, (_, i) => (
        <div
          key={i}
          className={`tutorial-progress-dot ${
            i < currentStep
              ? "tutorial-progress-dot--completed"
              : i === currentStep
                ? "tutorial-progress-dot--active"
                : ""
          }`}
        />
      ))}
    </div>
  );

  // Completion message after all steps
  if (showComplete) {
    return (
      <div className="tutorial-root">
        <div className="tutorial-frame">
          <div className="tutorial-frame-content">
            <div className="tutorial-step-content">
              <div className="tutorial-step-title">
                {t("tutorial.complete.title")}
              </div>
              <div className="tutorial-step-description">
                {t("tutorial.complete.description")}
              </div>
              <div className="tutorial-actions">
                <button
                  className="tutorial-complete-button"
                  onClick={() => void handleFinish()}
                  disabled={finishing}
                  type="button"
                >
                  {t("tutorial.complete.button")}
                </button>
              </div>
            </div>
          </div>
        </div>
      </div>
    );
  }

  const step = steps[currentStep];

  return (
    <div className="tutorial-root">
      <div className="tutorial-frame">
        <div className="tutorial-frame-content">
          <div className="tutorial-step-content">
            <div className="tutorial-step-number">
              {t("tutorial.stepProgress", { current: step?.id ?? 0, total: totalSteps })}
            </div>
            <div className="tutorial-step-title">
              {step ? t(step.titleKey) : ""}
            </div>
            <div className="tutorial-step-description" key={guide.failed ? "failed" : guide.textKey || step?.descriptionKey}>
              {guide.failed ? t("tutorial.guide.failed") : guide.textKey ? t(guide.textKey) : step ? t(step.descriptionKey) : ""}
            </div>
            <div className="tutorial-actions">
              {guide.failed && <button className="tutorial-complete-button" type="button" onClick={guide.retry}>{t("tutorial.guide.retry")}</button>}
              {checkState === "failed" && (
                <span className="tutorial-status tutorial-status--error">
                  {t("tutorial.failed")}
                </span>
              )}
              {checkState === "success" && (
                <span className="tutorial-status tutorial-status--success">
                  {t("tutorial.completed")}
                </span>
              )}
              <button
                className="tutorial-complete-button"
                onClick={() => void handleComplete()}
                disabled={checkState === "checking" || !guide.ready}
                type="button"
              >
                {checkState === "checking"
                  ? t("tutorial.checking")
                  : t("tutorial.nextButton")}
              </button>
            </div>
            {import.meta.env.DEV && guide.managed && <details className="tutorial-guide-debug"><summary>Guide</summary>{guide.debug}</details>}
          </div>
        </div>
        {renderProgress()}
      </div>
    </div>
  );
}
