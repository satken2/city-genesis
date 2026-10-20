/**
 * Credits window component
 *
 * Popup opened from the settings Information group, displaying required attribution for free assets and OSS
 * libraries.
 *
 * `CREDIT_ENTRIES` in `src/data/credits.ts` is the sole content source; this component only renders it. Update
 * that data when adding assets.
 * Do not translate author names, license names, or prescribed wording; retain their original text. Translate
 * only category headings and field labels.
 *
 * Use `plugin-opener` to open source URLs in the default browser, since opening in the WebView would leave no
 * way back.
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

/** One credit entry row */
function CreditRow({ entry }: { entry: CreditEntry }) {
  const { t } = useTranslation();

  /** Combine author/source on one row as "author / source". */
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

      {/* Show license-prescribed wording verbatim in monospace to indicate it has not been altered. */}
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

  /** Group by category, omitting headings for empty categories. */
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
