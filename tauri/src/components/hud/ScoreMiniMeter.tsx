/**
 * 施政スコアミニメーター（コックピット常駐）
 *
 * 市政の評価を常時コックピットに表示する小型メーター。
 * クリックで達成度ウィンドウ(スコアの内訳・推移を載せる画面)を開く。
 *
 * 表示するのは都市スコアの合計値ではなく「1世帯あたり平均」(city_score_per_household)である。
 * 合計値は世帯数に比例する絶対量のため人口を増やすだけで伸びてしまい、施政の良し悪しを表さない。
 * 1世帯あたりなら人口と独立に「住民ひとりひとりが豊かに暮らせているか」を表せる。
 *
 * - メーター全体の横幅 = 1世帯あたりの満点(2.0)、色塗り幅 = 現在の1世帯あたりスコア
 * - 0.7以上: 緑 / 0.4以上: 黄 / 0.4未満: 赤（Meter の既定色分けをそのまま使う）
 * - 10秒に1回バックエンドをREST取得して最新化する（BudgetMiniMeter と同じ間隔）
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { Meter } from "../common/Meter";
import "./hud.css";

/** バックエンドのポーリング間隔（ミリ秒） */
const POLL_INTERVAL_MS = 10_000;

/**
 * 1世帯が都市スコアに貢献しうる最大値。
 * qol_score(最大1.0) + 正規化 env_score(最大1.0) の合計であり、スコア式から定まる固定値。
 */
const MAX_SCORE_PER_HOUSEHOLD = 2.0;

type ScoreResponse = {
  latest: {
    city_score_per_household: number;
  };
};

export function ScoreMiniMeter({
  apiBaseUrl,
  onOpen,
}: {
  apiBaseUrl: string;
  onOpen: () => void;
}) {
  const { t } = useTranslation();
  const [perHousehold, setPerHousehold] = useState<number | null>(null);

  useEffect(() => {
    if (!apiBaseUrl) return;

    const controller = new AbortController();
    let cancelled = false;

    async function refresh() {
      try {
        const res = await fetchApi<ScoreResponse>(
          buildApiUrl(apiBaseUrl, "/api/v1/score"),
          { signal: controller.signal },
        );
        if (cancelled) return;
        setPerHousehold(res.latest.city_score_per_household);
      } catch {
        // 取得失敗（中断含む）は前回値を保持し、画面をちらつかせない（BudgetMiniMeterと同方針）。
      }
    }

    void refresh();
    const timer = setInterval(() => void refresh(), POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      controller.abort();
      clearInterval(timer);
    };
  }, [apiBaseUrl]);

  const hasData = perHousehold !== null;

  return (
    <button
      type="button"
      className="hud-mini-meter"
      onClick={onOpen}
      title={t("taskbar.scoreMeterTooltip")}
    >
      <span className="hud-mini-meter__label">{t("taskbar.scoreMeterLabel")}</span>
      {hasData && (
        <span className="hud-mini-meter__value">{perHousehold!.toFixed(2)}</span>
      )}
      <div className="hud-mini-meter__bar">
        {hasData ? (
          <Meter value={perHousehold! / MAX_SCORE_PER_HOUSEHOLD} />
        ) : (
          <span className="hud-mini-meter__placeholder">—</span>
        )}
      </div>
    </button>
  );
}
