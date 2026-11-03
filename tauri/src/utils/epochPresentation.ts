import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { logInfoToFile } from "./log";

/** 通知の購読を先に始め、同じ実行IDの応答だけを待つ。Unity不在・停止時も結果を表示できるよう待機を打ち切る。 */
async function requestEpochStage(id: string, eventName: string, command: string) {
  let complete!: () => void;
  const ready = new Promise<void>((resolve) => { complete = resolve; });
  const unlisten = await listen<{ id: string }>(eventName, (event) => {
    if (event.payload.id === id) complete();
  });
  let timer: ReturnType<typeof setTimeout> | undefined;
  const dispose = () => { clearTimeout(timer); unlisten(); };
  try {
    const connected = await invoke<boolean>(command, { id });
    if (!connected) complete();
    else timer = setTimeout(() => {
      logInfoToFile(`epoch presentation: ${eventName} acknowledgement timed out id=${id}`);
      complete();
    }, 10000);
  } catch (error) {
    dispose();
    throw error;
  }
  return { ready: ready.finally(dispose) };
}

export async function beginEpochPresentation() {
  const id = crypto.randomUUID();
  const { ready } = await requestEpochStage(id, "epoch:ready", "begin_epoch_presentation");
  return {
    id,
    isCurrent: () => invoke<boolean>("is_epoch_presentation_current", { id }),
    ready,
    sunset: async () => {
      const stage = await requestEpochStage(id, "epoch:darkened", "begin_epoch_sunset");
      await stage.ready;
    },
    hideProgress: async () => {
      await invoke("hide_epoch_progress", { id }).catch((error) => {
        console.error("failed to hide epoch progress", error);
      });
    },
    finish: () => invoke<void>("finish_epoch_presentation", { id }),
  };
}
