import {
  GROUND_COVER_DESERT,
  GROUND_COVER_LUSH,
  GROUND_COVER_SEMI_ARID,
  GROUND_COVER_SNOW,
} from "../../constants";
import { groundCoverRamp, type Rgb } from "../terrain/terrainData";

/** Relative luminance (WCAG) of an sRGB color. */
function luminance(rgb: Rgb): number {
  const [r, g, b] = rgb.map(value => {
    const c = value / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/**
 * Original SVG representing ground cover texture. Keep the type name in the tooltip and accessibility label.
 * The rounded tile uses the cover's midslope terrain color (the same palette as the terrain editor and 2D map),
 * and the glyph is black or white, whichever contrasts more with it.
 */
export function GroundCoverIcon({ cover, label }: { cover: number; label: string }) {
  let shape;
  switch (cover) {
    case GROUND_COVER_DESERT:
      shape = <><circle cx="36" cy="12" r="4" /><path d="M4 34Q15 15 28 28T44 29M4 40Q26 22 44 37" /></>;
      break;
    case GROUND_COVER_LUSH:
      shape = <><path d="M8 40V28m0 5Q0 25 5 18Q14 20 8 33M20 40V16m0 10Q8 17 13 8Q25 12 20 26M20 31Q31 28 29 17Q18 18 20 31M35 40V22m0 9Q28 20 33 12Q44 16 35 31M35 35Q45 30 44 23" /><path d="M4 41h40" /></>;
      break;
    case GROUND_COVER_SNOW:
      shape = <><path d="M24 5v32M10 13l28 16M10 29l28-16M19 8l5 5 5-5M19 34l5-5 5 5M10 19l7-2-2-7M33 32l-2-7 7-2M10 23l7 2-2 7M33 10l-2 7 7 2M5 42q9-5 19-1t19-1" /></>;
      break;
    case GROUND_COVER_SEMI_ARID:
      shape = <><path d="M4 34h40M8 34l6 6-3 5M14 40h12l5-6M26 40l6 5M25 33V21m0 7-6-6m6 2 6-8M8 23l3 3m25-1 4-3" /></>;
      break;
    default:
      shape = <><path d="M4 41h40M14 40Q15 27 7 23M14 35q3-13 8-17M25 40q-1-18-7-29M25 32q4-11 9-13M35 40q0-12 6-16" /></>;
  }
  const background = groundCoverRamp(cover)[1];
  const light = luminance(background);
  const glyph = (light + 0.05) / 0.05 >= 1.05 / (light + 0.05) ? "#000" : "#fff";
  return (
    <svg className="new-game-ground-icon" viewBox="-8 -8 64 64" role="img" aria-label={label}>
      <title>{label}</title>
      <rect x="-8" y="-8" width="64" height="64" rx="13" fill={`rgb(${background.join(",")})`} />
      <g fill="none" stroke={glyph} strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round">{shape}</g>
    </svg>
  );
}
