/**
 * 都市設定ウィンドウ
 *
 * window_type="city_rename" のポップアップで表示する。コックピットの都市名をクリックして開く。
 * 現在の都市名は `GET /api/v1/city/`、変更は `PATCH /api/v1/city/` の `name` で行う。
 * 近似設定は提供前の表示専用で、入力値の保持・APIへの送信は行わない。
 *
 * 変更後はグローバルイベント `city:renamed` を送出する。HUDの都市名はRust側の
 * セッションポーリング(数秒間隔)で更新されるため、待たずに即時反映させるための通知。
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit } from "@tauri-apps/api/event";

import { buildApiUrl, fetchApi, ApiError } from "../../utils/api";
import { SettingsRow } from "../common/SettingsRow";
import "./CityRenameWindow.css";

/** 都市名の最大文字数。UI(コックピット)に収まる範囲かつセーブ名として現実的な長さに制限する。 */
const CITY_NAME_MAX_LENGTH = 40;
const CANDIDATE_SETTINGS = ["goods", "jobs", "housing"] as const;

type CityRenameWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

export function CityRenameWindow({ apiBaseUrl, onClose }: CityRenameWindowProps) {
  const { t } = useTranslation();

  const [currentName, setCurrentName] = useState<string>("");
  const [inputName, setInputName] = useState<string>("");
  const [loaded, setLoaded] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string>("");

  // 現在の都市名を取得して入力欄の初期値にする
  useEffect(() => {
    const controller = new AbortController();
    (async () => {
      try {
        const res = await fetchApi<{ city: { name?: string } }>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/"),
          { signal: controller.signal },
        );
        const name = res.city?.name ?? "";
        setCurrentName(name);
        setInputName(name);
        setLoaded(true);
      } catch (err) {
        if (controller.signal.aborted) return;
        console.error("[CityRename] failed to fetch city name", err);
        setError(t("common.fetchError"));
      }
    })();
    return () => {
      controller.abort();
    };
  }, [apiBaseUrl, t]);

  const trimmedName = inputName.trim();
  const canSave = loaded && !saving && trimmedName.length > 0 && trimmedName !== currentName;

  const handleSave = useCallback(async () => {
    if (!canSave) return;
    setSaving(true);
    setError("");
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/city/"), {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name: trimmedName }),
      });
      await emit("city:renamed", { name: trimmedName });
      onClose();
    } catch (err) {
      console.error("[CityRename] failed to update city name", err);
      const code = err instanceof ApiError ? err.code : "UNKNOWN_ERROR";
      setError(t(`errors.${code}`, { defaultValue: t("errors.UNKNOWN_ERROR") }));
      setSaving(false);
    }
  }, [apiBaseUrl, canSave, onClose, t, trimmedName]);

  return (
    <div className="city-settings">
      <div className="settings-body city-settings-body">
        <section className="settings-group" aria-labelledby="city-settings-general">
          <h3 className="settings-group-title" id="city-settings-general">{t("citySettings.general")}</h3>
          <div className="settings-card">
            <SettingsRow label={t("cityRename.label")} stretch>
              <input
                id="city-rename-input"
                className="settings-number city-settings-name"
                aria-label={t("cityRename.label")}
                type="text"
                value={inputName}
                maxLength={CITY_NAME_MAX_LENGTH}
                disabled={!loaded || saving}
                autoFocus
                onChange={(event) => setInputName(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === "Enter" && !event.nativeEvent.isComposing) void handleSave();
                }}
              />
            </SettingsRow>
          </div>
        </section>

        <section className="settings-group" aria-labelledby="city-settings-approximation">
          <h3 className="settings-group-title" id="city-settings-approximation">{t("citySettings.approximation")}</h3>
          <p className="city-settings-intro">{t("citySettings.intro")}</p>
          <div className="city-settings-warning" role="note">
            <strong>{t("citySettings.warning")}</strong>
            <p>{t("citySettings.irreversible")}</p>
          </div>
          <div className="settings-card">
            {CANDIDATE_SETTINGS.map((key) => (
              <SettingsRow
                key={key}
                label={t(`citySettings.${key}.label`)}
                description={t(`citySettings.${key}.description`)}
                badge={t("settings.compute.comingSoon")}
                disabled
              >
                <input
                  className="settings-number"
                  type="number"
                  aria-label={t(`citySettings.${key}.label`)}
                  placeholder={t("citySettings.undecided")}
                  disabled
                />
                <span className="settings-unit">{t("citySettings.candidates")}</span>
              </SettingsRow>
            ))}
            <SettingsRow
              label={t("citySettings.interval.label")}
              description={t("citySettings.interval.description")}
              badge={t("settings.compute.comingSoon")}
              disabled
            >
              <input
                className="settings-number"
                type="number"
                aria-label={t("citySettings.interval.label")}
                placeholder={t("citySettings.undecided")}
                disabled
              />
              <span className="settings-unit">{t("citySettings.epochs")}</span>
            </SettingsRow>
            <SettingsRow
              label={t("citySettings.aggregation.label")}
              description={t("citySettings.aggregation.description")}
              badge={t("settings.compute.comingSoon")}
              disabled
            >
              <div className="settings-seg" role="group" aria-label={t("citySettings.aggregation.label")}>
                <button className="settings-seg-btn active" type="button" aria-pressed disabled>{t("citySettings.off")}</button>
                <button className="settings-seg-btn" type="button" aria-pressed={false} disabled>{t("citySettings.on")}</button>
              </div>
            </SettingsRow>
          </div>
        </section>
      </div>

      <div className="city-settings-footer">
        {error && <p className="city-settings-error" role="alert">{error}</p>}
        <div className="city-settings-actions">
          <button className="settings-btn" type="button" disabled={saving} onClick={onClose}>
            {t("common.cancel")}
          </button>
          <button
            className="settings-btn city-settings-save"
            type="button"
            disabled={!canSave}
            onClick={() => void handleSave()}
          >
            {saving ? t("common.saving") : t("common.save")}
          </button>
        </div>
      </div>
    </div>
  );
}
