import { useEffect, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { PLAY_MODE_NORMAL } from "../constants";
import { buildApiUrl, fetchApi } from "../utils/api";

/** 初回取得と既存のセッション通知で共有する。取得前はnullとし、通常モードの制限を先に適用しない。 */
export function usePlayMode(apiBaseUrl: string): number | null {
  const [playMode, setPlayMode] = useState<number | null>(null);

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    let unlisten: (() => void) | undefined;
    let receivedEvent = false;
    setPlayMode(null);
    void listen<{ session?: { play_mode: number } }>("session-state", (event) => {
      if (controller.signal.aborted) return;
      receivedEvent = true;
      setPlayMode(event.payload.session?.play_mode ?? PLAY_MODE_NORMAL);
    }).then((fn) => {
      if (controller.signal.aborted) fn();
      else unlisten = fn;
    }).catch(console.error);
    void fetchApi<{ play_mode: number }>(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), { signal: controller.signal })
      .then((session) => {
        if (!controller.signal.aborted && !receivedEvent) setPlayMode(session.play_mode ?? PLAY_MODE_NORMAL);
      })
      .catch(() => {});
    return () => {
      controller.abort();
      unlisten?.();
    };
  }, [apiBaseUrl]);

  return playMode;
}
