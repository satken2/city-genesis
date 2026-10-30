import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { logInfoToFile } from "./log";

/** Subscribe first to avoid missing Unity's readiness reply. */
export async function beginEpochPresentation() {
  const id = crypto.randomUUID();
  let complete!: () => void;
  const ready = new Promise<void>((resolve) => { complete = resolve; });
  const unlisten = await listen<{ id: string }>("epoch:ready", (event) => {
    if (event.payload.id === id) complete();
  });
  let timer: ReturnType<typeof setTimeout> | undefined;
  const dispose = () => { clearTimeout(timer); unlisten(); };
  try {
    const connected = await invoke<boolean>("begin_epoch_presentation", { id });
    if (!connected) complete();
    else timer = setTimeout(() => {
      // Start without Unity when it stops or restarts; it resynchronizes its city on reconnection.
      logInfoToFile(`epoch presentation: readiness acknowledgement timed out id=${id}`);
      complete();
    }, 10000);
  } catch (error) {
    dispose();
    throw error;
  }
  return {
    id,
    isCurrent: () => invoke<boolean>("is_epoch_presentation_current", { id }),
    ready: ready.finally(dispose),
    setTipsVisible: async (visible: boolean) => {
      await invoke("set_epoch_tips_visible", { id, visible }).catch((error) => {
        console.error("failed to update epoch tips", error);
      });
    },
    finish: () => invoke<void>("finish_epoch_presentation", { id }),
  };
}
