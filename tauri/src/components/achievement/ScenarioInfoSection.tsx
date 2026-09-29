/**
 * シナリオ情報セクション（達成目標ウィンドウの先頭に置くバンド）
 *
 * シナリオ（テンプレート）のタイトル・説明・達成条件を表示する。
 * これらはテンプレート作成者が多言語で書く紹介文で、シミュレーションには影響しない。
 * ニューゲーム画面のシナリオ詳細パネルに出るのと同じ内容であり、
 * プレイ中に「このシナリオで何を目指すのか」を読み返すための場所を兼ねる。
 * 表示する言語は UI の表示言語で、その言語の文が無ければ英語を出す。
 *
 * 表示専用。紹介文の編集は、シナリオとして保存したあとの紹介情報ウィンドウ(ScenarioMetadataWindow)だけで行う
 * (多言語の入力欄と開始設定を1か所に置くため)。テンプレート作成モードでは、その案内を出す。
 * 通常プレイ中に3項目すべてが空のシナリオでは何も描画しない。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolveLocalizedText, type LocalizedText } from "../../utils/locale";
import "./ScenarioInfoSection.css";

type ScenarioInfoSectionProps = {
  apiBaseUrl: string;
  /** テンプレート作成モードかどうか。true のときは紹介文の編集場所を案内する */
  templateMode: boolean;
};

/** GET /api/v1/city のうち、このセクションが使うプロパティ */
type CityScenarioFields = {
  city: {
    scenario_title?: LocalizedText;
    scenario_description?: LocalizedText;
    scenario_objective?: LocalizedText;
  };
};

export function ScenarioInfoSection({ apiBaseUrl, templateMode }: ScenarioInfoSectionProps) {
  const { t, i18n } = useTranslation();
  const [texts, setTexts] = useState<CityScenarioFields["city"]>({});
  const [imagePreview, setImagePreview] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    const controller = new AbortController();

    async function load() {
      try {
        const res = await fetchApi<CityScenarioFields>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/"),
          { signal: controller.signal }
        );
        setTexts(res.city);

        // 既存のサムネイル画像の取得を試みる
        try {
          const thumbUrl = buildApiUrl(apiBaseUrl, "/api/v1/templates/thumbnail");
          const blob = await fetchApi<Blob>(thumbUrl, { signal: controller.signal, responseType: "blob" });
          if (!controller.signal.aborted) {
            setImagePreview(URL.createObjectURL(blob));
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

  if (!loaded) return null;

  const title = resolveLocalizedText(texts.scenario_title, i18n.language);
  const description = resolveLocalizedText(texts.scenario_description, i18n.language);
  const objective = resolveLocalizedText(texts.scenario_objective, i18n.language);

  // 紹介文が一切ないシナリオでは、通常プレイ中に空欄を出しても意味がない。
  if (!templateMode && !title && !description && !objective) return null;

  return (
    <div className="scenario-info">
      <h3 className="scenario-info__title">{t("achievements.scenarioSection")}</h3>
      {templateMode && <p className="scenario-info__read-text scenario-info__hint">{t("achievements.scenarioEditHint")}</p>}
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
    </div>
  );
}
