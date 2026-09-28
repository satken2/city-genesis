/**
 * チュートリアルウィンドウコンポーネント
 *
 * 新規ゲーム開始直後に表示されるステップ型チュートリアル。
 * 各ステップの案内と完了判定は専用の進行管理(`useTutorialGuide`)が担い、
 * 「次へ」ボタン押下時に条件を確認して、満たしていれば次のステップへ進む。
 *
 * - タイトルバーや×ボタンを持たない、ヒントバー風のシンプルな表示
 * - 画面上部中央に固定され、ヒントバーの下に配置される
 * - 左側に正方形の画像、右側に説明文とボタンを配置
 * - 進捗はセーブ ID ごとに localStorage に保持する
 * - 途中でやめるボタンは置かない(システムトレイから終了・保存できる)
 * - 全ステップ完了で完了メッセージを表示し、「終了」ボタンでテンプレートモードを解除して
 *   このウィンドウを隠す。都市はそのまま通常のプレイとして続けられる
 *
 * 表示・非表示の制御:
 * - ウィンドウの表示は Rust 側で制御する（tutorial.show() + tutorial:show イベント）
 * - ゲーム終了時は Rust 側で hide() + tutorial:hide イベントを発行
 * - session-state イベントは API URL とセーブ ID の取得にのみ利用し、
 *   ポーリングによる同一セーブ ID のイベントでは進捗を復元しない
 */

import { useEffect, useMemo, useState, useCallback, useRef } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { listen } from "@tauri-apps/api/event";
import { invoke } from "@tauri-apps/api/core";
import { TUTORIAL_SCENARIOS } from "./tutorialSteps";
import { useTutorialGuide } from "../../tutorial/useTutorialGuide";
import { fetchApi } from "../../utils/api";
import { showNotification } from "../../utils/notification";
import "./Tutorial.css";

/** localStorage にチュートリアル進捗を保存する際のキーのプレフィックス */
const TUTORIAL_STORAGE_KEY_PREFIX = "tutorial_step_";

/** 開始時の説明ウィンドウを閉じたことを記録するキーのプレフィックス(シナリオ・セーブID単位) */
const TUTORIAL_INTRO_KEY_PREFIX = "tutorial_intro_seen_";

/** localStorage にチュートリアル完了フラグを保存する際のキー。
 * セーブ ID に依存しないグローバルなフラグで、
 * ニューゲームウィンドウでシナリオ選択のロック解除判定に使用する。 */
const TUTORIAL_CLEARED_KEY = "tutorial_cleared";

/**
 * 指定されたセーブ ID に対応する localStorage キーを返す
 */
function getStorageKey(saveId: string, tutorialId: string): string {
  return `${TUTORIAL_STORAGE_KEY_PREFIX}${tutorialId}_${saveId}`;
}

/**
 * localStorage からチュートリアル進捗を読み込む
 * セーブ ID が未確定の場合はステップ 0 を返す
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
 * localStorage にチュートリアル進捗を保存する
 */
function saveProgress(saveId: string | null, tutorialId: string, step: number): void {
  if (!saveId) return;
  localStorage.setItem(getStorageKey(saveId, tutorialId), String(step));
}

/**
 * チュートリアル完了時に localStorage から進捗を削除する
 */
function clearProgress(saveId: string | null, tutorialId: string): void {
  if (!saveId) return;
  localStorage.removeItem(getStorageKey(saveId, tutorialId));
}

/** セッション状態イベントのペイロード型 */
type SessionStatePayload = {
  current_save_id: string | null;
  apiBaseUrl?: string;
  tutorialId?: string | null;
};

/** 完了ボタンの検証状態 */
type CheckState = "idle" | "checking" | "failed" | "success";

export function Tutorial() {
  const { t } = useTranslation();
  const appWindow = useMemo(() => getCurrentWindow(), []);
  const [tutorialId, setTutorialId] = useState<string>("basic");
  const [currentStep, setCurrentStep] = useState(0);
  const [checkState, setCheckState] = useState<CheckState>("idle");
  const [apiBaseUrl, setApiBaseUrl] = useState("");
  const [saveId, setSaveId] = useState<string | null>(null);
  const [completed, setCompleted] = useState(false);
  const [visible, setVisible] = useState(false);
  /** 全ステップ完了後の完了メッセージ表示中かどうか */
  const [showComplete, setShowComplete] = useState(false);
  /** 完了後の「終了」(テンプレートモードの解除)を送信中かどうか */
  const [finishing, setFinishing] = useState(false);
  /** 開始時の説明ウィンドウを閉じたか。閉じるまでは案内と操作制限を始めない */
  const [introDone, setIntroDone] = useState(true);
  const introOpenedRef = useRef<string | null>(null);

  const currentScenario = useMemo(() => {
    return TUTORIAL_SCENARIOS.find((s) => s.id === tutorialId) || TUTORIAL_SCENARIOS[0];
  }, [tutorialId]);

  const steps = currentScenario.steps;
  const totalSteps = steps.length;
  const guide = useTutorialGuide(visible && !completed && !showComplete && introDone, tutorialId, currentStep + 1, saveId, apiBaseUrl);

  /**
   * 前回のセーブ ID を保持する ref。
   * ポーリングで同一セーブ ID の session-state イベントが何度も飛んでくるため、
   * セーブ ID が実際に変化した時だけ進捗を復元するために使用する。
   */
  const prevSaveIdRef = useRef<string | null>(null);

  /** 説明ウィンドウを1セッションにつき1回開く。閉じられたら(開始ボタン・×とも)案内を始める */
  useEffect(() => {
    if (!visible || introDone || !saveId || completed) return;
    const key = `${saveId}_${tutorialId}`;
    if (introOpenedRef.current === key) return;
    introOpenedRef.current = key;
    void invoke("open_registered_window", { windowType: "tutorial_intro", params: { extra: { tutorialId } } }).catch(console.error);
  }, [visible, introDone, saveId, tutorialId, completed]);

  useEffect(() => {
    if (introDone || !saveId) return;
    let stop: (() => void) | undefined, disposed = false;
    void listen<{ window: string; visible: boolean }>("tutorial:window_visibility", (event) => {
      if (event.payload.window !== "tutorial_intro" || event.payload.visible) return;
      localStorage.setItem(`${TUTORIAL_INTRO_KEY_PREFIX}${tutorialId}_${saveId}`, "1");
      setIntroDone(true);
    }).then((fn) => { if (disposed) fn(); else stop = fn; });
    return () => { disposed = true; stop?.(); };
  }, [introDone, saveId, tutorialId]);

  useEffect(() => {
    let unlistenSession: (() => void) | undefined;

    // セッション状態イベントから API URL とセーブ ID を取得する。
    // ポーリングで定期的に飛んでくるため、セーブ ID が変化した場合のみ
    // 進捗の復元を行う。同一セーブ ID のポーリングでは既存の UI 状態を保持する。
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

          // セーブ ID が変化した場合のみ進捗を復元する
          if (current_save_id !== prevSaveIdRef.current) {
            prevSaveIdRef.current = current_save_id;
            setFinishing(false);
            setCheckState("idle");
            const storedStep = loadProgress(current_save_id, activeTutorialId, activeTotalSteps);
            if (storedStep >= activeTotalSteps) {
              // 既に完了している場合は即座に非表示にする
              setCompleted(true);
              setVisible(false);
              appWindow.hide().catch(() => {});
            } else {
              setCurrentStep(storedStep);
              setCompleted(false);
              setShowComplete(false);
              // 説明ウィンドウは最初のステップから始めるときだけ出す(途中から再開したときは出さない)。
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

        // API URL はセッションペイロードから取得する
        const url = (event.payload as Record<string, unknown>).apiBaseUrl;
        if (typeof url === "string" && url) {
          setApiBaseUrl(url.replace(/\/+$/, ""));
        }
      })
      .then((fn) => {
        unlistenSession = fn;
      })
      .catch(console.error);

    // Tauri 側から tutorial:show / tutorial:hide イベントで
    // 表示制御を受け取る。Rust 側で show() / hide() を直接呼ぶ他に、
    // イベント経由でも制御できるようにする。
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
          // 終えたチュートリアルの透明なウィンドウが画面上部のクリックを吸わないよう隠し直す。
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

  /** 「次へ」ボタン押下時の検証処理。条件の確認と工期スキップ等の代行は進行管理が行う */
  const handleComplete = useCallback(async () => {
    if (checkState === "checking" || !apiBaseUrl || !guide.ready) return;

    const step = steps[currentStep];
    if (!step) return;

    setCheckState("checking");
    try {
      const passed = await guide.check();
      if (passed) {
        setCheckState("success");
        const nextStep = currentStep + 1;

        if (nextStep >= totalSteps) {
          // 全ステップ完了 → 完了メッセージを表示する
          clearProgress(saveId, tutorialId);
          localStorage.setItem(`${TUTORIAL_CLEARED_KEY}_${tutorialId}`, "true");

          // バックエンドのチュートリアル達成クエスト(強制完了型)を完了にする
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

                // クエスト達成おめでとうウィンドウを表示する。
                // QuestCompleteWindow は notify="modal" のみ表示するため、
                // GET /quests のクエストをそのまま通知ペイロードとして渡す
                // (notify/tier/achieved_values 等のフィールドを含む)。
                await invoke("open_registered_window", {
                  windowType: "quest_complete",
                  params: {
                    extra: {
                      quests: [{ ...tutorialQuest, notify: "modal", status: "completed" }],
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
          // 次のステップへ進む
          saveProgress(saveId, tutorialId, nextStep);
          setCurrentStep(nextStep);
          setCheckState("idle");
        }
      } else {
        setCheckState("failed");
        // 失敗メッセージを3秒間表示した後、リセットする
        setTimeout(() => setCheckState("idle"), 3000);
      }
    } catch {
      setCheckState("failed");
      setTimeout(() => setCheckState("idle"), 3000);
    }
  }, [checkState, apiBaseUrl, currentStep, saveId, guide, steps, tutorialId, totalSteps]);

  /**
   * 完了メッセージの「終了」ボタン押下時の処理。
   * チュートリアル中の都市はテンプレートモード(工期スキップ等の代行に必要)で動いているため、
   * それを解除してからこのウィンドウを隠す。都市はそのまま通常のプレイとして続けられる。
   */
  const handleFinish = useCallback(async () => {
    if (finishing || !apiBaseUrl) return;
    setFinishing(true);
    try {
      await fetchApi(`${apiBaseUrl}/api/v1/sessions`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ template_mode: false }),
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

  /** 進捗ドットを枠内の下部に描画する */
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

  // 全ステップ完了後の完了メッセージ
  if (showComplete) {
    return (
      <div className="tutorial-root">
        <div className="tutorial-frame">
          <div className="tutorial-frame-content">
            <div className="tutorial-step-image">
              {steps[totalSteps - 1] && steps[totalSteps - 1].image && (
                <img src={steps[totalSteps - 1].image} alt="Complete" />
              )}
            </div>
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
          <div className="tutorial-step-image">
            {step && step.image && <img src={step.image} alt={`Step ${step.id}`} />}
          </div>
          <div className="tutorial-step-content">
            <div className="tutorial-step-number">
              {t("tutorial.stepProgress", { current: step?.id ?? 0, total: totalSteps })}
            </div>
            <div className="tutorial-step-title">
              {step ? t(step.titleKey) : ""}
            </div>
            <div className="tutorial-step-description">
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
