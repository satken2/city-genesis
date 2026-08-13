/**
 * HUD3ウィンドウ(アイコンパネル・コックピット・システムトレイ)が共有するセッション状態
 *
 * Rust側のセッションポーリングスレッドが全ウィンドウへブロードキャストする
 * `session-state` イベントを受け取り、表示可否と都市の基本情報を返す。
 * ネイティブウィンドウ自体の表示/非表示はRust側(`set_hud_visible`)が行うが、
 * 表示直後に前セッションの内容が一瞬見えるのを避けるため、React側でも `visible` で描画を止める。
 *
 * チュートリアルのハイライト対象も全HUDウィンドウで共通のため、ここでまとめて扱う。
 */

import { useEffect, useMemo, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { listen } from "@tauri-apps/api/event";

type SessionStatePayload = {
  current_save_id: string | null;
  session?: Record<string, unknown>;
  city?: Record<string, unknown>;
  apiBaseUrl: string;
  isLoading?: boolean;
};

export type HudSession = {
  /** セッション(プレイ中の都市)が存在し、HUDを描画してよいか */
  visible: boolean;
  apiBaseUrl: string;
  /** テンプレート編集モード。資源編集・パラメータ編集など製作者向けの操作が増える */
  templateMode: boolean;
  /** 市庁舎が未設置(=都市創設前)のため、建設以外の操作をロックするか */
  isLocked: boolean;
  /** デバッグ実行。テンプレートモードの切替ボタンをシステムトレイに出す */
  debugExecution: boolean;
  cityName: string;
  /** 1800年1月1日からの経過日数。未取得のときは null */
  currentDate: number | null;
  /** エポック実行中。実行中はHUDの全ボタンを無効化する */
  epochExecuting: boolean;
};

export function useHudSession(): HudSession {
  const appWindow = useMemo(() => getCurrentWindow(), []);
  const [visible, setVisible] = useState(false);
  const [apiBaseUrl, setApiBaseUrl] = useState("");
  const [templateMode, setTemplateMode] = useState(false);
  const [cityHallPlaced, setCityHallPlaced] = useState(true);
  const [debugExecution, setDebugExecution] = useState(false);
  const [cityName, setCityName] = useState("");
  const [currentDate, setCurrentDate] = useState<number | null>(null);
  const [epochExecuting, setEpochExecuting] = useState(false);

  useEffect(() => {
    let unlistenSession: (() => void) | undefined;
    let unlistenRename: (() => void) | undefined;
    let unlistenEpoch: (() => void) | undefined;

    listen<SessionStatePayload>("session-state", (event) => {
      const payload = event.payload;
      const shouldShow = payload.current_save_id != null;
      // Rust側(`set_hud_visible`)もセッションの開始・終了で表示を切り替えるが、
      // ポーリングのたびに現在の状態へ寄せ直すことで、取りこぼしで
      // ウィンドウが消えたまま/出たままになるのを防ぐ。
      if (shouldShow) {
        appWindow.show().catch(() => {});
      } else {
        appWindow.hide().catch(() => {});
      }
      setVisible(shouldShow);
      setApiBaseUrl(payload.apiBaseUrl);
      setTemplateMode((payload.session?.template_mode as boolean) ?? false);
      setDebugExecution((payload.session?.debug_execution as boolean) ?? false);

      const cityData = payload.city?.city as Record<string, unknown> | undefined;
      setCityName((cityData?.name as string) ?? "");
      setCurrentDate((cityData?.current_date as number) ?? null);
      setCityHallPlaced((cityData?.city_hall_placed as boolean) ?? false);
    })
      .then((fn) => {
        unlistenSession = fn;
      })
      .catch(console.error);

    // 都市名の変更。セッションポーリングの次の周回を待たずに表示へ反映する
    // (送信元は city_rename ウィンドウ = CityRenameWindow.tsx)。
    listen<{ name: string }>("city:renamed", (event) => {
      setCityName(event.payload.name);
    })
      .then((fn) => {
        unlistenRename = fn;
      })
      .catch(console.error);

    // エポック実行の開始/終了。送信元は epoch_confirm ウィンドウ(EpochConfirmWindow.tsx)。
    appWindow
      .listen<{ executing: boolean }>("epoch:executing", (event) => {
        setEpochExecuting(event.payload.executing);
      })
      .then((fn) => {
        unlistenEpoch = fn;
      })
      .catch(console.error);

    return () => {
      unlistenSession?.();
      unlistenRename?.();
      unlistenEpoch?.();
    };
  }, [appWindow]);

  return {
    visible,
    apiBaseUrl,
    templateMode,
    isLocked: !cityHallPlaced && !templateMode,
    debugExecution,
    cityName,
    currentDate,
    epochExecuting,
  };
}

/**
 * チュートリアルのハイライト対象を購読し、ボタンのクラス名を解決する関数を返す。
 * ハイライト対象のIDはHUD3ウィンドウにまたがるが、各ウィンドウは自分が持つIDだけ反応する。
 */
export function useTutorialHighlight(): (id: string, baseClass: string) => string {
  const [tutorialActive, setTutorialActive] = useState(false);
  const [tutorialTarget, setTutorialTarget] = useState<string | undefined>(undefined);

  useEffect(() => {
    // 初期状態は localStorage から復元する(ウィンドウ生成がチュートリアル開始より後になる場合があるため)
    setTutorialActive(localStorage.getItem("tutorial_active") === "true");
    setTutorialTarget(localStorage.getItem("tutorial_target") || undefined);

    let unlistenState: (() => void) | undefined;
    listen<{ active: boolean; targetElementId?: string }>("tutorial:state_changed", (event) => {
      setTutorialActive(event.payload.active);
      setTutorialTarget(event.payload.targetElementId);
    })
      .then((fn) => {
        unlistenState = fn;
      })
      .catch(console.error);

    return () => {
      unlistenState?.();
    };
  }, []);

  return (id: string, baseClass: string) =>
    tutorialActive && tutorialTarget === id ? `${baseClass} hud-highlighted-btn` : baseClass;
}
