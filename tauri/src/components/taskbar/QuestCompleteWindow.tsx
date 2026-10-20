/**
 * Quest completion notification window
 *
 * Celebration popup shown after epoch execution when newly completed quests have notify="modal".
 * Play tada.opus with a confetti animation.
 *
 * Show simultaneous completions one per page, using Next and Close on the last page.
 * Each page displays measured values at completion (achieved_values) and rewards (reward).
 *
 * Receive the completed quest array from the parent and call onClose after all pages have been viewed and
 * closed.
 */

import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { QuestNotification } from "../../types";
// Always reference audio through imports. Passing a literal path directly to Audio excludes it from the bundle,
// leaving the file missing and playback silent in production builds.
import tadaSound from "../../assets/sounds/tada.opus";
import { logInfoToFile } from "../../utils/log";
import { resolveLocalizedText } from "../../utils/locale";
import "./QuestCompleteWindow.css";

type QuestCompleteWindowProps = {
  quests: QuestNotification[];
  onClose: () => void;
  /**
   * Opening timestamp. Reopening a visible window does not remount it,
   * so replay the fanfare whenever this value changes.
   */
  openedAt?: number;
};

/** Number of confetti pieces, each a decorative element falling through CSS animation */
const CONFETTI_COUNT = 24;

/** Confetti color palette */
const CONFETTI_COLORS = ["#f0c040", "#f5d060", "#ffe9a0", "#e8a020"];

function formatValue(v: number | null): string {
  if (v === null || v === undefined) return "—";
  // Show up to two decimal places; preserve integers and apply digit grouping.
  return Number.isInteger(v)
    ? v.toLocaleString()
    : v.toLocaleString(undefined, { maximumFractionDigits: 2 });
}

/** Log playback success/failure to tauri.log so silence can be diagnosed from device logs. */
function playFanfare(audio: HTMLAudioElement): void {
  audio.play()
    .then(() => logInfoToFile("quest_complete: fanfare started"))
    .catch((e: unknown) => {
      const error = e instanceof Error ? e : null;
      console.error("[quest_complete] fanfare failed:", error?.name, error?.message ?? String(e));
    });
}

export function QuestCompleteWindow({ quests, onClose, openedAt }: QuestCompleteWindowProps) {
  const { t, i18n } = useTranslation();
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const [pageIndex, setPageIndex] = useState(0);

  const modalQuests = useMemo(() => quests.filter((q) => q.notify === "modal"), [quests]);

  useEffect(() => {
    const audio = new Audio(tadaSound);
    audioRef.current = audio;
    playFanfare(audio);
    return () => {
      audio.pause();
      if (audioRef.current === audio) audioRef.current = null;
    };
  }, [openedAt]);

  if (modalQuests.length === 0) {
    return null;
  }

  const quest = modalQuests[Math.min(pageIndex, modalQuests.length - 1)];
  const isLastPage = pageIndex >= modalQuests.length - 1;
  const achievedEntries = Object.values(quest.achieved_values ?? {});

  function handleNext() {
    if (isLastPage) {
      onClose();
    } else {
      setPageIndex((i) => i + 1);
      // Replay fanfare on each page change.
      if (audioRef.current) playFanfare(audioRef.current);
    }
  }

  return (
    <div className="quest-complete-overlay" onClick={handleNext}>
      {/* Confetti: include pageIndex in keys to replay animations on page changes. */}
      <div className="quest-complete-confetti" aria-hidden={true}>
        {Array.from({ length: CONFETTI_COUNT }, (_, i) => (
          <span
            key={`${pageIndex}-${i}`}
            className="quest-complete-confetti-piece"
            style={{
              left: `${(i * 100) / CONFETTI_COUNT + 2}%`,
              backgroundColor: CONFETTI_COLORS[i % CONFETTI_COLORS.length],
              animationDelay: `${(i % 8) * 0.15}s`,
              animationDuration: `${2.2 + (i % 5) * 0.35}s`,
            }}
          />
        ))}
      </div>

      <div className="quest-complete-card" onClick={(e) => e.stopPropagation()}>
        <h2 className="quest-complete-title">{t("questComplete.title")}</h2>
        <p className="quest-complete-subtitle">{t("questComplete.congratulations")}</p>

        <div className="quest-complete-item">
          <span className="quest-complete-item-name">{resolveLocalizedText(quest.name, i18n.language)}</span>
          <span className="quest-complete-item-desc">{resolveLocalizedText(quest.description, i18n.language)}</span>

          {/* Measured values at completion, shown as measured / target to capture the achievement moment. */}
          {achievedEntries.length > 0 && (
            <div className="quest-complete-values">
              {achievedEntries.map((entry, i) => (
                <span key={i} className="quest-complete-value-row">
                  <span className="quest-complete-value-current">{formatValue(entry.current)}</span>
                  <span className="quest-complete-value-sep">{entry.compare}</span>
                  <span className="quest-complete-value-target">{formatValue(entry.target)}</span>
                </span>
              ))}
            </div>
          )}

          {/* Completion reward (grant) */}
          {quest.reward?.type === "grant_money" && (
            <div className="quest-complete-reward">
              <span className="quest-complete-reward-label">{t("questComplete.reward")}</span>
              <span className="quest-complete-reward-value">
                {t("common.currency", { value: quest.reward.amount })}
              </span>
            </div>
          )}
        </div>

        {/* Page position indicator for multiple completions */}
        {modalQuests.length > 1 && (
          <span className="quest-complete-pager">
            {pageIndex + 1} / {modalQuests.length}
          </span>
        )}

        <button className="quest-complete-close" onClick={handleNext} type="button">
          {isLastPage ? t("questComplete.close") : t("questComplete.next")}
        </button>
      </div>
    </div>
  );
}
