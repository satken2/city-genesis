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

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import "./ScenarioInfoSection.css";

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
      } catch {
        /* 取得できなければ何も表示しない */
      } finally {
        if (!controller.signal.aborted) setLoaded(true);
      }
    }

    void load();
    return () => controller.abort();
  }, [apiBaseUrl]);

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
      setSaved(true);
    } catch {
      setSaveError(true);
    } finally {
      setSaving(false);
    }
  }, [apiBaseUrl, title, description, objective]);

  if (!loaded) return null;

  // 地形から始めた都市（紹介文が一切ない）では、通常プレイ中に空欄を出しても意味がない。
  if (!templateMode && !title && !description && !objective) return null;

  return (
    <div className="scenario-info">
      <h3 className="scenario-info__title">{t("achievements.scenarioSection")}</h3>

      {templateMode ? (
        <div className="scenario-info__form">
          <label className="scenario-info__field">
            <span className="scenario-info__label">{t("achievements.scenarioTitleLabel")}</span>
            <input
              className="scenario-info__input"
              type="text"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              placeholder={t("achievements.scenarioTitlePlaceholder")}
              disabled={saving}
            />
          </label>

          <label className="scenario-info__field">
            <span className="scenario-info__label">{t("achievements.scenarioDescriptionLabel")}</span>
            <textarea
              className="scenario-info__textarea"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder={t("achievements.scenarioDescriptionPlaceholder")}
              disabled={saving}
              rows={3}
            />
          </label>

          <label className="scenario-info__field">
            <span className="scenario-info__label">{t("achievements.scenarioObjectiveLabel")}</span>
            <textarea
              className="scenario-info__textarea"
              value={objective}
              onChange={(e) => setObjective(e.target.value)}
              placeholder={t("achievements.scenarioObjectivePlaceholder")}
              disabled={saving}
              rows={3}
            />
          </label>

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
