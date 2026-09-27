/**
 * シナリオ情報セクション（達成目標ウィンドウの先頭に置くバンド）
 *
 * シナリオ（テンプレート）のタイトル・説明・達成条件を表示する。
 * これらはテンプレート作成者が記入するフリーテキストで、シミュレーションには影響しない。
 * ニューゲーム画面のシナリオ詳細パネルに出るのと同じ内容であり、
 * プレイ中に「このシナリオで何を目指すのか」を読み返すための場所を兼ねる。
 *
 * テンプレート作成モード(gd3.template_mode)でのみ編集フォームになり、
 * PATCH /api/v1/city で保存する。通常プレイ中は読み取り専用で、
 * 3項目すべてが空のシナリオ（＝地形から始めた都市）では何も描画しない。
 */

import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import "./ScenarioInfoSection.css";
import { ScenarioTextFields } from "../common/ScenarioTextFields";

type ScenarioInfoSectionProps = {
  apiBaseUrl: string;
  /** テンプレート作成モードかどうか。true のときだけ編集フォームを出す */
  templateMode: boolean;
};

/** GET /api/v1/city のうち、このセクションが使うプロパティ */
type CityScenarioFields = {
  city: {
    scenario_title?: string;
    scenario_description?: string;
    scenario_objective?: string;
  };
};

export function ScenarioInfoSection({ apiBaseUrl, templateMode }: ScenarioInfoSectionProps) {
  const { t } = useTranslation();
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [objective, setObjective] = useState("");
  const [imagePreview, setImagePreview] = useState<string | null>(null);
  const [imageFile, setImageFile] = useState<File | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState(false);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    const controller = new AbortController();

    async function load() {
      try {
        const res = await fetchApi<CityScenarioFields>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/"),
          { signal: controller.signal }
        );
        setTitle(res.city.scenario_title ?? "");
        setDescription(res.city.scenario_description ?? "");
        setObjective(res.city.scenario_objective ?? "");

        // 既存のサムネイル画像の取得を試みる
        try {
          const thumbUrl = buildApiUrl(apiBaseUrl, "/api/v1/templates/thumbnail");
          const blob = await fetchApi<Blob>(thumbUrl, { signal: controller.signal, responseType: "blob" });
          {
            if (!controller.signal.aborted) {
              setImagePreview(URL.createObjectURL(blob));
            }
          }
        } catch {
          /* サムネイルが無い場合は無視 */
        }
      } catch {
        /* 取得できなければ何も表示しない */
      } finally {
        if (!controller.signal.aborted) setLoaded(true);
      }
    }

    void load();
    return () => controller.abort();
  }, [apiBaseUrl]);

  const handleImageChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    setImageFile(file);
    setImagePreview(URL.createObjectURL(file));
  }, []);

  const handleClearImage = useCallback(() => {
    setImageFile(null);
    setImagePreview(null);
    if (fileInputRef.current) {
      fileInputRef.current.value = "";
    }
  }, []);

  const handleSave = useCallback(async () => {
    setSaving(true);
    setSaveError(false);
    setSaved(false);
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/city/"), {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          scenario_title: title,
          scenario_description: description,
          scenario_objective: objective,
        }),
      });

      // サムネイル画像が選択されていればアップロード
      if (imageFile) {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/templates/thumbnail"), {
          method: "POST",
          headers: { "Content-Type": imageFile.type || "image/png" },
          body: imageFile,
        });
      }

      setSaved(true);
    } catch {
      setSaveError(true);
    } finally {
      setSaving(false);
    }
  }, [apiBaseUrl, title, description, objective, imageFile]);

  if (!loaded) return null;

  // 地形から始めた都市（紹介文が一切ない）では、通常プレイ中に空欄を出しても意味がない。
  if (!templateMode && !title && !description && !objective) return null;

  return (
    <div className="scenario-info">
      <h3 className="scenario-info__title">{t("achievements.scenarioSection")}</h3>

      {templateMode ? (
        <div className="scenario-info__form">
          {/* サムネイル画像設定 */}
          <div className="scenario-info__field">
            <span className="scenario-info__label">{t("achievements.scenarioImageLabel")}</span>
            <div className="scenario-info__thumb-wrap">
              {imagePreview && (
                <img className="scenario-info__thumb-img" src={imagePreview} alt="" />
              )}
              <input
                ref={fileInputRef}
                type="file"
                accept="image/*"
                className="scenario-info__thumb-input"
                onChange={handleImageChange}
                disabled={saving}
              />
              <button
                type="button"
                className="scenario-info__thumb-btn"
                onClick={() => fileInputRef.current?.click()}
                disabled={saving}
              >
                {t("achievements.scenarioImageSelect")}
              </button>
              {imagePreview && (
                <button
                  type="button"
                  className="scenario-info__thumb-btn"
                  onClick={handleClearImage}
                  disabled={saving}
                >
                  {t("achievements.scenarioImageClear")}
                </button>
              )}
            </div>
          </div>

          <ScenarioTextFields value={{ scenario_title: title, scenario_description: description, scenario_objective: objective }} disabled={saving}
            onChange={value => { setTitle(value.scenario_title); setDescription(value.scenario_description); setObjective(value.scenario_objective); }} />

          <div className="scenario-info__actions">
            <button
              className="scenario-info__save-btn"
              type="button"
              onClick={() => void handleSave()}
              disabled={saving}
            >
              {t("achievements.scenarioSave")}
            </button>
            {saved && (
              <span className="scenario-info__result">{t("achievements.scenarioSaved")}</span>
            )}
            {saveError && (
              <span className="scenario-info__result scenario-info__result--error">
                {t("achievements.scenarioSaveFailed")}
              </span>
            )}
          </div>
        </div>
      ) : (
        <div className="scenario-info__read">
          {imagePreview && (
            <img className="scenario-info__read-thumb" src={imagePreview} alt="" />
          )}
          {title && <div className="scenario-info__read-title">{title}</div>}
          {description && <p className="scenario-info__read-text">{description}</p>}
          {objective && (
            <div className="scenario-info__read-block">
              <span className="scenario-info__label">
                {t("achievements.scenarioObjectiveLabel")}
              </span>
              <p className="scenario-info__read-text">{objective}</p>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
