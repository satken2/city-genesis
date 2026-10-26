/**
 * アプリの設定。`src-tauri/src/settings.rs` の移植で、項目と既定値は Rust の AppSettings と同じ。
 * exe は設定ファイルに、ブラウザ版は localStorage に保存する。
 */
export interface AppSettings {
  language: string;
  font_size: number;
  volumes: Record<string, number>;
  monitor_index: number;
  hintbar_visible: boolean;
  citizen_voice_visible: boolean;
  multi_display: boolean;
  popup_monitor_index: number;
}

const STORAGE_KEY = "city-genesis.settings";

function defaults(): AppSettings {
  return {
    language: "ja",
    font_size: 13,
    volumes: { master: 80, bgm: 60, sfx: 70 },
    monitor_index: 0,
    hintbar_visible: true,
    citizen_voice_visible: true,
    multi_display: false,
    popup_monitor_index: 0,
  };
}

/** serde の `#[serde(default)]` と同じく、無い項目と型の合わない項目は既定値にする。 */
function normalize(raw: unknown): AppSettings {
  const base = defaults();
  if (raw === null || typeof raw !== "object") return base;
  const value = raw as Record<string, unknown>;
  const pick = <K extends keyof AppSettings>(key: K, valid: (v: unknown) => boolean): AppSettings[K] =>
    (valid(value[key]) ? value[key] : base[key]) as AppSettings[K];
  const isNumber = (v: unknown) => typeof v === "number" && Number.isFinite(v);
  return {
    language: pick("language", (v) => typeof v === "string"),
    font_size: pick("font_size", isNumber),
    volumes: pick("volumes", (v) => v !== null && typeof v === "object" && Object.values(v as object).every(isNumber)),
    monitor_index: pick("monitor_index", isNumber),
    hintbar_visible: pick("hintbar_visible", (v) => typeof v === "boolean"),
    citizen_voice_visible: pick("citizen_voice_visible", (v) => typeof v === "boolean"),
    multi_display: pick("multi_display", (v) => typeof v === "boolean"),
    popup_monitor_index: pick("popup_monitor_index", isNumber),
  };
}

export function loadSettings(): AppSettings {
  try {
    const text = localStorage.getItem(STORAGE_KEY);
    return text === null ? defaults() : normalize(JSON.parse(text));
  } catch (error) {
    console.error("設定の読み込みに失敗しました", error);
    return defaults();
  }
}

export function saveSettings(settings: unknown): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(normalize(settings)));
  } catch (error) {
    throw String(error);
  }
}
