/**
 * 言語を国旗で示すアイコン(自作のSVG。3:2 の 30×20 座標)。
 *
 * 国旗は `utils/locale.ts` の Intl ロケールの地域で選ぶ(en=米国、zh-Hans=中国、zh-Hant=台湾、fr=フランス、ko=韓国)。
 * 地域を持たない es-419(中南米のスペイン語)はメキシコにする。小さく描くので紋章・星は簡略化している。
 * 言語名は title(マウスオーバー)と aria-label で出す。対応していない言語コードは何も描かない。
 */
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { isSupportedLanguage, type SupportedLanguage } from "../../utils/locale";
import "./LanguageFlag.css";

/** 中心 (cx, cy)・外接半径 r・先端の向き(度。0=右、-90=上)の五芒星 */
function star(cx: number, cy: number, r: number, pointDeg = -90, points = 5, innerRatio = 0.382): string {
  const vertices: string[] = [];
  for (let i = 0; i < points * 2; i++) {
    const radius = i % 2 === 0 ? r : r * innerRatio;
    const angle = ((pointDeg + (180 / points) * i) * Math.PI) / 180;
    vertices.push(`${(cx + radius * Math.cos(angle)).toFixed(2)},${(cy + radius * Math.sin(angle)).toFixed(2)}`);
  }
  return vertices.join(" ");
}

/** 小さい星の先端を大きい星の中心へ向ける(中国の国旗) */
function starToward(cx: number, cy: number, r: number, targetX: number, targetY: number): string {
  return star(cx, cy, r, (Math.atan2(targetY - cy, targetX - cx) * 180) / Math.PI);
}

const STRIPE = 20 / 13;

/** 韓国の卦の1本。broken は中央が切れた線 */
function trigramBar(index: number, broken: boolean): ReactNode {
  const y = 10 - 6.67 - (index + 1) * 0.83 - index * 0.42;
  return broken
    ? <g key={index}><rect x={12.5} y={y} width={2.08} height={0.83} /><rect x={15.42} y={y} width={2.08} height={0.83} /></g>
    : <rect key={index} x={12.5} y={y} width={5} height={0.83} />;
}

/** 卦(3本の線)を中心から対角線の向きへ置く。rotation は上向きの卦を回す角度 */
function trigram(rotation: number, pattern: [boolean, boolean, boolean]): ReactNode {
  return <g transform={`rotate(${rotation} 15 10)`}>{pattern.map((broken, index) => trigramBar(index, broken))}</g>;
}

const FLAGS: Record<SupportedLanguage, ReactNode> = {
  ja: <>
    <rect width="30" height="20" fill="#ffffff" />
    <circle cx="15" cy="10" r="6" fill="#bc002d" />
  </>,
  en: <>
    <rect width="30" height="20" fill="#b22234" />
    {[1, 3, 5, 7, 9, 11].map(index => <rect key={index} y={index * STRIPE} width="30" height={STRIPE} fill="#ffffff" />)}
    <rect width="12" height={STRIPE * 7} fill="#3c3b6e" />
    <g fill="#ffffff">
      {[0, 1, 2, 3].flatMap(row => [0, 1, 2, 3, 4].map(column =>
        <circle key={`${row}-${column}`} cx={1.3 + column * 2.35} cy={1.45 + row * 2.6} r="0.5" />))}
    </g>
  </>,
  "es-419": <>
    <rect width="10" height="20" fill="#006847" />
    <rect x="10" width="10" height="20" fill="#ffffff" />
    <rect x="20" width="10" height="20" fill="#ce1126" />
    <path d="M12.2 11.2a2.8 2.8 0 0 0 5.6 0" fill="none" stroke="#4c7a34" strokeWidth="0.7" />
    <ellipse cx="15" cy="9.6" rx="1.9" ry="2.3" fill="#8a5a2b" />
  </>,
  "zh-Hans": <>
    <rect width="30" height="20" fill="#de2910" />
    <g fill="#ffde00">
      <polygon points={star(5, 5, 3)} />
      <polygon points={starToward(10, 2, 1, 5, 5)} />
      <polygon points={starToward(12, 4, 1, 5, 5)} />
      <polygon points={starToward(12, 7, 1, 5, 5)} />
      <polygon points={starToward(10, 9, 1, 5, 5)} />
    </g>
  </>,
  "zh-Hant": <>
    <rect width="30" height="20" fill="#fe0000" />
    <rect width="15" height="10" fill="#000095" />
    <polygon points={star(7.5, 5, 3.6, -90, 12, 0.6)} fill="#ffffff" />
    <circle cx="7.5" cy="5" r="2.05" fill="#000095" />
    <circle cx="7.5" cy="5" r="1.75" fill="#ffffff" />
  </>,
  fr: <>
    <rect width="10" height="20" fill="#0055a4" />
    <rect x="10" width="10" height="20" fill="#ffffff" />
    <rect x="20" width="10" height="20" fill="#ef4135" />
  </>,
  ko: <>
    <rect width="30" height="20" fill="#ffffff" />
    <g transform="rotate(33.69 15 10)">
      <path d="M10 10a5 5 0 0 1 10 0z" fill="#cd2e3a" />
      <path d="M10 10a5 5 0 0 0 10 0z" fill="#0047a0" />
      <circle cx="12.5" cy="10" r="2.5" fill="#cd2e3a" />
      <circle cx="17.5" cy="10" r="2.5" fill="#0047a0" />
    </g>
    <g fill="#000000">
      {trigram(-56.31, [false, false, false])}
      {trigram(56.31, [true, false, true])}
      {trigram(-123.69, [false, true, false])}
      {trigram(123.69, [true, true, true])}
    </g>
  </>,
};

export function LanguageFlag({ language, className }: { language: string | undefined; className?: string }) {
  const { t } = useTranslation();
  if (!isSupportedLanguage(language)) return null;
  const name = t(`settings.language.${language}`);
  return (
    <svg className={`language-flag${className ? ` ${className}` : ""}`} viewBox="0 0 30 20" role="img" aria-label={name}>
      <title>{name}</title>
      {FLAGS[language]}
    </svg>
  );
}
