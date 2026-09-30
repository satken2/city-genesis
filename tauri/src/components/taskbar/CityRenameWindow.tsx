/**
 * 都市名の変更ウィンドウ
 *
 * window_type="city_rename" のポップアップで表示する。コックピットの都市名をクリックして開く。
 * 現在の都市名は `GET /api/v1/city/`、変更は `PATCH /api/v1/city/` の `name` で行う。
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
    <div className="city-rename">
      <div className="settings-body city-rename-body">
        <div className="settings-card">
          <SettingsRow label={t("cityRename.label")} stretch>
            <input
              id="city-rename-input"
              className="settings-number city-rename-name"
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
      </div>

      <div className="city-rename-footer">
        {error && <p className="city-rename-error" role="alert">{error}</p>}
        <div className="city-rename-actions">
          <button className="settings-btn" type="button" disabled={saving} onClick={onClose}>
            {t("common.cancel")}
          </button>
          <button
            className="settings-btn city-rename-save"
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
