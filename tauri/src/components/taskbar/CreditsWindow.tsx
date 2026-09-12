/**
 * クレジットウィンドウコンポーネント
 *
 * 設定ウィンドウの「情報」グループから開くポップアップウィンドウ。
 * フリー素材やOSSライブラリの規約が要求するクレジット表記を掲載する、
 * 表記義務を果たすための画面。
 *
 * 掲載内容は `src/data/credits.ts` の `CREDIT_ENTRIES` が唯一の正で、
 * ここはその描画だけを担う(素材を追加したら向こうを更新する)。
 * 作者名・ライセンス名・規約が指定する文面は原文のまま出すため翻訳しない。
 * 翻訳するのはカテゴリ見出しと項目ラベルだけ。
 *
 * 配布元URLは WebView 内で開くと戻れなくなるため、
 * `plugin-opener` で既定ブラウザに投げる。
 */

import { useMemo } from "react";
import { openUrl } from "@tauri-apps/plugin-opener";
import { useTranslation } from "react-i18next";
import {
  CREDIT_CATEGORY_ORDER,
  CREDIT_ENTRIES,
  type CreditCategory,
  type CreditEntry,
} from "../../data/credits";
import "./CreditsWindow.css";

/** 1件分の掲載行 */
function CreditRow({ entry }: { entry: CreditEntry }) {
  const { t } = useTranslation();

  /** 作者・配布元は同じ行に「作者名 / 配布元名」で畳んで並べる */
  const attribution = [entry.author, entry.source].filter(Boolean).join(" / ");

  return (
    <div className="credits-row">
      <div className="credits-row-head">
        {entry.url != null ? (
          <button
            className="credits-title credits-title--link"
            onClick={() => void openUrl(entry.url!)}
            type="button"
            title={entry.url}
          >
            {entry.title}
          </button>
        ) : (
          <span className="credits-title">{entry.title}</span>
        )}
        {entry.license != null && <span className="credits-license">{entry.license}</span>}
      </div>

      {attribution !== "" && <div className="credits-attribution">{attribution}</div>}

      {entry.note != null && <div className="credits-note">{entry.note}</div>}

      {/* 規約が文面を指定している場合は、改変していないことが分かるよう等幅で原文を出す */}
      {entry.requiredText != null && (
        <div className="credits-required">
          <span className="credits-required-label">{t("credits.requiredText")}</span>
          <span className="credits-required-body">{entry.requiredText}</span>
        </div>
      )}
    </div>
  );
}

export function CreditsWindow() {
  const { t } = useTranslation();

  /** カテゴリごとに束ね、1件もないカテゴリは見出しごと出さない */
  const sections = useMemo(() => {
    const grouped = new Map<CreditCategory, CreditEntry[]>();
    for (const entry of CREDIT_ENTRIES) {
      const list = grouped.get(entry.category);
      if (list == null) {
        grouped.set(entry.category, [entry]);
      } else {
        list.push(entry);
      }
    }
    return CREDIT_CATEGORY_ORDER.filter((category) => grouped.has(category)).map((category) => ({
      category,
      entries: grouped.get(category)!,
    }));
  }, []);

  return (
    <div className="credits-body">
      <p className="credits-lead">{t("credits.lead")}</p>

      {sections.length === 0 ? (
        <p className="credits-empty">{t("credits.empty")}</p>
      ) : (
        sections.map((section) => (
          <section className="credits-group" key={section.category}>
            <h3 className="credits-group-title">{t(`credits.categories.${section.category}`)}</h3>
            <div className="credits-card">
              {section.entries.map((entry) => (
                <CreditRow entry={entry} key={`${section.category}:${entry.title}`} />
              ))}
            </div>
          </section>
        ))
      )}
    </div>
  );
}
