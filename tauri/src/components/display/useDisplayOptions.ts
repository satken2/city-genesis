import { useCallback, useEffect, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import {
  sendChangeDisplay,
  sendViewQueryState,
  IPC_EVENT_VIEW_CURRENT_STATE,
} from "../../utils/ipc";
import { DISPLAY_LAYERS } from "./displayConstants";
import { TUTORIAL_FLAG_RESOURCE_VISIBLE } from "../tutorial/tutorialSetup";

/**
 * 天然資源レイヤーの表示状態をチュートリアル用の localStorage フラグに反映する。
 * チュートリアル(シナリオ2)のステップ判定はバックエンドから観測できない
 * 表示状態を localStorage 経由で受け取るため、ここで橋渡しする。
 */
function syncResourceTutorialFlag(mode: string): void {
  localStorage.setItem(
    TUTORIAL_FLAG_RESOURCE_VISIBLE,
    mode === "visible" ? "1" : "0"
  );
}

export type DisplaySelection = {
  mode: string;
  subMode: string | null;
};

export type DisplayState = Record<string, DisplaySelection>;

function buildInitialState(): DisplayState {
  const state: DisplayState = {};
  for (const layer of DISPLAY_LAYERS) {
    state[layer.id] = { mode: layer.defaultMode, subMode: null };
  }
  return state;
}

type LayerStatePayload = {
  mode: string;
  sub_mode: string | null;
};

type CurrentStatePayload = {
  layers: Record<string, LayerStatePayload>;
};

export function useDisplayOptions() {
  const [selections, setSelections] = useState<DisplayState>(buildInitialState);

  const selectMode = useCallback(
    (layerId: string, mode: string, subMode?: string) => {
      setSelections((prev) => ({
        ...prev,
        [layerId]: { mode, subMode: subMode ?? null },
      }));

      if (layerId === "resource") {
        syncResourceTutorialFlag(mode);
      }

      sendChangeDisplay(layerId, mode, subMode).catch((err) => {
        console.error("[useDisplayOptions] sendChangeDisplay failed:", err);
      });
    },
    [],
  );

  // マウント時にUnityへ表示状態を問い合わせ、レスポンスで同期する
  // リスナー登録をawaitしてからクエリを送信し、応答がリスナーに届くことを保証する
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    const appWindow = getCurrentWindow();
    let cancelled = false;

    const setup = async () => {
      unlisten = await appWindow.listen<CurrentStatePayload>(
        IPC_EVENT_VIEW_CURRENT_STATE,
        (event) => {
          const layers = event.payload?.layers;
          if (!layers) return;

          setSelections((prev) => {
            const next = { ...prev };
            for (const [layerId, state] of Object.entries(layers)) {
              if (next[layerId]) {
                next[layerId] = {
                  mode: state.mode,
                  subMode: state.sub_mode || null,
                };
                if (layerId === "resource") {
                  syncResourceTutorialFlag(state.mode);
                }
              }
            }
            return next;
          });
        },
      );

      if (cancelled) {
        unlisten();
        unlisten = undefined;
        return;
      }

      await sendViewQueryState();
    };

    setup().catch((err) => {
      console.warn("[useDisplayOptions] setup failed:", err);
    });

    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, []);

  return { selections, selectMode };
}
