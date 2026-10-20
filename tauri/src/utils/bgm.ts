import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import { loadAppSettings, type AppSettings } from "./settings";
import { parseDaysSinceEpoch } from "./date";

import bgmDefault from "../assets/sounds/bgm-default.opus";
import bgm01 from "../assets/sounds/bgm-01.opus";
import bgm04 from "../assets/sounds/bgm-04.opus";
import bgm05 from "../assets/sounds/bgm-05.opus";
import bgm07 from "../assets/sounds/bgm-07.opus";
import bgm09 from "../assets/sounds/bgm-09.opus";
import bgm10 from "../assets/sounds/bgm-10.opus";
import bgm12 from "../assets/sounds/bgm-12.opus";
import bgmResult from "../assets/sounds/bgm-result.opus";

const BGM_TRACKS: Record<string, string> = {
  default: bgmDefault,
  result: bgmResult,
  "01": bgm01,
  "04": bgm04,
  "05": bgm05,
  "07": bgm07,
  "09": bgm09,
  "10": bgm10,
  "12": bgm12,
};

export type BgmRequest = {
  id: string;
  track: string;
  priority: number;
};

export type SessionStatePayload = {
  current_save_id: string | null;
  session?: Record<string, unknown>;
  city?: Record<string, unknown>;
  apiBaseUrl: string;
  isLoading?: boolean;
};

export class BgmManager {
  private audio: HTMLAudioElement | null = null;
  private currentTrackKey: string | null = null;
  private isPlaying: boolean = false;

  private masterVolume: number = 80;
  private bgmVolume: number = 60;

  private isGameActive: boolean = false;
  private currentMonth: number | null = null;

  private overrideStack: BgmRequest[] = [];
  private unlistenFns: UnlistenFn[] = [];

  // Whether destroy() has run. listen() registers
  // asynchronously via a Promise, so its unlisten
  // function may not yet be in unlistenFns when destroy()
  // runs. The destroyed flag enables
  // self-unsubscription/early return to prevent leaked listeners
  // and duplicate BGM playback during StrictMode double mounts, etc.
  private destroyed: boolean = false;

  constructor() {
    // Load initial volume settings
    void this.loadVolumeSettings();

    // Listen to volume settings changes
    listen<AppSettings>("settings-updated", (event) => {
      if (this.destroyed) return;
      const settings = event.payload;
      if (settings.volumes) {
        this.masterVolume = settings.volumes.master ?? 80;
        this.bgmVolume = settings.volumes.bgm ?? 60;
        this.updateVolume();
      }
    }).then((unlisten) => {
      if (this.destroyed) {
        unlisten();
      } else {
        this.unlistenFns.push(unlisten);
      }
    }).catch(console.error);

    // Listen to session state changes (game activation, loading, and time updates)
    listen<SessionStatePayload>("session-state", (event) => {
      if (this.destroyed) return;
      const payload = event.payload;
      const isGameActive = payload.current_save_id != null && !payload.isLoading;

      const cityData = payload.city?.city as Record<string, unknown> | undefined;
      const currentDate = (cityData?.current_date as number) ?? null;

      let month: number | null = null;
      if (currentDate != null) {
        const parsed = parseDaysSinceEpoch(currentDate);
        month = parsed.month;
      }

      this.updateGameState(isGameActive, month);
    }).then((unlisten) => {
      if (this.destroyed) {
        unlisten();
      } else {
        this.unlistenFns.push(unlisten);
      }
    }).catch(console.error);

    // Listen to BGM request override event
    listen<{ id: string; track: string; priority?: number }>("bgm:request", (event) => {
      if (this.destroyed) return;
      const payload = event.payload;
      const priority = payload.priority ?? 0;
      this.requestBgm(payload.id, payload.track, priority);
    }).then((unlisten) => {
      if (this.destroyed) {
        unlisten();
      } else {
        this.unlistenFns.push(unlisten);
      }
    }).catch(console.error);

    // Listen to BGM release override event
    listen<{ id: string }>("bgm:release", (event) => {
      if (this.destroyed) return;
      const payload = event.payload;
      this.releaseBgm(payload.id);
    }).then((unlisten) => {
      if (this.destroyed) {
        unlisten();
      } else {
        this.unlistenFns.push(unlisten);
      }
    }).catch(console.error);

    // Stop BGM during epoch presentation (confirmation through results closure). The core
    // date advances during execution, so prevent monthly tracks switching mid-presentation.
    // Use lower priority than the results window's request (10) so its music plays while visible.
    listen<{ executing: boolean }>("epoch:executing", (event) => {
      if (this.destroyed) return;
      if (event.payload.executing) {
        this.requestBgm("epoch_executing", "none", 5);
      } else {
        this.releaseBgm("epoch_executing");
      }
    }).then((unlisten) => {
      if (this.destroyed) {
        unlisten();
      } else {
        this.unlistenFns.push(unlisten);
      }
    }).catch(console.error);
  }

  private async loadVolumeSettings() {
    try {
      const settings = await loadAppSettings();
      if (settings.volumes) {
        this.masterVolume = settings.volumes.master ?? 80;
        this.bgmVolume = settings.volumes.bgm ?? 60;
        this.updateVolume();
      }
    } catch (err) {
      console.error("[BgmManager] Failed to load volume settings initially:", err);
    }
  }

  private updateVolume() {
    if (this.audio) {
      const volumeScale = (this.masterVolume / 100) * (this.bgmVolume / 100);
      this.audio.volume = Math.max(0, Math.min(1, volumeScale));
    }
  }

  public updateGameState(isGameActive: boolean, month: number | null) {
    let changed = false;
    if (this.isGameActive !== isGameActive) {
      this.isGameActive = isGameActive;
      changed = true;
    }
    if (this.currentMonth !== month) {
      this.currentMonth = month;
      changed = true;
    }

    if (changed) {
      this.evaluateTrack();
    }
  }

  public requestBgm(id: string, track: string, priority: number) {
    // Remove existing request from the same id if any
    this.overrideStack = this.overrideStack.filter((req) => req.id !== id);
    // Push new request
    this.overrideStack.push({ id, track, priority });
    // Sort by priority descending
    this.overrideStack.sort((a, b) => b.priority - a.priority);

    this.evaluateTrack();
  }

  public releaseBgm(id: string) {
    this.overrideStack = this.overrideStack.filter((req) => req.id !== id);
    this.evaluateTrack();
  }

  private evaluateTrack() {
    if (!this.isGameActive) {
      this.stop();
      return;
    }

    let targetTrackKey = "default";

    if (this.overrideStack.length > 0) {
      targetTrackKey = this.overrideStack[0].track;
    } else if (this.currentMonth !== null) {
      const monthStr = this.currentMonth.toString().padStart(2, "0");
      if (BGM_TRACKS[monthStr]) {
        targetTrackKey = monthStr;
      }
    }

    if (targetTrackKey === "none") {
      this.stop();
      return;
    }

    if (this.currentTrackKey === targetTrackKey && this.isPlaying) {
      return;
    }

    this.play(targetTrackKey);
  }

  private play(trackKey: string) {
    if (this.destroyed) return;
    const src = BGM_TRACKS[trackKey] || BGM_TRACKS.default;

    if (this.audio) {
      this.audio.pause();
      this.audio = null;
    }

    this.audio = new Audio(src);
    this.audio.loop = true;
    this.updateVolume();

    this.audio.play().then(() => {
      this.isPlaying = true;
      this.currentTrackKey = trackKey;
    }).catch((err) => {
      console.error(`[BgmManager] Failed to play BGM track: ${trackKey}`, err);
    });
  }

  private stop() {
    if (this.audio) {
      this.audio.pause();
      this.audio = null;
    }
    this.isPlaying = false;
    this.currentTrackKey = null;
  }

  public destroy() {
    this.destroyed = true;
    this.stop();
    for (const unlisten of this.unlistenFns) {
      unlisten();
    }
    this.unlistenFns = [];
  }
}
