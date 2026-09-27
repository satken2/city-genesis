/**
 * 設定ウィンドウコンポーネント
 *
 * システムトレイまたはトップメニューの設定ボタンから開くポップアップウィンドウ。
 * ウィンドウのタイトルバーは App.tsx の titlebar コンポーネントが担当するため、
 * 本コンポーネント内にはタイトルや閉じるボタンを含めない。
 *
 * 設定項目は5つのグループに分けて並べる:
 * - 一般: 言語（対応言語は utils/locale.ts の SUPPORTED_LANGUAGES）、
 *   文字サイズ（プリセット大・中・小またはカスタム入力 8〜32px）、ヒントバーの表示
 * - 画面: HUD(アイコンパネル・コックピット・システムトレイ)を別のモニターに移動する、
 *   ウィンドウ位置・サイズのリセット
 * - サウンド: マスターボリューム・BGM・サウンドエフェクトのスライダー（ダミー）
 * - システム: GPUアクセラレーションのCPU/GPU切り替え
 * - 情報: クレジット表示（素材・OSSの表記義務を果たすクレジットウィンドウを開く）
 */

import {
  useCallback,
  useEffect,
  useState,
  type CSSProperties,
} from "react";
import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";
import { FONT_SIZE_PRESETS, useFontSize } from "../../contexts/FontSizeContext";
import { loadAppSettings, saveAppSettings, type AppSettings } from "../../utils/settings";
import { fetchApi, ApiError } from "../../utils/api";
import { requestConfirm } from "../../utils/confirmDialog";
import {
  SUPPORTED_LANGUAGES,
  isSupportedLanguage,
  resolveSupportedLanguage,
} from "../../utils/locale";
import { SettingsRow } from "../common/SettingsRow";
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

/** 文字サイズのカスタム入力で受け付ける範囲(px) */
const FONT_SIZE_MIN = 8;
const FONT_SIZE_MAX = 32;

/**
 * GPUアクセラレーション設定を操作可能にするか。
 * 提供準備が整うまで Coming soon の札を出して行ごと無効化する。
 * true に戻すだけで状態取得・切り替え・再起動の一連の動作が復活する。
 */
const COMPUTE_SETTING_ENABLED: boolean = false;

export function SettingsWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const { fontSize, setFontSize, presetKey } = useFontSize();

  /**
   * カスタム入力欄の文字列。
   * 入力途中の未確定な値(空文字や範囲外)を保持するために state を分けており、
   * プリセット選択時と入力確定時に現在のフォントサイズへ揃える。
   */
  const [customInput, setCustomInput] = useState(String(fontSize));

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

  /**
   * クレジットウィンドウを開く。
   * 設定ウィンドウとは別ウィンドウなので、設定を開いたまま並べて読める。
   */
  async function handleOpenCredits() {
    try {
      await invoke("open_registered_window", { windowType: "credits" });
    } catch (error) {
      console.error("failed to open credits", error);
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
   * プリセット値を適用し、カスタム入力欄の表示もその値に揃える。
   */
  function handlePresetSelect(key: string) {
    const newSize = FONT_SIZE_PRESETS[key];
    setFontSize(newSize);
    setCustomInput(String(newSize));
    void persistCurrentSettings({ font_size: newSize });
  }

  /**
   * カスタム入力の変更処理
   * 数値として有効な場合のみフォントサイズを更新し、設定ファイルに保存する。
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
   * カスタム入力欄からフォーカスが外れたときの処理
   * 未確定の入力(空文字・範囲外)が残ると現在値と表示が食い違うため、実際の値へ戻す。
   */
  function handleCustomBlur() {
    setCustomInput(String(fontSize));
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

  // プルダウンは必ずどれか1つが選ばれている状態にするため、地域付きコードを丸めて現在値にする
  const currentLanguage = resolveSupportedLanguage(i18n.language);

  return (
    <div className="settings-body">
      <section className="settings-group">
        <h3 className="settings-group-title">{t("settings.groups.general")}</h3>
        <div className="settings-card">
          <SettingsRow label={t("settings.language.label")}>
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
    </div>
  );
}
