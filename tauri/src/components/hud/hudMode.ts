/**
 * HUDの常駐モード(ブルドーザ・地区編集)のウィンドウ間共有
 *
 * ブルドーザはコックピットのボタンでON/OFFする。地区編集(`district`)はUnityの3D地区編集モード用の値で、
 * 現在はUIからONにする入口が無い(地区の編集は地区編集パネル `zone_edit` で行う)。
 * 常駐モードはUnityの描画ステートを占有するため同時にONにできない。
 * ウィンドウをまたいで唯一の状態を共有する必要があるので、
 * Tauriのグローバルイベント `hud:mode_changed` を真実源として全HUDウィンドウで同期する。
 *
 * モードの副作用(Unityへの`STATE_*`送信、ヒントバー文言、セッションリロード)は
 * 操作したコンポーネント側が実行する。他ウィンドウはこのイベントで表示状態を合わせるだけ。
 * 編集モードのウィンドウを開いたときは、開く入口を問わずRust(`window.rs` の `apply_mutex_rules`)が
 * 解除(`mode: null`)を送る。Unityのステートはそのウィンドウが上書きする。
 */

import { useEffect, useState } from "react";
import { emit, listen } from "@tauri-apps/api/event";

/** 排他的な常駐モード。null はどちらも解除された通常状態。 */
export type HudMode = "bulldozer" | "district" | null;

export const HUD_EVENT_MODE_CHANGED = "hud:mode_changed";

/**
 * 自ウィンドウ内の購読者。Tauriイベントの往復を待たず即時に反映するために持つ
 * (往復で戻ってきた同じ値による更新は同値なので無害)。
 */
const localListeners = new Set<(mode: HudMode) => void>();

/** 常駐モードを切り替え、全HUDウィンドウへ通知する。 */
export async function setHudMode(mode: HudMode): Promise<void> {
  localListeners.forEach((listener) => listener(mode));
  await emit(HUD_EVENT_MODE_CHANGED, { mode });
}

/**
 * 現在の常駐モードを購読する。
 * セッションが切れたとき(Unityが STATE_NORMAL で起動し直すとき)は解除状態に戻す。
 */
export function useHudMode(): HudMode {
  const [mode, setMode] = useState<HudMode>(null);

  useEffect(() => {
    localListeners.add(setMode);
    let unlistenMode: (() => void) | undefined;
    let unlistenSession: (() => void) | undefined;

    listen<{ mode: HudMode }>(HUD_EVENT_MODE_CHANGED, (event) => {
      setMode(event.payload.mode);
    })
      .then((fn) => {
        unlistenMode = fn;
      })
      .catch(console.error);

    listen<{ current_save_id: string | null }>("session-state", (event) => {
      if (event.payload.current_save_id == null) setMode(null);
    })
      .then((fn) => {
        unlistenSession = fn;
      })
      .catch(console.error);

    return () => {
      localListeners.delete(setMode);
      unlistenMode?.();
      unlistenSession?.();
    };
  }, []);

  return mode;
}
