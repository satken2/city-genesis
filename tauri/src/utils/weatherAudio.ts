import { listen, type EventCallback, type UnlistenFn } from "@tauri-apps/api/event";
import { loadAppSettings, type AppSettings } from "./settings";
import { sendAmbienceVolume } from "./ipc";

/** Cockpitが所有する音量設定の同期。音声再生はUnityが担当する。 */
export class AmbienceVolumeSync {
  private disposed = false;
  private revision = 0;
  private volume: number | null = null;
  private unlisteners: UnlistenFn[] = [];

  constructor() {
    const revision = this.revision;
    void loadAppSettings().then(settings => {
      if (!this.disposed && revision === this.revision) this.apply(settings);
    }).catch(console.error);
    this.subscribe<AppSettings>("settings-updated", ({ payload }) => {
      this.revision++;
      if (!this.disposed) this.apply(payload);
    });
    // Unityは起動(初回・再起動とも)のたびに graphic-reconnected で知らされるので、そのたびに送り直す。
    // Unityが不在の間の送信はMMFに溜まり続けて満杯にするため、周期的な再送はしない。
    this.subscribe("graphic-reconnected", () => this.send());
  }

  private subscribe<T>(event: string, handler: EventCallback<T>) {
    void listen<T>(event, handler)
      .then(fn => { if (this.disposed) fn(); else this.unlisteners.push(fn); })
      .catch(console.error);
  }

  private apply(settings: AppSettings) {
    const clamp = (n: number) => Number.isFinite(n) ? Math.max(0, Math.min(100, n)) / 100 : 0;
    this.volume = clamp(settings.volumes.master ?? 80) * clamp(settings.volumes.sfx ?? 70);
    this.send();
  }

  private send() {
    if (!this.disposed && this.volume !== null) void sendAmbienceVolume(this.volume).catch(console.error);
  }

  destroy() { this.disposed = true; this.unlisteners.forEach(fn => fn()); }
}
