/**
 * 起動後の接続監視とゲームの開始・終了。`src-tauri/src/lib.rs` のセッション監視・初期ロード・トップメニュー復帰と
 * `backend_health.rs` の移植で、状態の遷移と送るイベントは exe と同じにする。
 */
import { DEVELOPMENT } from "./config";
import { emit } from "./events";
import * as host from "./host";
import { sendUiAction } from "./ipc";
import { apiBase, fetchWithTimeout, sleep } from "./net";
import { requestAppExit } from "./process";
import { state } from "./state";
import { checkFrontendVersion, coreInfo, coreInfoReady, prepareGraphic, validateLoad, verifyCore } from "./version";
import {
  cancelEpochPresentation,
  closeAllPopupWindows,
  closeParameterSetupWindow,
  closeTipsWindow,
  dismissNotification,
  openParameterSetupWindow,
  openTipsWindow,
  setHudVisible,
  showHintbarIfEnabled,
} from "./windows";

const PLAY_MODE_NORMAL = 0;
const PLAY_MODE_SCENARIO = 1;
const PLAY_MODE_TUTORIAL = 2;
const GAME_VERIFIED = 0;
const POLL_INTERVAL_MS = 5000;

export function requireBackend(): void {
  if (!state.backendHealth.connected) throw "BACKEND_DISCONNECTED";
}

export function publishBackendConnection(connected: boolean): void {
  emit("backend-connection", { connected });
  if (state.graphicConnected) {
    try {
      sendUiAction("", null, "system:backend_connection", { connected });
    } catch {
      // Unity への通知の失敗は接続状態の配信を止めない(exe も結果を見ない)。
    }
  }
}

/**
 * コアが認証済みかを Unity へ伝え、UNVERIFIED の表示を切り替えさせる(デバッグ起動は認証済み扱い)。
 * Unity の起動時と、セッション監視で変化を見たときに送る。最初の取得に成功するまでは送らない。
 */
export function publishVerification(): void {
  const status = sessionVerificationStatus(state.sessionState);
  if (!state.graphicConnected || status === null) return;
  try {
    sendUiAction("", null, "system:verification", { verified: status === GAME_VERIFIED });
  } catch {
    // exe も送信の失敗を見ない。
  }
}

function sessionVerificationStatus(session: unknown): number | null {
  const status = (session as Record<string, unknown> | null)?.verification_status;
  return typeof status === "number" ? status : null;
}

/** HTTP の失敗が2回続いたら、製品版は終了し、開発版はトップメニューへ戻って再接続を待つ。成功が挟まれば数え直す。 */
function observeHealth(success: boolean): "Continue" | "ReturnToMenu" | "Exit" {
  const health = state.backendHealth;
  health.connected = success;
  health.failures = success ? 0 : health.failures + 1;
  if (health.failures < 2) return "Continue";
  if (!DEVELOPMENT) return "Exit";
  return health.failures === 2 ? "ReturnToMenu" : "Continue";
}

function observeBackend(success: boolean): boolean {
  const previous = state.backendHealth.connected;
  const action = observeHealth(success);
  state.backendHealth.connected = success && state.graphicContractError === null && coreInfoReady();
  if (previous !== state.backendHealth.connected) publishBackendConnection(state.backendHealth.connected);
  if (action === "Exit") {
    console.warn("[session-poll] 通信確認が2回連続で失敗したため終了します");
    requestAppExit();
    return false;
  }
  if (action === "ReturnToMenu") {
    console.warn("[session-poll] 通信確認が2回連続で失敗しました。開発版は再接続を待ちます");
    returnToTopmenu();
  }
  return true;
}

/** ロード要求の世代を進め、前のセッションの完了通知を無視する。 */
export function beginInitialLoading(needsSetup: boolean): void {
  cancelEpochPresentation();
  state.loadingId += 1;
  state.isLoading = true;
  state.backendLoadComplete = false;
  state.loadingStarted = false;
  state.graphicLoadReady = false;
  state.needsParameterSetup = needsSetup;
  state.awaitingParameterSetup = false;
  state.tutorialId = null;
  setHudVisible(false);
  host.hide("hintbar");
  try {
    sendUiAction("", null, "game:loading", {});
    openTipsWindow();
  } catch (error) {
    abortInitialLoading();
    throw error;
  }
}

function abortInitialLoading(): void {
  state.isLoading = false;
  state.backendLoadComplete = false;
  state.loadingStarted = false;
  state.graphicLoadReady = false;
  state.needsParameterSetup = false;
  // 既存のセッションが残っていれば、次の監視で描画を作り直して復帰する。
  state.lastSessionId = null;
  closeTipsWindow();
  host.show("topmenu");
}

/** game:start は描画の準備だけを始めさせる。表示の解禁は game:reveal で行う。 */
export function sendGameStartIpc(): void {
  state.loadingStarted = true;
  state.graphicLoadReady = false;
  // Unity なしで UI を開発するときは、コアの準備完了だけで開始できる。判定は「今つながっているか」。
  if (DEVELOPMENT) state.graphicLoadReady = !state.graphicConnected;
  const loadingId = state.loadingId;
  for (const [action, params] of [["session:reload", {}], ["game:start", { loading_id: loadingId }]] as const) {
    try {
      sendUiAction("", null, action, params);
    } catch {
      // exe も送信の失敗を見ない。描画が始まらなければ Tips が完了を待ち続ける。
    }
  }
}

function showGameControls(): void {
  emit("initial-loading:finished", null);
  setHudVisible(true);
  showHintbarIfEnabled();
  if (state.tutorialId !== null) {
    host.show("tutorial");
    emit("tutorial:show", state.tutorialId);
  }
}

/** セッションの消失・開発版の通信断で、表示と待機状態を片付けてトップメニューへ戻る。 */
function returnToTopmenu(): void {
  state.lastSessionId = null;
  state.sessionState = null;
  state.loadingId += 1;
  state.isLoading = false;
  state.backendLoadComplete = false;
  state.loadingStarted = false;
  state.graphicLoadReady = false;
  state.needsParameterSetup = false;
  state.awaitingParameterSetup = false;
  state.tutorialId = null;
  state.notificationQueue = [];
  cancelEpochPresentation();
  closeAllPopupWindows();
  dismissNotification();
  closeTipsWindow();
  closeParameterSetupWindow();
  setHudVisible(false);
  host.hide("hintbar");
  host.hide("tutorial");
  emit("tutorial:hide", null);
  host.show("topmenu");
  try {
    sendUiAction("", null, "game:end", {});
  } catch {
    // Unity がいなくても復帰は続ける。
  }
}

/** コアに接続できない間の session-state。トップメニューはこれを受けて表示され、接続失敗を示す。 */
function disconnectedSessionState(coreVersion: unknown) {
  return {
    current_save_id: null,
    session: null,
    city: null,
    apiBaseUrl: state.apiBaseUrl,
    isLoading: false,
    tutorialId: null,
    coreVersion: coreVersion ?? null,
    backendConnected: false,
  };
}

/**
 * 5秒ごとにコアのセッションを取得し、`session-state` として全ウィンドウへ配る。
 * コアの準備完了を確かめてから Unity の初期描画を始めさせる。
 */
export async function startSessionPolling(): Promise<void> {
  let core: Record<string, unknown>;
  for (;;) {
    if (state.shutdown) return;
    try {
      core = await coreInfo();
      break;
    } catch (error) {
      console.warn("[startup] コアの版情報を取得できません:", error);
      if (!observeBackend(false)) return;
      emit("session-state", disconnectedSessionState(null));
      await sleep(POLL_INTERVAL_MS);
    }
  }
  const versionError = checkFrontendVersion(core);
  if (versionError !== null) {
    console.error(`[startup] ${versionError}。終了します`);
    requestAppExit();
    return;
  }

  while (!state.shutdown) {
    if (!state.apiBaseUrl) {
      await sleep(POLL_INTERVAL_MS);
      continue;
    }
    const base = apiBase();
    // この取得より後にセッションの作成が終わった場合、古い応答を新しいセッションの状態として採用しない。
    const polledLoadingId = state.loadingId;
    const loadCompleteBeforePoll = state.backendLoadComplete;
    const startedBeforePoll = state.loadingStarted;

    const sessionData = await fetchSession(base);
    if (!sessionData) {
      if (!observeBackend(false)) break;
      if (state.lastSessionId === null && !state.isLoading) emit("session-state", disconnectedSessionState(core.core_version));
      await sleep(POLL_INTERVAL_MS);
      continue;
    }
    if (state.graphicContractError !== null) {
      if (!observeBackend(true)) break;
      await sleep(POLL_INTERVAL_MS);
      continue;
    }

    // ゲームの開始・終了は current_save_id の有無、別の都市への切り替えは session_id で判定する。
    const currentSaveId = typeof sessionData.current_save_id === "string" ? sessionData.current_save_id : null;
    const currentSessionId = currentSaveId !== null && typeof sessionData.session_id === "string" ? sessionData.session_id : null;
    const cityData = currentSaveId !== null ? await fetchCity(base) : null;

    const previousSessionId = state.lastSessionId;
    const isLoading = state.isLoading;
    const sessionChanged = previousSessionId !== currentSessionId;
    const loadCompleted =
      loadCompleteBeforePoll && state.backendLoadComplete && state.loadingId === polledLoadingId && currentSaveId !== null;
    if (loadCompleted) state.backendLoadComplete = false;
    // ロード中の消失は通常の変化判定から外れるので別に拾う。準備完了の確認後に送った取得だけで判定する。
    const sessionLostWhileLoading =
      isLoading && currentSaveId === null && (loadCompleteBeforePoll || startedBeforePoll) && state.loadingId === polledLoadingId;

    if ((sessionChanged && !isLoading) || loadCompleted || sessionLostWhileLoading) {
      state.lastSessionId = currentSessionId;
      if (currentSessionId !== null) {
        console.info("[session-poll] current_save_id が設定されました。ゲーム開始を検知");
        if (!isLoading) {
          if (sessionChanged) closeAllPopupWindows();
          try {
            beginInitialLoading(false);
          } catch (error) {
            console.error("初期ロード画面を開けません:", error);
          }
        }
        // セッションの作成の応答より先に検知しても、準備完了まではロードしない。
        if (loadCompleted || !isLoading) sendGameStartIpc();
      } else {
        console.info("[session-poll] current_save_id がクリアされました。ゲーム終了を検知");
        returnToTopmenu();
      }
    }

    const verificationChanged = sessionVerificationStatus(state.sessionState) !== sessionVerificationStatus(sessionData);
    state.sessionState = sessionData;
    if (!state.isLoading && sessionData.play_mode !== PLAY_MODE_TUTORIAL) state.tutorialId = null;
    if (verificationChanged) publishVerification();
    // 復旧時は、セッションの消失・切り替えを反映してから操作を解禁する。
    if (!observeBackend(true)) break;
    emit("session-state", {
      current_save_id: currentSaveId,
      session: sessionData,
      city: cityData,
      apiBaseUrl: state.apiBaseUrl,
      isLoading: state.isLoading,
      tutorialId: state.tutorialId,
      coreVersion: core.core_version ?? null,
      backendConnected: true,
    });
    await sleep(POLL_INTERVAL_MS);
  }
}

async function fetchSession(base: string): Promise<Record<string, unknown> | null> {
  try {
    const response = await fetchWithTimeout(`${base}/api/v1/sessions`, 10000);
    if (!response.ok) {
      console.error(`[session-poll] GET /api/v1/sessions 失敗: status=${response.status} body=${await response.text().catch(() => "")}`);
      return null;
    }
    const json = (await response.json()) as unknown;
    if (json !== null && typeof json === "object" && "current_save_id" in json) return json as Record<string, unknown>;
    console.error("[session-poll] セッション応答の形式が不正です");
    return null;
  } catch (error) {
    console.error("[session-poll] GET /api/v1/sessions リクエスト失敗:", error);
    return null;
  }
}

/** 都市の情報(タスクバーの都市名・日付用)。失敗してもセッションの情報には影響させない。 */
async function fetchCity(base: string): Promise<unknown> {
  try {
    const response = await fetchWithTimeout(`${base}/api/v1/city/`, 10000);
    const body = await response.text();
    return response.ok ? (JSON.parse(body) as unknown) : null;
  } catch {
    return null;
  }
}

interface CreateSessionArgs {
  scenarioId?: string | null;
  cityName?: string | null;
  language?: string | null;
  simulationDays?: number | null;
  playMode?: number | null;
  tutorialId?: string | null;
  acceptBalanceChange?: boolean | null;
  climate?: number | null;
  groundCover?: number | null;
  assetPack?: string | null;
}

/** 新規ゲームのセッションを作る(`create_session`)。追加の指定はクエリパラメータでコアへ渡す。 */
export async function createSession(args: CreateSessionArgs): Promise<void> {
  const playMode = args.playMode ?? PLAY_MODE_NORMAL;
  if (![PLAY_MODE_NORMAL, PLAY_MODE_SCENARIO, PLAY_MODE_TUTORIAL].includes(playMode)) throw "不正なプレイモードです。";
  const scenarioId = args.scenarioId ?? null;
  await verifyCore();
  if (scenarioId !== null) await validateLoad(scenarioId, true, args.acceptBalanceChange ?? false);
  await prepareGraphic();
  requireBackend();
  host.hide("topmenu");
  closeAllPopupWindows();
  beginInitialLoading(scenarioId === null && playMode === PLAY_MODE_NORMAL);
  state.isLoading = true;
  state.tutorialId = playMode === PLAY_MODE_TUTORIAL ? (args.tutorialId ?? "basic") : null;

  const query = new URLSearchParams();
  const optional: [string, unknown][] = [
    ["scenario_id", scenarioId],
    ["city_name", args.cityName],
    ["language", args.language],
    ["simulation_days", args.simulationDays],
    ["play_mode", playMode],
    ["tutorial_id", args.tutorialId],
    ["accept_balance_change", args.acceptBalanceChange ?? false],
    ["climate", args.climate],
    ["ground_cover", args.groundCover],
    ["asset_pack", args.assetPack],
  ];
  for (const [key, value] of optional) if (value !== null && value !== undefined) query.set(key, String(value));
  await postSessionStart(`${apiBase()}/api/v1/sessions?${query}`, "セッション作成");
}

/** セーブデータを読み込んでゲームを始める(`start_load_game`)。 */
export async function startLoadGame(args: { saveId: string; acceptBalanceChange?: boolean | null }): Promise<void> {
  const accepted = args.acceptBalanceChange ?? false;
  await validateLoad(args.saveId, false, accepted);
  await prepareGraphic();
  requireBackend();
  host.hide("topmenu");
  // ロードを確定したら、前の都市に結び付いた詳細パネルなどを一掃する。
  closeAllPopupWindows();
  beginInitialLoading(false);
  const query = new URLSearchParams({ save_id: args.saveId, accept_balance_change: String(accepted) });
  await postSessionStart(`${apiBase()}/api/v1/sessions?${query}`, "ロード");
}

/** 開始の要求を送る。成功したら完了の判定をセッション監視に任せ、失敗したらトップメニューへ戻す。 */
async function postSessionStart(url: string, what: string): Promise<void> {
  let response: Response;
  try {
    response = await fetchWithTimeout(url, 60000, { method: "POST" });
  } catch (error) {
    abortInitialLoading();
    throw `${what}リクエストに失敗しました: ${String(error)}`;
  }
  if (!response.ok) {
    const body = await response.text().catch(() => "");
    console.error(`[${what}] 失敗: ${response.status} ${body}`);
    abortInitialLoading();
    host.show("topmenu");
    state.isLoading = false;
    throw `${what}に失敗しました: ${response.status} ${response.statusText} ${body}`;
  }
  state.backendLoadComplete = true;
}

/** フリーモードの新規ゲームで、パラメータ設定を終えたあとに HUD を出す(`finish_parameter_setup`)。 */
export function finishParameterSetup(): void {
  if (!state.awaitingParameterSetup) throw "初期設定中ではありません";
  state.awaitingParameterSetup = false;
  state.isLoading = false;
  closeParameterSetupWindow();
  showGameControls();
}

/** Tips の開始ボタン(`close_tips_window`)。初期ロードの描画が終わっていれば都市を見せる。 */
export function closeTipsAndReveal(): void {
  if (!state.isLoading || !state.graphicLoadReady) throw "初期ロードは完了していません";
  const needsSetup = state.needsParameterSetup;
  const loadingId = state.loadingId;
  // 作れなくても Tips から再試行できるよう、先に次のウィンドウを用意する。
  if (needsSetup) openParameterSetupWindow(false);
  sendUiAction("", null, "game:reveal", { loading_id: loadingId });
  state.graphicLoadReady = false;
  state.needsParameterSetup = false;
  state.awaitingParameterSetup = needsSetup;
  state.isLoading = needsSetup;
  closeTipsWindow();
  if (!needsSetup) showGameControls();
}
