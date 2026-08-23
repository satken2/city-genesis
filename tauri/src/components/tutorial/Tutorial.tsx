/**
 * チュートリアルウィンドウコンポーネント
 *
 * 新規ゲーム開始直後に表示されるステップ型チュートリアル。
 * 各ステップではユーザーに操作を促し、完了ボタン押下時に
 * バックエンド API を検証して条件クリアなら次のステップへ進む。
 *
 * - タイトルバーや×ボタンを持たない、ヒントバー風のシンプルな表示
 * - 画面上部中央に固定され、ヒントバーの下に配置される
 * - 左側に正方形の画像、右側に説明文と完了ボタンを配置
 * - 進捗はセーブ ID ごとに localStorage に保持する
 * - 全ステップ完了で完了メッセージを表示し、「メニューに戻る」ボタンで
 *   セッションを終了してトップメニューに戻る
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
import { emit } from "@tauri-apps/api/event";
import { invoke } from "@tauri-apps/api/core";
import { TUTORIAL_SCENARIOS } from "./tutorialSteps";
import { runTutorialSetup } from "./tutorialSetup";
import "./Tutorial.css";

/** localStorage にチュートリアル進捗を保存する際のキーのプレフィックス */
const TUTORIAL_STORAGE_KEY_PREFIX = "tutorial_step_";

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
  /** セッション終了中（DELETEリクエスト送信後）かどうか */
  const [returningToMenu, setReturningToMenu] = useState(false);

  const currentScenario = useMemo(() => {
    return TUTORIAL_SCENARIOS.find((s) => s.id === tutorialId) || TUTORIAL_SCENARIOS[0];
  }, [tutorialId]);

  const steps = currentScenario.steps;
  const totalSteps = steps.length;

  // チュートリアルの有効状態とターゲットボタンの同期処理
  useEffect(() => {
    const active = visible && !completed && !showComplete;
    const target = active ? steps[currentStep]?.targetElementId : undefined;

    localStorage.setItem("tutorial_active", String(active));
    if (target) {
      localStorage.setItem("tutorial_target", target);
    } else {
      localStorage.removeItem("tutorial_target");
    }

    emit("tutorial:state_changed", {
      active,
      targetElementId: target,
    }).catch(console.error);

    return () => {
      localStorage.setItem("tutorial_active", "false");
      localStorage.removeItem("tutorial_target");
      emit("tutorial:state_changed", {
        active: false,
        targetElementId: undefined,
      }).catch(console.error);
    };
  }, [visible, completed, currentStep, showComplete]);

  /**
   * 前回のセーブ ID を保持する ref。
   * ポーリングで同一セーブ ID の session-state イベントが何度も飛んでくるため、
   * セーブ ID が実際に変化した時だけ進捗を復元するために使用する。
   */
  const prevSaveIdRef = useRef<string | null>(null);

  /**
   * 初期状態セットアップを実行済みのキー(saveId + tutorialId)を保持する ref。
   * ポーリングで何度も条件が満たされても、1つのセッションにつき一度だけ
   * runTutorialSetup を呼ぶための重複実行ガード。
   */
  const setupRanForRef = useRef<string | null>(null);

  /**
   * チュートリアル開始時に、シナリオごとの初期都市状態を構築する。
   * 表示中かつ API URL・セーブ ID が確定したタイミングで一度だけ実行する。
   * runTutorialSetup 側でもバックエンドの状態を見て冪等性を担保しているが、
   * ここでも saveId + tutorialId 単位でガードして無駄な呼び出しを避ける。
   */
  useEffect(() => {
    if (!visible || !apiBaseUrl || !saveId) return;
    const key = `${saveId}_${tutorialId}`;
    if (setupRanForRef.current === key) return;
    setupRanForRef.current = key;
    void runTutorialSetup(tutorialId, apiBaseUrl);
  }, [visible, apiBaseUrl, saveId, tutorialId]);

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
            }
          }
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

  /** 完了ボタン押下時の検証処理 */
  const handleComplete = useCallback(async () => {
    if (checkState === "checking" || !apiBaseUrl) return;

    const step = steps[currentStep];
    if (!step) return;

    setCheckState("checking");
    try {
      // 検証の前提を整える処理(例: 工期スキップによるパスの即時完成)を先に実行する。
      // 端点ノードの接続など「完成したパスにしか現れない状態」を検証条件にするステップは、
      // ここで完成させておかないと何度押しても条件を満たせない。
      // 前提整備の失敗は検証を止めない(条件未達として通常どおり不合格になる)。
      try {
        await step.prepare?.(apiBaseUrl);
      } catch (e) {
        console.error("Tutorial step prepare failed:", e);
      }

      const passed = await step.validate(apiBaseUrl);
      if (passed) {
        // 検証成功時、ステップ固有の副作用(例: 建設スキップによる強制稼働)を実行する。
        // 副作用の失敗はチュートリアルの進行を止めないよう握りつぶす。
        try {
          await step.onComplete?.(apiBaseUrl);
        } catch (e) {
          console.error("Tutorial step onComplete failed:", e);
        }

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
  }, [checkState, apiBaseUrl, currentStep, saveId]);

  /**
   * 完了メッセージの「メニューに戻る」ボタン押下時の処理。
   * バックエンドの DELETE /api/v1/sessions を呼び出してセッションを終了する。
   * Rust 側のポーリングが current_save_id の null 化を検知し、
   * 自動的にトップメニューに戻る。
   */
  const handleReturnToMenu = useCallback(async () => {
    if (returningToMenu || !apiBaseUrl) return;
    setReturningToMenu(true);
    try {
      await fetch(`${apiBaseUrl}/api/v1/sessions`, { method: "DELETE" });
    } catch {
      // 通信エラーでも UI 側のポーリングが状態変化を検知して画面遷移する
    }
  }, [returningToMenu, apiBaseUrl]);

  if (!visible) {
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
                  onClick={() => void handleReturnToMenu()}
                  disabled={returningToMenu}
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
  const buttonTextKey = step?.buttonTextKey ?? "tutorial.completeButton";

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
              {step ? t(step.descriptionKey) : ""}
            </div>
            <div className="tutorial-actions">
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
                disabled={checkState === "checking"}
                type="button"
              >
                {checkState === "checking"
                  ? t("tutorial.checking")
                  : t(buttonTextKey)}
              </button>
            </div>
          </div>
        </div>
        {renderProgress()}
      </div>
    </div>
  );
}
