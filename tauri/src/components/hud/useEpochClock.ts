/**
 * 次のエポックまでの残り時間と、時間を止める操作(コックピットが持つ)
 *
 * 時計の正は Unity の太陽で、1周期は朝から夕焼けまで。周期が終わると Unity が `epoch:due` を送り、
 * onDue が開始ボタンと同じ手順でエポックを始める。始められなかったら、渡された stop で時間を止める。
 * UI が持つのは周期の長さ(設定)と時間を止めているかだけで、変わったときと Unity の再起動時に送る。
 * 開始ボタンが押せない間(市庁舎の建設前)も止めて送る。時間を止めても交通の演出は止めない。
 *
 * 遊ぶモード(通常・チュートリアル)は進めた状態、作る・見せるモード(シナリオ作成・読み取り専用)は止めた状態で
 * 始め、都市を開くたびとモードが変わるたびにこの既定へ戻す。
 */
import { PLAY_MODE_NORMAL, PLAY_MODE_TUTORIAL } from "../../constants";

import { useCallback, useEffect, useRef, useState } from "react";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import { IPC_EVENT_CLOCK_STATE, IPC_EVENT_EPOCH_DUE, sendClockSettings, type ClockState } from "../../utils/ipc";
import { loadAppSettings, type AppSettings } from "../../utils/settings";

const RUNNING_PLAY_MODES = [PLAY_MODE_NORMAL, PLAY_MODE_TUTORIAL];

/** Unity から受けた残り時間と、受けた時刻(performance.now)。 */
export type EpochClockSnapshot = { remainingSeconds: number; running: boolean; receivedAt: number };

function subscribe<T>(event: string, handler: (payload: T) => void): () => void {
  let disposed = false;
  let unlisten: UnlistenFn | undefined;
  listen<T>(event, ({ payload }) => handler(payload))
    .then((fn) => (disposed ? fn() : (unlisten = fn)))
    .catch(console.error);
  return () => {
    disposed = true;
    unlisten?.();
  };
}

export function useEpochClock(options: {
  visible: boolean;
  playMode: number;
  blocked: boolean;
  onDue: (stop: () => void) => void;
}) {
  const { visible, playMode, blocked, onDue } = options;
  const [stopped, setStopped] = useState(true);
  const [minutes, setMinutes] = useState<number | null>(null);
  const [clock, setClock] = useState<EpochClockSnapshot | null>(null);
  const onDueRef = useRef(onDue);
  onDueRef.current = onDue;
  const stop = useCallback(() => setStopped(true), []);

  useEffect(() => {
    if (visible) setStopped(!RUNNING_PLAY_MODES.includes(playMode));
  }, [visible, playMode]);

  useEffect(() => {
    let disposed = false;
    const apply = (settings: AppSettings) => {
      if (!disposed) setMinutes(settings.epoch_interval_minutes);
    };
    void loadAppSettings().then(apply).catch(console.error);
    const unsubscribe = subscribe<AppSettings>("settings-updated", apply);
    return () => {
      disposed = true;
      unsubscribe();
    };
  }, []);

  const sentStopped = stopped || blocked;
  useEffect(() => {
    if (!visible || minutes === null) return;
    const send = () => void sendClockSettings(minutes * 60, sentStopped).catch(console.error);
    send();
    return subscribe("graphic-reconnected", send);
  }, [visible, minutes, sentStopped]);

  useEffect(() => {
    if (!visible) {
      setClock(null);
      return;
    }
    const unsubscribeState = subscribe<ClockState>(IPC_EVENT_CLOCK_STATE, (payload) => {
      if (!payload || !Number.isFinite(payload.remaining_seconds)) return;
      setClock({ remainingSeconds: payload.remaining_seconds, running: payload.running === true, receivedAt: performance.now() });
    });
    const unsubscribeDue = subscribe(IPC_EVENT_EPOCH_DUE, () => onDueRef.current(stop));
    return () => {
      unsubscribeState();
      unsubscribeDue();
    };
  }, [visible, stop]);

  const toggleStopped = useCallback(() => setStopped((value) => !value), []);

  return { clock, stopped, toggleStopped };
}

/** 手元で減らした残り秒数。 */
export function remainingSecondsOf(clock: EpochClockSnapshot, now: number): number {
  return clock.running ? Math.max(0, clock.remainingSeconds - (now - clock.receivedAt) / 1000) : clock.remainingSeconds;
}
