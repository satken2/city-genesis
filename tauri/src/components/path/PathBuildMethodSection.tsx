/**
 * パス建設パネルの敷設方法(単一/碁盤状)の切り替えと、碁盤状の分割数。
 *
 * 選べる方法が1つしかない種別ではセクションごと描かない(pathBuildMethods.buildMethodsFor)。
 * 碁盤状は、直角に折れる3点で決めた矩形を辺ごとの分割数で等分した道路で埋める
 * (1辺目=1点目→2点目、2辺目=2点目→3点目)。
 */

import { useTranslation } from "react-i18next";
import { PATH_BUILD_METHOD_GRID, PATH_GRID_BUILD_MAX_DIVISIONS } from "../../constants";
import { NumberStepper } from "../common/NumberStepper";
import { BUILD_METHOD_KEYS, buildMethodsFor } from "./pathBuildMethods";

type PathBuildMethodSectionProps = {
  pathType: number | null;
  buildMethod: number;
  gridDivisions: [number, number];
  onChangeBuildMethod: (method: number) => void;
  onChangeGridDivision: (index: 0 | 1, value: number) => void;
};

export function PathBuildMethodSection({
  pathType,
  buildMethod,
  gridDivisions,
  onChangeBuildMethod,
  onChangeGridDivision,
}: PathBuildMethodSectionProps) {
  const { t } = useTranslation();
  const methods = buildMethodsFor(pathType);
  if (methods.length < 2) return null;

  return (
    <div className="pc-section">
      <div className="pc-section-title">{t("path_construction.buildMethod.title")}</div>
      <div className="pc-platform-side-group">
        {methods.map((method) => (
          <button
            key={method}
            type="button"
            data-guide="path.buildMethod"
            data-guide-value={method}
            className={`pc-platform-side-btn ${buildMethod === method ? "active" : ""}`}
            onClick={() => onChangeBuildMethod(method)}
          >
            {t(`path_construction.buildMethod.${BUILD_METHOD_KEYS[method]}`)}
          </button>
        ))}
      </div>
      {buildMethod === PATH_BUILD_METHOD_GRID && ([0, 1] as const).map((index) => (
        <div key={index} className="pc-settings-row">
          <span className="pc-settings-label">
            {t(index === 0 ? "path_construction.grid.divisionsFirst" : "path_construction.grid.divisionsSecond")}
          </span>
          <div className="pc-stepper-row">
            <NumberStepper
              value={gridDivisions[index]}
              onChange={(value) => onChangeGridDivision(index, value)}
              min={1}
              max={PATH_GRID_BUILD_MAX_DIVISIONS}
              step={1}
              integerOnly
            />
          </div>
        </div>
      ))}
    </div>
  );
}
