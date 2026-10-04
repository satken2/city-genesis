/**
 * HUD各ウィンドウ(アイコンパネル・コックピット・システムトレイ・表示トレイ)が共有するセッション状態
 *
 * Rust側のセッションポーリングスレッドが全ウィンドウへブロードキャストする
 * `session-state` イベントを受け取り、表示可否と都市の基本情報を返す。
 * ネイティブウィンドウ自体の表示/非表示はRust側(`set_hud_visible`)が行うが、
 * 表示直後に前セッションの内容が一瞬見えるのを避けるため、React側でも `visible` で描画を止める。
 */
import { PLAY_MODE_NORMAL } from "../../constants";


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
  /** 通常・神・チュートリアルのプレイモード */
  playMode: number;
  /** 市庁舎が未設置(=都市創設前)のため、建設以外の操作をロックするか */
  isLocked: boolean;
  /** デバッグ実行。通常・神モードの切替ボタンをシステムトレイに出す */
  debugExecution: boolean;
  cityName: string;
  /** 1800年1月1日からの経過日数。未取得のときは null */
  currentDate: number | null;
  /** 最後に開始したエポックの確定気候。未実行・未取得は null。 */
  epochTemperature: number | null;
  epochSunlight: number | null;
  epochPrecipitation: number | null;
  /** エポック実行中。実行中はHUDの全ボタンを無効化する */
  epochExecuting: boolean;
};

export function useHudSession(): HudSession {
  const appWindow = useMemo(() => getCurrentWindow(), []);
  const [visible, setVisible] = useState(false);
  const [apiBaseUrl, setApiBaseUrl] = useState("");
  const [playMode, setPlayMode] = useState<number>(PLAY_MODE_NORMAL);
  const [cityHallPlaced, setCityHallPlaced] = useState(true);
  const [debugExecution, setDebugExecution] = useState(false);
  const [cityName, setCityName] = useState("");
  const [currentDate, setCurrentDate] = useState<number | null>(null);
  const [epochTemperature, setEpochTemperature] = useState<number | null>(null);
  const [epochSunlight, setEpochSunlight] = useState<number | null>(null);
  const [epochPrecipitation, setEpochPrecipitation] = useState<number | null>(null);
  const [epochExecuting, setEpochExecuting] = useState(false);

  useEffect(() => {
    let unlistenSession: (() => void) | undefined;
    let unlistenRename: (() => void) | undefined;
    let unlistenEpoch: (() => void) | undefined;
    let unlistenLoading: (() => void) | undefined;

    listen("initial-loading:finished", () => {
      setVisible(true);
      appWindow.show().catch(console.error);
    }).then((fn) => { unlistenLoading = fn; }).catch(console.error);

    listen<SessionStatePayload>("session-state", (event) => {
      const payload = event.payload;
      const shouldShow = payload.current_save_id != null && !payload.isLoading;
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
      setPlayMode((payload.session?.play_mode as number) ?? PLAY_MODE_NORMAL);
      setDebugExecution((payload.session?.debug_execution as boolean) ?? false);

      const cityData = payload.city?.city as Record<string, unknown> | undefined;
      setCityName((cityData?.name as string) ?? "");
      setCurrentDate((cityData?.current_date as number) ?? null);
      setEpochTemperature((cityData?.epoch_temperature as number) ?? null);
      setEpochSunlight((cityData?.epoch_sunlight as number) ?? null);
      setEpochPrecipitation((cityData?.epoch_precipitation as number) ?? null);
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
      unlistenLoading?.();
    };
  }, [appWindow]);

  return {
    visible,
    apiBaseUrl,
    playMode,
    isLocked: !cityHallPlaced && playMode === PLAY_MODE_NORMAL,
    debugExecution,
    cityName,
    currentDate,
    epochTemperature,
    epochSunlight,
    epochPrecipitation,
    epochExecuting,
  };
}

