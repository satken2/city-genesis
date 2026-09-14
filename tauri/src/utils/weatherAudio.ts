import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import { loadAppSettings, type AppSettings } from "./settings";
import { sendAmbienceVolume } from "./ipc";

/** Cockpitが所有する音量設定の同期。音声再生はUnityが担当する。 */
export class AmbienceVolumeSync {
  private disposed = false;
  private revision = 0;
  private volume: number | null = null;
  private unlisten?: UnlistenFn;
  private timer: ReturnType<typeof setInterval>;

  constructor() {
    const revision = this.revision;
    void loadAppSettings().then(settings => {
      if (!this.disposed && revision === this.revision) this.apply(settings);
    }).catch(console.error);
    void listen<AppSettings>("settings-updated", ({ payload }) => {
      this.revision++;
      if (!this.disposed) this.apply(payload);
    }).then(fn => { if (this.disposed) fn(); else this.unlisten = fn; }).catch(console.error);
    // Unityの後起動・再起動・通知取りこぼしでも音量を復元する。
    this.timer = setInterval(() => this.send(), 2000);
  }

  private apply(settings: AppSettings) {
    const clamp = (n: number) => Number.isFinite(n) ? Math.max(0, Math.min(100, n)) / 100 : 0;
    this.volume = clamp(settings.volumes.master ?? 80) * clamp(settings.volumes.sfx ?? 70);
    this.send();
  }

  private send() {
    if (!this.disposed && this.volume !== null) void sendAmbienceVolume(this.volume).catch(console.error);
  }

  destroy() { this.disposed = true; clearInterval(this.timer); this.unlisten?.(); }
}
