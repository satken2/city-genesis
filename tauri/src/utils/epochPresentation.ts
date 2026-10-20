import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { logInfoToFile } from "./log";

/** Subscribe first to avoid missing fade-out rendering completion. */
export async function beginEpochPresentation() {
  const id = crypto.randomUUID();
  let complete!: () => void;
  const darkened = new Promise<void>((resolve) => { complete = resolve; });
  const unlisten = await listen<{ id: string }>("epoch:darkened", (event) => {
    if (event.payload.id === id) complete();
  });
  let timer: ReturnType<typeof setTimeout> | undefined;
  const dispose = () => { clearTimeout(timer); unlisten(); };
  try {
    const connected = await invoke<boolean>("begin_epoch_presentation", { id });
    if (!connected) complete();
    else timer = setTimeout(() => {
      // Restore UI without losing completed simulation results when Unity stops or restarts.
      logInfoToFile(`epoch presentation: blackout acknowledgement timed out id=${id}`);
      complete();
    }, 10000);
  } catch (error) {
    dispose();
    throw error;
  }
  return {
    id,
    isCurrent: () => invoke<boolean>("is_epoch_presentation_current", { id }),
    darkened: darkened.finally(dispose),
    setTipsVisible: async (visible: boolean) => {
      await invoke("set_epoch_tips_visible", { id, visible }).catch((error) => {
        console.error("failed to update epoch tips", error);
      });
    },
    finish: () => invoke<void>("finish_epoch_presentation", { id }),
  };
}
