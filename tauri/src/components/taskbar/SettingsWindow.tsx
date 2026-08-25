/**
 * 設定ウィンドウコンポーネント
 *
 * システムトレイまたはトップメニューの設定ボタンから開くポップアップウィンドウ。
 * ウィンドウのタイトルバーは App.tsx の titlebar コンポーネントが担当するため、
 * 本コンポーネント内にはタイトルや閉じるボタンを含めない。
 *
 * 設定項目:
 * - 文字サイズ: プリセット（大・中・小）またはカスタム入力（8〜32px）
 * - ディスプレイ: HUD(アイコンパネル・コックピット・システムトレイ)を別のモニターに移動する
 * - ヒントバー: 画面上部のヒントバーの表示/非表示
 * - 言語: 対応言語（utils/locale.ts の SUPPORTED_LANGUAGES）を切り替える
 * - 音量: マスターボリューム・BGM・サウンドエフェクトのスライダー（ダミー）
 */

import { useCallback, useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";
import { FONT_SIZE_PRESETS, useFontSize } from "../../contexts/FontSizeContext";
import { loadAppSettings, saveAppSettings, type AppSettings } from "../../utils/settings";
import { fetchApi, ApiError } from "../../utils/api";
import { SUPPORTED_LANGUAGES, isSupportedLanguage } from "../../utils/locale";
import "./SettingsWindow.css";

/**
 * プリセットキーの表示順序
 * i18n の翻訳キー settings.fontSize.<key> と対応する
 */
const PRESET_ORDER = ["large", "medium", "small"] as const;

/** 音量項目の定義 */
const VOLUME_KEYS = [
  { key: "master", defaultValue: 80 },
  { key: "bgm", defaultValue: 60 },
  { key: "sfx", defaultValue: 70 },
] as const;

export function SettingsWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const { fontSize, setFontSize, presetKey } = useFontSize();
  const [customInput, setCustomInput] = useState(
    presetKey == null ? String(fontSize) : "",
  );

  /**
   * 音量はダミー実装。
   * 各項目を useState で保持し、スライダー操作で値を更新するが、
   * 実際の音声出力への反映は今回のスコープ外とする。
   */
  const [volumes, setVolumes] = useState<Record<string, number>>(() => {
    const initial: Record<string, number> = {};
    for (const item of VOLUME_KEYS) {
      initial[item.key] = item.defaultValue;
    }
    return initial;
  });

  /** 画面上部のヒントバーを表示するか。設定ファイルに未記録の場合は表示扱い。 */
  const [hintbarVisible, setHintbarVisible] = useState(true);

  /** GPU設定の状態: enabled=config.tomlの設定, active=実際にCuPyが動作中 */
  const [gpuEnabled, setGpuEnabled] = useState(false);
  const [gpuActive, setGpuActive] = useState(false);
  const [gpuRestarting, setGpuRestarting] = useState(false);

  /**
   * マウント時に設定ファイルから現在の設定値を復元する。
   * ファイル読み込みが失敗した場合は、現在の Context / State の値をそのまま使用する。
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
          settings.font_size >= 8 &&
          settings.font_size <= 32
        ) {
          setFontSize(settings.font_size);
          const pk = Object.entries(FONT_SIZE_PRESETS).find(
            ([, v]) => v === settings.font_size,
          )?.[0];
          setCustomInput(pk == null ? String(settings.font_size) : "");
        }
        if (settings.volumes) {
          setVolumes((prev) => ({ ...prev, ...settings.volumes }));
        }
        setHintbarVisible(settings.hintbar_visible !== false);
      })
      .catch((err) => {
        console.error("failed to load settings", err);
      });

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

    return () => {
      cancelled = true;
    };
  }, [i18n, setFontSize]);

  /**
   * 現在の UI 状態を設定ファイルに保存するヘルパー。
   * 各設定項目の変更時に呼び出され、永続化を担う。
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
   * ディスプレイ切替ボタン押下時の処理
   * direction が 1 なら次のモニター、-1 なら前のモニターにHUDを移動する。
   * Rust 側で monitor_index は既に設定ファイルに保存されるため、
   * フロントエンド側では追加の保存処理は不要。
   */
  async function handleSwitchMonitor(direction: number) {
    try {
      await invoke<{ monitorIndex: number }>("switch_monitor", { direction });
    } catch (error) {
      console.error("failed to switch monitor", error);
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
   * プリセットボタン押下時の処理
   * カスタム入力欄をクリアしてプリセット値を適用し、設定ファイルに保存する。
   */
  function handlePresetSelect(key: string) {
    const newSize = FONT_SIZE_PRESETS[key];
    setFontSize(newSize);
    setCustomInput("");
    void persistCurrentSettings({ font_size: newSize });
  }

  /**
   * カスタム入力の変更処理
   * 数値として有効な場合のみフォントサイズを更新し、設定ファイルに保存する。
   */
  function handleCustomChange(value: string) {
    setCustomInput(value);
    const parsed = Number(value);
    if (Number.isFinite(parsed) && parsed >= 8 && parsed <= 32) {
      setFontSize(parsed);
      void persistCurrentSettings({ font_size: parsed });
    }
  }

  /**
   * ヒントバーの表示切り替え処理
   * 設定ファイルへの保存で発行される settings-updated イベントを
   * ヒントバーウィンドウが受け取り、その場で表示/非表示が切り替わる。
   */
  function handleHintbarVisibleChange(next: boolean) {
    setHintbarVisible(next);
    void persistCurrentSettings({ hintbar_visible: next });
  }

  /**
   * 言語切り替え処理
   * i18n の言語を変更し、設定ファイルに永続化する。
   */
  function handleChangeLanguage(nextLang: string) {
    void i18n.changeLanguage(nextLang);
    void persistCurrentSettings({ language: nextLang });
  }

  /**
   * 音量スライダー変更処理
   * 0〜100 の範囲でダミー値を更新し、設定ファイルに保存する。
   */
  function handleVolumeChange(key: string, value: number) {
    const clamped = Math.max(0, Math.min(100, value));
    const nextVolumes = { ...volumes, [key]: clamped };
    setVolumes(nextVolumes);
    void persistCurrentSettings({ volumes: nextVolumes });
  }

  async function handleGpuToggle(enableGpu: boolean) {
    const mode = enableGpu ? t("settings.compute.gpu") : t("settings.compute.cpu");
    if (!window.confirm(t("settings.compute.confirmRestart", { mode }))) {
      return;
    }

    setGpuRestarting(true);
    const baseUrl = apiBaseUrl.replace(/\/+$/, "");

    try {
      await fetchApi(`${baseUrl}/api/v1/system/gpu-mode`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ enabled: enableGpu }),
      });

      try {
        // backend_only を明示する。genesis.py スーパーバイザ監視下(本番)では
        // バックエンドは genesis.py 内のスレッドで単独再起動できないため 409 が返る。
        // その場合は設定のみ保存済み(次回起動時に反映)としてユーザーに通知する。
        await fetchApi(`${baseUrl}/api/v1/system/shutdown?mode=backend_only`, { method: "POST" });
      } catch (err) {
        if (err instanceof ApiError && err.status === 409) {
          setGpuEnabled(enableGpu);
          alert(t("settings.compute.restartUnavailable"));
          return;
        }
        throw err;
      }

      await new Promise((resolve) => setTimeout(resolve, 1000));

      await invoke("restart_backend");
      setGpuEnabled(enableGpu);
      setGpuActive(enableGpu);
    } catch (err) {
      console.error("GPU toggle failed", err);
      alert(t("settings.compute.restartFailed"));
    } finally {
      setGpuRestarting(false);
    }
  }

  return (
    <div className="settings-body">
      <section className="settings-section">
        <h3 className="settings-section-title">
          {t("settings.fontSize.label")}
        </h3>

        <div className="settings-font-size-options">
          {PRESET_ORDER.map((key) => (
            <button
              key={key}
              className={`settings-preset-btn ${presetKey === key ? "active" : ""}`}
              onClick={() => handlePresetSelect(key)}
              type="button"
            >
              {t(`settings.fontSize.${key}`)}
              <span className="settings-preset-size">
                {FONT_SIZE_PRESETS[key]}px
              </span>
            </button>
          ))}

          <div className="settings-custom-wrapper">
            <label className="settings-custom-label">
              {t("settings.fontSize.custom")}
            </label>
            <div className="settings-custom-input-row">
              <input
                className="settings-custom-input"
                type="number"
                min={8}
                max={32}
                step={1}
                value={customInput}
                onChange={(e) => handleCustomChange(e.target.value)}
                placeholder={`${fontSize}`}
              />
              <span className="settings-custom-unit">px</span>
            </div>
          </div>
        </div>

        <p className="settings-preview-text">
          {t("settings.fontSize.preview")}: {fontSize}px
        </p>
      </section>

      <section className="settings-section">
        <h3 className="settings-section-title">
          {t("settings.monitor.label")}
        </h3>
        <div className="settings-monitor-options">
          <button
            className="settings-monitor-btn"
            onClick={() => void handleSwitchMonitor(1)}
            type="button"
            title={t("settings.monitor.prev")}
          >
            &#x2039; {t("settings.monitor.prev")}
          </button>
          <button
            className="settings-monitor-btn"
            onClick={() => void handleSwitchMonitor(-1)}
            type="button"
            title={t("settings.monitor.next")}
          >
            {t("settings.monitor.next")} &#x203A;
          </button>
        </div>
      </section>

      <section className="settings-section">
        <h3 className="settings-section-title">
          {t("settings.windowReset.label")}
        </h3>
        <div className="settings-monitor-options">
          <button
            className="settings-action-btn"
            onClick={() => void handleResetWindowGeometries()}
            type="button"
          >
            {t("settings.windowReset.button")}
          </button>
        </div>
      </section>

      <section className="settings-section">
        <h3 className="settings-section-title">
          {t("settings.hintbar.label")}
        </h3>
        <div className="settings-toggle-options">
          {([true, false] as const).map((value) => (
            <button
              key={value ? "show" : "hide"}
              className={`settings-toggle-btn ${hintbarVisible === value ? "active" : ""}`}
              onClick={() => handleHintbarVisibleChange(value)}
              type="button"
            >
              {t(value ? "settings.hintbar.show" : "settings.hintbar.hide")}
            </button>
          ))}
        </div>
        <p className="settings-preview-text">
          {t("settings.hintbar.description")}
        </p>
      </section>

      <section className="settings-section">
        <h3 className="settings-section-title">
          {t("settings.language.label")}
        </h3>
        <div className="settings-language-options">
          {SUPPORTED_LANGUAGES.map((lang) => (
            <button
              key={lang}
              className={`settings-language-btn ${i18n.language === lang ? "active" : ""}`}
              onClick={() => handleChangeLanguage(lang)}
              type="button"
            >
              {t(`settings.language.${lang}`)}
            </button>
          ))}
        </div>
      </section>

      <section className="settings-section">
        <h3 className="settings-section-title">
          {t("settings.volume.label")}
        </h3>
        <div className="settings-volume-list">
          {VOLUME_KEYS.map((item) => (
            <div key={item.key} className="settings-volume-row">
              <label className="settings-volume-label">
                {t(`settings.volume.${item.key}`)}
              </label>
              <div className="settings-volume-slider-wrapper">
                <input
                  className="settings-volume-slider"
                  type="range"
                  min={0}
                  max={100}
                  value={volumes[item.key] ?? item.defaultValue}
                  onChange={(e) => handleVolumeChange(item.key, Number(e.target.value))}
                />
                <span className="settings-volume-value">
                  {volumes[item.key] ?? item.defaultValue}%
                </span>
              </div>
            </div>
          ))}
        </div>
      </section>

      <section className="settings-section">
        <h3 className="settings-section-title">
          {t("settings.compute.label")}
        </h3>
        <div className="settings-language-options">
          {(["cpu", "gpu"] as const).map((mode) => {
            const isGpu = mode === "gpu";
            const isActive = isGpu ? gpuEnabled : !gpuEnabled;
            return (
              <button
                key={mode}
                className={`settings-language-btn ${isActive ? "active" : ""}`}
                onClick={() => void handleGpuToggle(isGpu)}
                disabled={gpuRestarting || gpuEnabled === isGpu}
                type="button"
              >
                {t(`settings.compute.${mode}`)}
              </button>
            );
          })}
        </div>
        <p className="settings-preview-text">
          {gpuRestarting
            ? t("settings.compute.restarting")
            : gpuActive
              ? t("settings.compute.statusActive")
              : t("settings.compute.statusInactive")}
        </p>
        <p className="settings-preview-text" style={{ fontSize: "0.85em", opacity: 0.7 }}>
          {t("settings.compute.restartRequired")}
        </p>
      </section>
    </div>
  );
}
