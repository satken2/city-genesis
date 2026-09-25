/**
 * 制限速度の道路標識(日本式の最高速度標識: 赤い輪・白地・青い数字)
 *
 * 配色と輪の比率は3Dビューの標識(Unity の UI/SpeedLimitSign.cs の JapaneseStyle)と揃え、
 * 画面をまたいで同じ標識に見えるようにしている。画像アセットは使わず SVG で描く。
 */

import "./SpeedLimitSign.css";

type SpeedLimitSignProps = {
  /** 表示する速度(km/h)。未確定のときは null で、数字の代わりに「–」を出す */
  speed: number | null;
  /** 読み上げとツールチップに使う説明文 */
  label: string;
  /** 一辺の大きさ(px) */
  size?: number;
};

/** 標識の数字。整数はそのまま、端数は小数1桁まで出す */
function formatSignSpeed(speed: number): string {
  return String(Math.round(speed * 10) / 10);
}

export function SpeedLimitSign({ speed, label, size = 68 }: SpeedLimitSignProps) {
  const text = speed == null || !Number.isFinite(speed) ? "–" : formatSignSpeed(speed);
  // 桁が増えても白地からはみ出さないよう、字の大きさを落としたうえで字幅を詰めて収める。
  const fontSize = text.length <= 2 ? 42 : text.length === 3 ? 36 : 30;
  const textLength = text.length <= 1 ? undefined : text.length === 2 ? 44 : text.length === 3 ? 54 : 58;

  return (
    <svg className="speed-limit-sign" width={size} height={size} viewBox="0 0 100 100" role="img" aria-label={label}>
      <title>{label}</title>
      <circle cx="50" cy="50" r="50" fill="#e6e9ec" />
      <circle cx="50" cy="50" r="48" fill="#cd2430" />
      <circle cx="50" cy="50" r="36" fill="#ffffff" />
      <text
        className="speed-limit-sign__number"
        x="50"
        y="52"
        fontSize={fontSize}
        textLength={textLength}
        lengthAdjust={textLength != null ? "spacingAndGlyphs" : undefined}
      >
        {text}
      </text>
    </svg>
  );
}
