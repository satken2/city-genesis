/**
 * Speed limit road sign (Japanese style: red ring, white background, blue numbers).
 *
 * Match colors/ring proportions to the 3D sign (JapaneseStyle in Unity UI/SpeedLimitSign.cs)
 * so it appears consistent across screens. Draw with SVG without image assets.
 */

import "./SpeedLimitSign.css";

type SpeedLimitSignProps = {
  /** Speed to display (km/h). Null when unconfirmed; display "–" instead of digits. */
  speed: number | null;
  /** Description for accessibility and tooltips. */
  label: string;
  /** Side length (px). */
  size?: number;
};

/** Sign number. Show integers unchanged and fractions to at most one decimal place. */
function formatSignSpeed(speed: number): string {
  return String(Math.round(speed * 10) / 10);
}

export function SpeedLimitSign({ speed, label, size = 68 }: SpeedLimitSignProps) {
  const text = speed == null || !Number.isFinite(speed) ? "–" : formatSignSpeed(speed);
  // For more digits, shrink font size and condense width so text stays inside the white area.
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
