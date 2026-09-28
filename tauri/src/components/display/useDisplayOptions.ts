import { useCallback, useEffect, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import {
  sendChangeDisplay,
  sendViewQueryState,
  IPC_EVENT_VIEW_CURRENT_STATE,
} from "../../utils/ipc";
import { DISPLAY_LAYERS } from "./displayConstants";

export type DisplaySelection = {
  mode: string;
  subMode: string | null;
  /** ONになっている表示トグルのID集合。色モードと独立してUnityへ flags(CSV) として送られる。 */
  flags: string[];
};

export type DisplayState = Record<string, DisplaySelection>;

function buildInitialState(): DisplayState {
  const state: DisplayState = {};
  for (const layer of DISPLAY_LAYERS) {
    state[layer.id] = {
      mode: layer.defaultMode,
      subMode: null,
      flags: layer.defaultFlags ?? [],
    };
  }
  return state;
}

type LayerStatePayload = {
  mode: string;
  sub_mode: string | null;
  flags: string | null;
};

type CurrentStatePayload = {
  layers: Record<string, LayerStatePayload>;
};

export function useDisplayOptions() {
  const [selections, setSelections] = useState<DisplayState>(buildInitialState);

  const selectMode = useCallback(
    (layerId: string, mode: string, subMode?: string) => {
      setSelections((prev) => {
        // 色モードの変更でトグル状態を巻き戻さないよう、現在の flags を維持して送信する
        const flags = prev[layerId]?.flags ?? [];

        sendChangeDisplay(layerId, mode, subMode, flags).catch((err) => {
          console.error("[useDisplayOptions] sendChangeDisplay failed:", err);
        });

        return {
          ...prev,
          [layerId]: { mode, subMode: subMode ?? null, flags },
        };
      });
    },
    [],
  );

  /**
   * 表示トグル(計画中表示・公共施設ハイライト等)のON/OFFを反転する。
   * 色モード・サブモードは現在値を維持したままUnityへ送信する。
   */
  const toggleFlag = useCallback((layerId: string, flagId: string) => {
    setSelections((prev) => {
      const current = prev[layerId];
      if (!current) return prev;

      const flags = current.flags.includes(flagId)
        ? current.flags.filter((f) => f !== flagId)
        : [...current.flags, flagId];

      sendChangeDisplay(
        layerId,
        current.mode,
        current.subMode ?? undefined,
        flags,
      ).catch((err) => {
        console.error("[useDisplayOptions] sendChangeDisplay failed:", err);
      });

      return {
        ...prev,
        [layerId]: { ...current, flags },
      };
    });
  }, []);

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
                  flags: state.flags ? state.flags.split(",") : [],
                };
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

  return { selections, selectMode, toggleFlag };
}
