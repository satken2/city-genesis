import { useTranslation } from "react-i18next";

/** Faces and meters share five levels, each 20% wide; 100% belongs to the top level. Only 0% has a dedicated expression. */
const SATISFACTION_LEVELS = [
  { key: "exhausted", emoji: "😵", color: "var(--flat-danger)" },
  { key: "unhappy", emoji: "😢", color: "color-mix(in srgb, var(--flat-danger), var(--flat-warn))" },
  { key: "neutral", emoji: "😐", color: "var(--flat-warn)" },
  { key: "happy", emoji: "😊", color: "color-mix(in srgb, var(--flat-warn), var(--flat-good))" },
  { key: "delighted", emoji: "🥰", color: "var(--flat-good)" },
] as const;

export function getSatisfactionAppearance(value: number) {
  const fraction = Number.isFinite(value) ? Math.max(0, Math.min(1, value)) : 0;
  if (fraction === 0) return { fraction, key: "zero", emoji: "☠️", color: "var(--flat-danger)" };
  return { fraction, ...SATISFACTION_LEVELS[Math.min(4, Math.floor(fraction * 5))] };
}

/** Choose an expression from satisfaction (0-1). Image replacement is contained within this component. */
export function SatisfactionFace({ value, size = 32 }: { value: number; size?: number }) {
  const { t } = useTranslation();
  const appearance = getSatisfactionAppearance(value);
  const label = t(`satisfaction.${appearance.key}`);
  return (
    <span
      className="satisfaction-face"
      role="img"
      aria-label={label}
      title={label}
      style={{ display: "inline-flex", alignItems: "center", justifyContent: "center", flexShrink: 0, fontSize: size, width: "1.2em", height: "1.2em", lineHeight: 1 }}
    >
      {appearance.emoji}
    </span>
  );
}
