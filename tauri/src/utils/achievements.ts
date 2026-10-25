import { invoke } from "@tauri-apps/api/core";
import { buildApiUrl, fetchApi } from "./api";

/** Achievement the core unlocked in this city. polygon is the planar location [[x, z], ...], or null for city-wide ones. */
export type UnlockedAchievement = { id: string; polygon: number[][] | null };

/**
 * Take achievements unlocked in this city that have not been shown yet (POST /api/v1/achievements/take-unshown).
 * The core forgets them once taken, so pass the result to showUnlockedAchievements.
 */
export async function takeUnlockedAchievements(apiBaseUrl: string): Promise<UnlockedAchievement[]> {
  const data = await fetchApi<{ achievements: UnlockedAchievement[] }>(
    buildApiUrl(apiBaseUrl, "/api/v1/achievements/take-unshown"),
    { method: "POST" },
  );
  return data.achievements ?? [];
}

/**
 * Open the achievement window and copy the player profile's achievements to Steam at the same moment,
 * so the in-game window and Steam's notification appear together.
 */
export async function showUnlockedAchievements(achievements: UnlockedAchievement[]): Promise<void> {
  if (achievements.length === 0) return;
  await invoke("open_registered_window", {
    windowType: "achievement_unlocked",
    params: { extra: { achievements, openedAt: Date.now() } },
  });
  await invoke("sync_steam_achievements");
}

/** Take and show in one step, for operations that have no result window of their own. */
export async function presentUnlockedAchievements(apiBaseUrl: string): Promise<void> {
  await showUnlockedAchievements(await takeUnlockedAchievements(apiBaseUrl));
}
