/**
 * 選択中パスバリアントの説明カード(アイコン+名称+説明文+線形・構造の規格値)。
 *
 * 表示位置がパネルごとに違う(道路建設=バリアント選択の直下、軌道建設=規格プルダウンと
 * 線路構成の設定の間)ため、共通ボディ(PathBuildSections)には含めず独立したセクションにしている。
 * 未選択のときは何も描画しない(=区切り線も出ない)。
 *
 * 説明文の下には、敷設時の検証で効くバリアントの制約値を小さなアイコン+数値だけで並べる
 * (最小曲線半径・最大勾配・最大スパン長・最小高さ)。項目名はアイコンのツールチップで示す。
 * 制約として意味を持たない値は出さない: 0=制限なしの曲線半径・勾配、地形に張り付く
 * バリアントのスパン長(非追従スパンにしか掛からない)、標高を編集できないバリアントの最小高さ。
 */

import { useTranslation } from "react-i18next";
import type { PathMasterEntry } from "../../types";
import { resolvePathIcon } from "../../utils/pathIcon";

type PathVariantDescriptionProps = {
  entry: PathMasterEntry | null;
};

/** 規格値1項目。i18nキーは path_construction.spec 配下、value は単位付きの表示文字列。 */
type SpecItem = {
  key: "minCurveRadius" | "maxGrade" | "maxSpan" | "minHeight";
  value: string;
  icon: React.ReactNode;
};

/** 小数1桁までで末尾の0を落とす(100.0→"100"、2.5→"2.5")。 */
function formatSpecNumber(value: number): string {
  return String(Number(value.toFixed(1)));
}

/** 24px正方の線画アイコン。色は文字色に追従させる。 */
const SPEC_ICON_PROPS = {
  className: "pc-spec-icon",
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 2,
  strokeLinecap: "round" as const,
  strokeLinejoin: "round" as const,
  "aria-hidden": true,
};

const ICON_CURVE_RADIUS = (
  <svg {...SPEC_ICON_PROPS}>
    <path d="M4 20 A16 16 0 0 1 20 4" />
    <path d="M20 20 L8.7 8.7" />
  </svg>
);

const ICON_GRADE = (
  <svg {...SPEC_ICON_PROPS}>
    <path d="M3 19 H21 V8 Z" />
  </svg>
);

const ICON_SPAN = (
  <svg {...SPEC_ICON_PROPS}>
    <path d="M2 8 H22" />
    <path d="M6 8 V20 M18 8 V20" />
    <path d="M8 15 H16 M10 13 l-2 2 2 2 M14 13 l2 2 -2 2" />
  </svg>
);

const ICON_MIN_HEIGHT = (
  <svg {...SPEC_ICON_PROPS}>
    <path d="M3 20 H21" />
    <path d="M12 4 V16 M8 12 l4 4 4 -4" />
  </svg>
);

/** バリアントの制約値のうち、意味を持つものだけを表示順に並べる。 */
function collectSpecs(entry: PathMasterEntry): SpecItem[] {
  const specs: SpecItem[] = [];
  if (entry.min_curve_radius && entry.min_curve_radius > 0) {
    specs.push({ key: "minCurveRadius", value: `${formatSpecNumber(entry.min_curve_radius)}m`, icon: ICON_CURVE_RADIUS });
  }
  if (entry.max_grade && entry.max_grade > 0) {
    specs.push({ key: "maxGrade", value: `${formatSpecNumber(entry.max_grade)}%`, icon: ICON_GRADE });
  }
  // max_span は柱・桁で支える非追従スパンだけに掛かるので、地形に張り付くしかないバリアントでは出さない
  const canLeaveGround = entry.height_editable || entry.follow_terrain === false;
  if (entry.max_span && entry.max_span > 0 && canLeaveGround) {
    specs.push({ key: "maxSpan", value: `${formatSpecNumber(entry.max_span)}m`, icon: ICON_SPAN });
  }
  if (entry.height_editable) {
    specs.push({ key: "minHeight", value: `${formatSpecNumber(entry.min_height ?? 0)}m`, icon: ICON_MIN_HEIGHT });
  }
  return specs;
}

export function PathVariantDescription({ entry }: PathVariantDescriptionProps) {
  const { t } = useTranslation();
  if (!entry) return null;

  const specs = collectSpecs(entry);

  return (
    <div className="pc-section">
      <div className="pc-description-frame">
        <img
          src={resolvePathIcon(entry.type)}
          alt={entry.display_name}
          className="pc-description-icon"
          draggable={false}
        />
        <div className="pc-description-body">
          <div className="pc-description-name">{entry.display_name}</div>
          <div className="pc-description-text">{entry.description || "—"}</div>
          {specs.length > 0 && (
            <div className="pc-description-specs">
              {specs.map((spec) => (
                <span key={spec.key} className="pc-spec" title={t(`path_construction.spec.${spec.key}`)}>
                  {spec.icon}
                  <span className="pc-spec-value">{spec.value}</span>
                </span>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
