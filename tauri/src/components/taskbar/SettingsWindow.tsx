/**
 * Settings window component
 *
 * Popup opened from settings in the system tray or top menu. App.tsx's titlebar component provides the title
 * bar, so omit title/close controls here.
 *
 * Six groups:
 * - General: language (SUPPORTED_LANGUAGES in utils/locale.ts), font size (large/medium/small presets or 8–32px
 *   custom input), hint bar visibility.
 * - Display: move HUD (icon panel/cockpit/system tray) and 3D view (Unity window) independently between
 *   monitors; multi-display mode and destination for popups separate from HUD; reset window positions/sizes.
 * - Sound: master/BGM/sound effect sliders (placeholders).
 * - System: CPU/GPU acceleration selection.
 * - Information: open credits for required asset/OSS attribution.
 * - Developer settings: open the connected backend's Swagger in the default browser.
 */

import {
  useCallback,
  useEffect,
  useState,
  type CSSProperties,
} from "react";
import { invoke } from "@tauri-apps/api/core";
import { openUrl } from "@tauri-apps/plugin-opener";
import { useTranslation } from "react-i18next";
import { FONT_SIZE_PRESETS, useFontSize } from "../../contexts/FontSizeContext";
import { loadAppSettings, saveAppSettings, type AppSettings } from "../../utils/settings";
import { fetchApi, buildApiUrl } from "../../utils/api";
import { requestConfirm } from "../../utils/confirmDialog";
import { sendMoveGraphicDisplay } from "../../utils/ipc";
import {
  SUPPORTED_LANGUAGES,
  isSupportedLanguage,
  resolveSupportedLanguage,
} from "../../utils/locale";
import { SettingsRow } from "../common/SettingsRow";
import "./SettingsWindow.css";

/**
 * Preset display order, matching i18n settings.fontSize.<key>.
 */
const PRESET_ORDER = ["large", "medium", "small"] as const;

/** Volume setting definitions */
const VOLUME_KEYS = [
  { key: "master", defaultValue: 80 },
  { key: "bgm", defaultValue: 60 },
  { key: "sfx", defaultValue: 70 },
] as const;

/** Accepted custom font size range in pixels */
const FONT_SIZE_MIN = 8;
const FONT_SIZE_MAX = 32;

/**
 * Whether GPU acceleration settings are enabled.
 * Until ready, show Coming Soon and disable the whole row.
 * Setting true restores state retrieval, switching, and restart behavior.
 */
const COMPUTE_SETTING_ENABLED: boolean = false;

export function SettingsWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const { fontSize, setFontSize, presetKey } = useFontSize();

  /**
   * Custom input string, kept in separate state to retain incomplete values such as blanks or out-of-range input.
   * Synchronize with the actual font size on preset selection and input commitment.
   */
  const [customInput, setCustomInput] = useState(String(fontSize));

  /**
   * Volume controls are placeholders.
   * Keep each value in useState and update on slider interaction; applying them to actual audio output is outside
   * this scope.
   */
  const [volumes, setVolumes] = useState<Record<string, number>>(() => {
    const initial: Record<string, number> = {};
    for (const item of VOLUME_KEYS) {
      initial[item.key] = item.defaultValue;
    }
    return initial;
  });

  /** Whether to show the top hint bar. Default to visible if absent from settings. */
  const [hintbarVisible, setHintbarVisible] = useState(true);

  const [multiDisplay, setMultiDisplay] = useState(false);

  /** GPU state: enabled = config.toml setting; active = CuPy actually running. */
  const [gpuEnabled, setGpuEnabled] = useState(false);
  const [gpuActive, setGpuActive] = useState(false);
  const [gpuRestarting, setGpuRestarting] = useState(false);

  /**
   * Restore settings from the configuration file on mount.
   * If reading fails, retain current Context/State values.
   */
  useEffect(() => {
    let cancelled = false;
    loadAppSettings()
      .then((settings) => {
        if (cancelled) return;
        if (isSupportedLanguage(settings.language)) {
          void i18n.changeLanguage(settings.language);
        }
        if (
          Number.isFinite(settings.font_size) &&
          settings.font_size >= FONT_SIZE_MIN &&
          settings.font_size <= FONT_SIZE_MAX
        ) {
          setFontSize(settings.font_size);
          setCustomInput(String(settings.font_size));
        }
        if (settings.volumes) {
          setVolumes((prev) => ({ ...prev, ...settings.volumes }));
        }
        setHintbarVisible(settings.hintbar_visible !== false);
        setMultiDisplay(settings.multi_display === true);
      })
      .catch((err) => {
        console.error("failed to load settings", err);
      });

    if (COMPUTE_SETTING_ENABLED) {
      fetchApi<{ enabled: boolean; active: boolean }>(
        `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/system/gpu-status`
      )
        .then((data) => {
          if (!cancelled) {
            setGpuEnabled(data.enabled);
            setGpuActive(data.active);
          }
        })
        .catch(() => {});
    }

    return () => {
      cancelled = true;
    };
  }, [i18n, setFontSize]);

  /**
   * Persist the current UI state to the settings file.
   * Called when each setting changes.
   */
  const persistCurrentSettings = useCallback(
    async (updates: Partial<AppSettings>) => {
      try {
        const current = await loadAppSettings();
        await saveAppSettings({ ...current, ...updates });
      } catch (error) {
        console.error("failed to save settings", error);
      }
    },
    [],
  );

  /**
   * Display switch handler.
   * Move HUD to the next monitor for direction = 1, previous for -1.
   * Rust already persists monitor_index, so no additional frontend save is needed.
   */
  async function handleSwitchMonitor(direction: number) {
    try {
      await invoke<{ monitorIndex: number }>("switch_monitor", { direction });
    } catch (error) {
      console.error("failed to switch monitor", error);
    }
  }

  /** Move the 3D view (Unity window) to an adjacent display independently of HUD/popup destinations. */
  async function handleMoveGraphicDisplay(direction: 1 | -1) {
    try {
      await sendMoveGraphicDisplay(direction);
    } catch (error) {
      console.error("failed to move graphic display", error);
    }
  }

  /**
   * Toggle multi-display mode. Rust chooses the destination, moves open windows, and saves settings.
   * This settings window is also a popup and moves to the selected monitor.
   */
  async function handleMultiDisplayChange(next: boolean) {
    setMultiDisplay(next);
    try {
      await invoke("set_multi_display", { enabled: next });
    } catch (error) {
      console.error("failed to set multi display", error);
    }
  }

  /** Cycle the popup destination in multi-display mode, skipping the HUD monitor. */
  async function handleSwitchPopupMonitor(direction: number) {
    try {
      await invoke("switch_popup_monitor", { direction });
    } catch (error) {
      console.error("failed to switch popup monitor", error);
    }
  }

  /**
   * Open credits in a separate window so it can be read alongside settings.
   */
  async function handleOpenCredits() {
    try {
      await invoke("open_registered_window", { windowType: "credits" });
    } catch (error) {
      console.error("failed to open credits", error);
    }
  }

  async function handleOpenSwagger() {
    try {
      await openUrl(buildApiUrl(apiBaseUrl, "/docs"));
    } catch (error) {
      console.error("failed to open Swagger", error);
    }
  }

  async function handleResetWindowGeometries() {
    try {
      await invoke("reset_window_geometries");
    } catch (error) {
      console.error("failed to reset window geometries", error);
    }
  }

  /**
   * Preset button handler.
   * Apply the preset and synchronize the custom input display.
   */
  function handlePresetSelect(key: string) {
    const newSize = FONT_SIZE_PRESETS[key];
    setFontSize(newSize);
    setCustomInput(String(newSize));
    void persistCurrentSettings({ font_size: newSize });
  }

  /**
   * Custom input change handler.
   * Update font size and save settings only for valid numeric input.
   */
  function handleCustomChange(value: string) {
    setCustomInput(value);
    const parsed = Number(value);
    if (Number.isFinite(parsed) && parsed >= FONT_SIZE_MIN && parsed <= FONT_SIZE_MAX) {
      setFontSize(parsed);
      void persistCurrentSettings({ font_size: parsed });
    }
  }

  /**
   * Custom input blur handler.
   * Revert incomplete input (blank/out of range) to the actual value to prevent display/state disagreement.
   */
  function handleCustomBlur() {
    setCustomInput(String(fontSize));
  }

  /**
   * Hint bar visibility handler.
   * Saving settings emits settings-updated, which the hint bar receives to change visibility immediately.
   */
  function handleHintbarVisibleChange(next: boolean) {
    setHintbarVisible(next);
    void persistCurrentSettings({ hintbar_visible: next });
  }

  /**
   * Language switch handler.
   * Change i18n language and persist it in settings.
   */
  function handleChangeLanguage(nextLang: string) {
    void i18n.changeLanguage(nextLang);
    void persistCurrentSettings({ language: nextLang });
  }

  /**
   * Volume slider handler.
   * Update placeholder values within 0–100 and save settings.
   */
  function handleVolumeChange(key: string, value: number) {
    const clamped = Math.max(0, Math.min(100, value));
    const nextVolumes = { ...volumes, [key]: clamped };
    setVolumes(nextVolumes);
    void persistCurrentSettings({ volumes: nextVolumes });
  }

  async function handleGpuToggle(enableGpu: boolean) {
    const mode = enableGpu ? t("settings.compute.gpu") : t("settings.compute.cpu");
    const confirmed = await requestConfirm({
      message: t("settings.compute.confirmRestart", { mode }),
      confirmLabel: t("settings.compute.restartAction"),
    });
    if (!confirmed) return;

    setGpuRestarting(true);
    const baseUrl = apiBaseUrl.replace(/\/+$/, "");

    try {
      await fetchApi(`${baseUrl}/api/v1/system/gpu-mode`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ enabled: enableGpu }),
      });
      // The core chooses its compute device at startup; save the setting for the next launch.
      setGpuEnabled(enableGpu);
      alert(t("settings.compute.restartUnavailable"));
    } catch (err) {
      console.error("GPU toggle failed", err);
      alert(t("settings.compute.restartFailed"));
    } finally {
      setGpuRestarting(false);
    }
  }

  // Normalize region-specific language codes so the dropdown always has a selected option.
  const currentLanguage = resolveSupportedLanguage(i18n.language);

  return (
    <div className="settings-body">
      <section className="settings-group">
        <h3 className="settings-group-title">{t("settings.groups.general")}</h3>
        <div className="settings-card">
          <SettingsRow
            label={t("settings.language.label")}
            description={t("settings.language.description")}
          >
            <div className="settings-select-wrap">
              <select
                className="settings-select"
                value={currentLanguage}
                onChange={(e) => handleChangeLanguage(e.target.value)}
              >
                {SUPPORTED_LANGUAGES.map((lang) => (
                  <option key={lang} value={lang}>
                    {t(`settings.language.${lang}`)}
                  </option>
                ))}
              </select>
            </div>
          </SettingsRow>

          <SettingsRow
            label={t("settings.fontSize.label")}
            description={`${t("settings.fontSize.preview")}: ${fontSize}px`}
          >
            <div className="settings-seg">
              {PRESET_ORDER.map((key) => (
                <button
                  key={key}
                  className={`settings-seg-btn ${presetKey === key ? "active" : ""}`}
                  onClick={() => handlePresetSelect(key)}
                  type="button"
                >
                  {t(`settings.fontSize.${key}`)}
                  <span className="settings-seg-sub">{FONT_SIZE_PRESETS[key]}px</span>
                </button>
              ))}
            </div>
            <input
              className="settings-number"
              type="number"
              min={FONT_SIZE_MIN}
              max={FONT_SIZE_MAX}
              step={1}
              value={customInput}
              onChange={(e) => handleCustomChange(e.target.value)}
              onBlur={handleCustomBlur}
              title={t("settings.fontSize.custom")}
              aria-label={t("settings.fontSize.custom")}
            />
            <span className="settings-unit">px</span>
          </SettingsRow>

          <SettingsRow
            label={t("settings.hintbar.label")}
            description={t("settings.hintbar.description")}
          >
            <div className="settings-seg">
              {([true, false] as const).map((value) => (
                <button
                  key={value ? "show" : "hide"}
                  className={`settings-seg-btn ${hintbarVisible === value ? "active" : ""}`}
                  onClick={() => handleHintbarVisibleChange(value)}
                  type="button"
                >
                  {t(value ? "settings.hintbar.show" : "settings.hintbar.hide")}
                </button>
              ))}
            </div>
          </SettingsRow>
        </div>
      </section>

      <section className="settings-group">
        <h3 className="settings-group-title">{t("settings.groups.display")}</h3>
        <div className="settings-card">
          <SettingsRow label={t("settings.monitor.label")}>
            <button
              className="settings-btn"
              onClick={() => void handleSwitchMonitor(1)}
              type="button"
              title={t("settings.monitor.prev")}
            >
              <span className="settings-btn-chevron">&#x2039;</span> {t("settings.monitor.prev")}
            </button>
            <button
              className="settings-btn"
              onClick={() => void handleSwitchMonitor(-1)}
              type="button"
              title={t("settings.monitor.next")}
            >
              {t("settings.monitor.next")} <span className="settings-btn-chevron">&#x203A;</span>
            </button>
          </SettingsRow>

          <SettingsRow label={t("settings.graphicMonitor.label")}>
            <button
              className="settings-btn"
              onClick={() => void handleMoveGraphicDisplay(-1)}
              type="button"
              title={t("settings.monitor.prev")}
            >
              <span className="settings-btn-chevron">&#x2039;</span> {t("settings.monitor.prev")}
            </button>
            <button
              className="settings-btn"
              onClick={() => void handleMoveGraphicDisplay(1)}
              type="button"
              title={t("settings.monitor.next")}
            >
              {t("settings.monitor.next")} <span className="settings-btn-chevron">&#x203A;</span>
            </button>
          </SettingsRow>

          <SettingsRow
            label={t("settings.multiDisplay.label")}
            description={t("settings.multiDisplay.description")}
          >
            <div className="settings-seg">
              {([true, false] as const).map((value) => (
                <button
                  key={value ? "on" : "off"}
                  className={`settings-seg-btn ${multiDisplay === value ? "active" : ""}`}
                  onClick={() => void handleMultiDisplayChange(value)}
                  type="button"
                >
                  {t(value ? "settings.multiDisplay.on" : "settings.multiDisplay.off")}
                </button>
              ))}
            </div>
          </SettingsRow>

          <SettingsRow label={t("settings.popupMonitor.label")} disabled={!multiDisplay}>
            <button
              className="settings-btn"
              onClick={() => void handleSwitchPopupMonitor(-1)}
              type="button"
              disabled={!multiDisplay}
              title={t("settings.monitor.prev")}
            >
              <span className="settings-btn-chevron">&#x2039;</span> {t("settings.monitor.prev")}
            </button>
            <button
              className="settings-btn"
              onClick={() => void handleSwitchPopupMonitor(1)}
              type="button"
              disabled={!multiDisplay}
              title={t("settings.monitor.next")}
            >
              {t("settings.monitor.next")} <span className="settings-btn-chevron">&#x203A;</span>
            </button>
          </SettingsRow>

          <SettingsRow label={t("settings.windowReset.label")}>
            <button
              className="settings-btn"
              onClick={() => void handleResetWindowGeometries()}
              type="button"
            >
              {t("settings.windowReset.action")}
            </button>
          </SettingsRow>
        </div>
      </section>

      <section className="settings-group">
        <h3 className="settings-group-title">{t("settings.groups.sound")}</h3>
        <div className="settings-card">
          {VOLUME_KEYS.map((item) => {
            const value = volumes[item.key] ?? item.defaultValue;
            return (
              <SettingsRow key={item.key} label={t(`settings.volume.${item.key}`)} stretch>
                <input
                  className="settings-slider"
                  type="range"
                  min={0}
                  max={100}
                  value={value}
                  style={{ "--settings-fill": `${value}%` } as CSSProperties}
                  onChange={(e) => handleVolumeChange(item.key, Number(e.target.value))}
                />
                <span className="settings-slider-value">{value}%</span>
              </SettingsRow>
            );
          })}
        </div>
      </section>

      <section className="settings-group">
        <h3 className="settings-group-title">{t("settings.groups.system")}</h3>
        <div className="settings-card">
          <SettingsRow
            label={t("settings.compute.label")}
            badge={COMPUTE_SETTING_ENABLED ? undefined : t("settings.compute.comingSoon")}
            disabled={!COMPUTE_SETTING_ENABLED}
            description={
              COMPUTE_SETTING_ENABLED ? (
                <>
                  <span
                    className={`settings-status-dot ${
                      gpuRestarting
                        ? "settings-status-dot--busy"
                        : gpuActive
                          ? "settings-status-dot--on"
                          : ""
                    }`}
                  />
                  {gpuRestarting
                    ? t("settings.compute.restarting")
                    : gpuActive
                      ? t("settings.compute.statusActive")
                      : t("settings.compute.statusInactive")}
                  {" / "}
                  {t("settings.compute.restartRequired")}
                </>
              ) : undefined
            }
          >
            <div className="settings-seg">
              {(["cpu", "gpu"] as const).map((mode) => {
                const isGpu = mode === "gpu";
                const isActive = isGpu ? gpuEnabled : !gpuEnabled;
                return (
                  <button
                    key={mode}
                    className={`settings-seg-btn ${isActive ? "active" : ""}`}
                    onClick={() => void handleGpuToggle(isGpu)}
                    disabled={!COMPUTE_SETTING_ENABLED || gpuRestarting || gpuEnabled === isGpu}
                    type="button"
                  >
                    {t(`settings.compute.${mode}`)}
                  </button>
                );
              })}
            </div>
          </SettingsRow>
        </div>
      </section>

      <section className="settings-group">
        <h3 className="settings-group-title">{t("settings.groups.about")}</h3>
        <div className="settings-card">
          <SettingsRow
            label={t("settings.credits.label")}
            description={t("settings.credits.description")}
          >
            <button
              className="settings-btn"
              onClick={() => void handleOpenCredits()}
              type="button"
            >
              {t("settings.credits.action")}
            </button>
          </SettingsRow>
        </div>
      </section>

      <section className="settings-group">
        <h3 className="settings-group-title">{t("settings.groups.developer")}</h3>
        <div className="settings-card">
          <SettingsRow
            label={t("settings.swagger.label")}
            description={t("settings.swagger.description")}
          >
            <button
              className="settings-btn"
              onClick={() => void handleOpenSwagger()}
              type="button"
              disabled={!apiBaseUrl}
              title={apiBaseUrl ? buildApiUrl(apiBaseUrl, "/docs") : undefined}
              data-guide="settings-open-swagger"
            >
              {t("settings.swagger.action")}
            </button>
          </SettingsRow>
        </div>
      </section>
    </div>
  );
}
