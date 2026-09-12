/**
 * クエスト達成通知ウィンドウ
 *
 * エポック実行完了後に、新規達成された notify="modal" のクエストがある場合に
 * 表示されるお祝いポップアップ。tada.wav の再生と紙吹雪アニメーションで演出する。
 *
 * 複数のクエストを同時に達成した場合は tier（gold > silver > bronze）の降順に
 * 1件ずつページ送りで表示する（「次へ」ボタン、最後のページで「閉じる」）。
 * 各ページには達成時の実測値（achieved_values）と達成報酬（reward）を表示する。
 *
 * 親コンポーネントからは達成したクエストの配列を受け取り、
 * 全ページを見終えて閉じると onClose コールバックが呼ばれる。
 */

import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { QuestNotification, QuestTier } from "../../types";
// 音声は必ず import 経由で参照する。パス文字列を直接 Audio に渡すとバンドル対象から
// 外れ、本番ビルドではファイルが存在せず無音になる。
import tadaSound from "../../assets/sounds/tada.wav";
import "./QuestCompleteWindow.css";

type QuestCompleteWindowProps = {
  quests: QuestNotification[];
  onClose: () => void;
};

/** tier の表示順（豪華なものから見せる） */
const TIER_ORDER: Record<QuestTier, number> = { gold: 0, silver: 1, bronze: 2 };

/** 紙吹雪の枚数。CSSアニメーションで舞い落ちる装飾要素の数 */
const CONFETTI_COUNT = 24;

/** 紙吹雪の色パレット（tierごとに変える） */
const CONFETTI_COLORS: Record<QuestTier, string[]> = {
  gold: ["#f0c040", "#f5d060", "#ffe9a0", "#e8a020"],
  silver: ["#c0c8d8", "#e0e8f0", "#98a8c0", "#f0f4f8"],
  bronze: ["#c08850", "#d8a068", "#a87040", "#e8c090"],
};

function formatValue(v: number | null): string {
  if (v === null || v === undefined) return "—";
  // 小数は2桁まで、整数はそのまま桁区切りで表示する
  return Number.isInteger(v)
    ? v.toLocaleString()
    : v.toLocaleString(undefined, { maximumFractionDigits: 2 });
}

export function QuestCompleteWindow({ quests, onClose }: QuestCompleteWindowProps) {
  const { t } = useTranslation();
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const [pageIndex, setPageIndex] = useState(0);

  // notify="modal" のクエストのみを tier の降順（gold→silver→bronze）で表示する
  const modalQuests = useMemo(
    () =>
      quests
        .filter((q) => q.notify === "modal")
        .sort((a, b) => (TIER_ORDER[a.tier] ?? 2) - (TIER_ORDER[b.tier] ?? 2)),
    [quests],
  );

  useEffect(() => {
    audioRef.current = new Audio(tadaSound);
    audioRef.current.play().catch(() => {});
    return () => {
      if (audioRef.current) {
        audioRef.current.pause();
        audioRef.current = null;
      }
    };
  }, []);

  if (modalQuests.length === 0) {
    return null;
  }

  const quest = modalQuests[Math.min(pageIndex, modalQuests.length - 1)];
  const isLastPage = pageIndex >= modalQuests.length - 1;
  const achievedEntries = Object.values(quest.achieved_values ?? {});
  const colors = CONFETTI_COLORS[quest.tier] ?? CONFETTI_COLORS.bronze;

  function handleNext() {
    if (isLastPage) {
      onClose();
    } else {
      setPageIndex((i) => i + 1);
      // ページ送りのたびにファンファーレを鳴らし直す
      audioRef.current?.play().catch(() => {});
    }
  }

  return (
    <div className="quest-complete-overlay" onClick={handleNext}>
      {/* 紙吹雪。key に pageIndex を含めてページ送りごとにアニメーションを再生する */}
      <div className="quest-complete-confetti" aria-hidden={true}>
        {Array.from({ length: CONFETTI_COUNT }, (_, i) => (
          <span
            key={`${pageIndex}-${i}`}
            className="quest-complete-confetti-piece"
            style={{
              left: `${(i * 100) / CONFETTI_COUNT + 2}%`,
              backgroundColor: colors[i % colors.length],
              animationDelay: `${(i % 8) * 0.15}s`,
              animationDuration: `${2.2 + (i % 5) * 0.35}s`,
            }}
          />
        ))}
      </div>

      <div
        className={`quest-complete-card quest-complete-card--${quest.tier}`}
        onClick={(e) => e.stopPropagation()}
      >
        {/* tier バッジ（gold / silver / bronze） */}
        <span className={`quest-complete-tier quest-complete-tier--${quest.tier}`}>
          {t(`questComplete.tier.${quest.tier}`)}
        </span>

        <h2 className="quest-complete-title">{t("questComplete.title")}</h2>
        <p className="quest-complete-subtitle">{t("questComplete.congratulations")}</p>

        <div className="quest-complete-item">
          <span className="quest-complete-item-name">{quest.name}</span>
          <span className="quest-complete-item-desc">{quest.description}</span>

          {/* 達成時の実測値。「実測値 / 目標値」で達成の瞬間を可視化する */}
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

          {/* 達成報酬（交付金） */}
          {quest.reward?.type === "grant_money" && (
            <div className="quest-complete-reward">
              <span className="quest-complete-reward-label">{t("questComplete.reward")}</span>
              <span className="quest-complete-reward-value">
                {t("common.currency", { value: quest.reward.amount })}
              </span>
            </div>
          )}
        </div>

        {/* 複数達成時のページ位置表示 */}
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
