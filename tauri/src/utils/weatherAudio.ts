import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import { loadAppSettings, type AppSettings } from "./settings";
import ambience from "../assets/sounds/weather/city-ambience.ogg";
import drizzle from "../assets/sounds/weather/drizzle.ogg";
import rain from "../assets/sounds/weather/rain.ogg";
import heavyRain from "../assets/sounds/weather/heavy-rain.ogg";

/** Cockpitだけが所有する環境音。長い録音はストリーミングし、PCM全体をメモリへ展開しない。 */
export class WeatherAudio {
  private channels = [ambience, drizzle, rain, heavyRain].map((url) => {
    const audio = new Audio(url);
    audio.loop = true;
    audio.preload = "none";
    audio.volume = 0;
    return { audio, gain: 0, target: 0, starting: false, retryAt: 0 };
  });
  private active = false;
  private rain = 0;
  private volume = 0;
  private disposed = false;
  private settingsRevision = 0;
  private unlisten?: UnlistenFn;
  private timer: ReturnType<typeof setInterval>;

  constructor() {
    const revision = this.settingsRevision;
    void loadAppSettings().then((settings) => {
      if (!this.disposed && revision === this.settingsRevision) this.applySettings(settings);
    }).catch(console.error);
    void listen<AppSettings>("settings-updated", ({ payload }) => {
      this.settingsRevision++;
      if (!this.disposed) this.applySettings(payload);
    }).then((unlisten) => {
      if (this.disposed) unlisten(); else this.unlisten = unlisten;
    }).catch(console.error);
    this.timer = setInterval(() => this.tick(), 50);
  }

  private applySettings(settings: AppSettings) {
    const clamp = (value: number) => Number.isFinite(value) ? Math.min(100, Math.max(0, value)) / 100 : 0;
    this.volume = clamp(settings.volumes.master ?? 80) * clamp(settings.volumes.sfx ?? 70);
    // 音量0はクロスフェードを待たず即座に反映する。
    if (this.volume === 0) this.channels.forEach(({ audio }) => { audio.volume = 0; });
    this.updateTargets();
  }

  update(active: boolean, rainLevel: number) {
    this.active = active;
    this.rain = Number.isInteger(rainLevel) && rainLevel >= 0 && rainLevel <= 3 ? rainLevel : 0;
    this.updateTargets();
    if (!active) this.channels.forEach((channel) => {
      channel.audio.pause(); channel.audio.volume = 0; channel.gain = 0;
    });
  }

  private updateTargets() {
    const levels = [0.5, 0.4, 0.6, 0.8];
    this.channels.forEach((channel, index) => {
      channel.target = this.active && (index === 0 || index === this.rain) ? levels[index] : 0;
    });
  }

  private tick() {
    if (this.disposed) return;
    this.channels.forEach((channel) => {
      channel.gain += Math.max(-1 / 30, Math.min(1 / 30, channel.target - channel.gain));
      channel.audio.volume = channel.gain * this.volume;
      if (channel.audio.volume <= 0.001) { channel.audio.pause(); return; }
      if (channel.audio.paused && !channel.starting && Date.now() >= channel.retryAt) {
        channel.starting = true;
        void channel.audio.play().then(() => {
          if (this.disposed || !this.active) channel.audio.pause();
        }).catch(() => {
          // WebViewの自動再生がまだ許可されない場合も、次の操作後に再試行できる。
          channel.retryAt = Date.now() + 2000;
        }).finally(() => { channel.starting = false; });
      }
    });
  }

  destroy() {
    this.disposed = true;
    clearInterval(this.timer);
    this.unlisten?.();
    this.channels.forEach(({ audio }) => { audio.pause(); audio.removeAttribute("src"); audio.load(); });
  }
}
