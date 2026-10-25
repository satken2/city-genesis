/**
 * Achievement unlocked window
 *
 * No title bar (registry chromeless); close with the button on the last page.
 * Shows achievements newly unlocked in this city, one per page, with achievement.opus. Achievements unlocked by
 * something completed or opened in the city carry its location, and the camera button moves the 3D view there.
 * Names and descriptions come from i18n (steamAchievements.items.<API name>); Steamworks registers the same text.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
// Always reference audio through imports. Passing a literal path directly to Audio excludes it from the bundle.
import achievementSound from "../../assets/sounds/achievement.opus";
import type { UnlockedAchievement } from "../../utils/achievements";
import { sendCameraFocus } from "../../utils/ipc";
import "./AchievementUnlockedWindow.css";

type AchievementUnlockedWindowProps = {
  achievements: UnlockedAchievement[];
  onClose: () => void;
  /** Opening timestamp. Reopening a visible window does not remount it, so restart from the first page when it changes. */
  openedAt?: number;
};

function playSound(audio: HTMLAudioElement): void {
  audio.currentTime = 0;
  audio.play().catch((e: unknown) => console.error("[achievement_unlocked] sound failed:", e));
}

export function AchievementUnlockedWindow({ achievements, onClose, openedAt }: AchievementUnlockedWindowProps) {
  const { t } = useTranslation();
  const [pageIndex, setPageIndex] = useState(0);
  const [audio] = useState(() => new Audio(achievementSound));

  useEffect(() => {
    setPageIndex(0);
    playSound(audio);
    return () => audio.pause();
  }, [audio, openedAt]);

  if (achievements.length === 0) return null;

  const achievement = achievements[Math.min(pageIndex, achievements.length - 1)];
  const isLastPage = pageIndex >= achievements.length - 1;

  function handleNext() {
    if (isLastPage) {
      onClose();
      return;
    }
    setPageIndex((i) => i + 1);
    playSound(audio);
  }

  return (
    <div className="achievement-unlocked">
      {/* Key by page to replay the medal animation for each achievement. */}
      <div className="achievement-unlocked-medal" key={`${openedAt}-${pageIndex}`} aria-hidden={true}>
        <span className="achievement-unlocked-rays" />
        <svg viewBox="0 0 64 64">
          <circle cx="32" cy="32" r="27" className="achievement-unlocked-medal-face" />
          <circle cx="32" cy="32" r="21" className="achievement-unlocked-medal-ring" />
          <path
            d="M32 17.5l4.3 8.7 9.6 1.4-6.95 6.77 1.64 9.56L32 39.4l-8.59 4.53 1.64-9.56-6.95-6.77 9.6-1.4z"
            className="achievement-unlocked-medal-star"
          />
        </svg>
      </div>

      <h2 className="achievement-unlocked-name">{t(`steamAchievements.items.${achievement.id}.name`)}</h2>
      <p className="achievement-unlocked-desc">{t(`steamAchievements.items.${achievement.id}.description`)}</p>

      <div className="achievement-unlocked-actions">
        {achievement.polygon && achievement.polygon.length > 0 && (
          <button
            className="achievement-unlocked-button"
            type="button"
            onClick={() => void sendCameraFocus("achievement_unlocked", achievement.polygon ?? []).catch(console.error)}
          >
            {t("common.focusCamera")}
          </button>
        )}
        <button className="achievement-unlocked-button achievement-unlocked-button--primary" type="button" onClick={handleNext}>
          {isLastPage ? t("steamAchievements.close") : t("steamAchievements.next")}
        </button>
      </div>

      {achievements.length > 1 && (
        <span className="achievement-unlocked-pager">
          {pageIndex + 1} / {achievements.length}
        </span>
      )}
    </div>
  );
}
