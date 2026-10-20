/**
 * Icons showing path condition levels (durability, noise, air pollution).
 *
 * All are custom 32 × 32 shapes using currentColor (the caller chooses colors for each level).
 * Higher level numbers indicate worse conditions.
 * - Durability: more cracks in a road slab, with a broken corner at the worst level.
 * - Noise: more sound waves from a speaker.
 * - Air pollution: a denser cloud with more falling particles.
 */

import type { ReactNode } from "react";

type LeveledIconProps = {
  /** 0 is best. null means the value cannot be evaluated; draw only the basic shape. */
  level: number | null;
  /** Side length (px). */
  size?: number;
};

function IconFrame({ size = 40, children }: { size?: number; children: ReactNode }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 32 32"
      stroke="currentColor"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      {children}
    </svg>
  );
}

/** Worst durability level (slab with a broken corner). Lower levels use crack counts. */
export const DURABILITY_ICON_MAX_LEVEL = 4;

const DURABILITY_CRACKS = [
  "M13 4L14.5 9L12 12.5L14 16",
  "M28 13L23.5 14.5L21.5 12.5L18 16",
  "M8 28L10 23.5L8.5 21L12 19M14 16L17 19.5L16 23L17.5 26",
  "M4 16.5L8 16L10 18L12 19M17 19.5L21 21",
];

export function DurabilityIcon({ level, size }: LeveledIconProps) {
  const lv = level ?? 0;
  const broken = lv >= DURABILITY_ICON_MAX_LEVEL;
  return (
    <IconFrame size={size}>
      {broken ? (
        <>
          <path
            d="M8 4H24Q28 4 28 8V19L24.5 21.5L22 28H8Q4 28 4 24V8Q4 4 8 4Z"
            fill="currentColor"
            fillOpacity="0.22"
            strokeWidth="2"
          />
          <path d="M26 23.5L29 22.5L29.5 26.5L26.5 28Z" fill="currentColor" fillOpacity="0.5" strokeWidth="1.5" />
        </>
      ) : (
        <rect x="4" y="4" width="24" height="24" rx="4" fill="currentColor" fillOpacity="0.18" strokeWidth="2" />
      )}
      {DURABILITY_CRACKS.slice(0, lv).map((d) => (
        <path key={d} d={d} fill="none" strokeWidth="1.8" />
      ))}
    </IconFrame>
  );
}

const NOISE_WAVES = [
  "M20 12.5Q22.5 16 20 19.5",
  "M23 9.5Q27.5 16 23 22.5",
  "M26 6.5Q32 16 26 25.5",
];

export function NoiseIcon({ level, size }: LeveledIconProps) {
  return (
    <IconFrame size={size}>
      <path d="M4 12H9.5L16 6.5V25.5L9.5 20H4Z" fill="currentColor" fillOpacity="0.25" strokeWidth="2" />
      {NOISE_WAVES.slice(0, level ?? 0).map((d) => (
        <path key={d} d={d} fill="none" strokeWidth="2.2" />
      ))}
    </IconFrame>
  );
}

const POLLUTION_FILL_OPACITY = [0.06, 0.3, 0.55, 0.85];
const POLLUTION_PARTICLES: [number, number][][] = [
  [[12, 23], [20, 23]],
  [[9, 26.5], [16, 26.5], [23, 26.5]],
  [[12.5, 30], [19.5, 30]],
];

export function PollutionIcon({ level, size }: LeveledIconProps) {
  const lv = level ?? 0;
  return (
    <IconFrame size={size}>
      <path
        d="M9 19H23A5 5 0 0 0 23.6 9.05A7 7 0 0 0 10.2 7.6A5.5 5.5 0 0 0 9 19Z"
        fill="currentColor"
        fillOpacity={POLLUTION_FILL_OPACITY[Math.min(lv, POLLUTION_FILL_OPACITY.length - 1)]}
        strokeWidth="2"
      />
      {POLLUTION_PARTICLES.slice(0, lv).flat().map(([cx, cy]) => (
        <circle key={`${cx}-${cy}`} cx={cx} cy={cy} r="1.5" fill="currentColor" stroke="none" />
      ))}
    </IconFrame>
  );
}
