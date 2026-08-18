/**
 * 補修設定タブ
 *
 * 公共建物・道路の補修を開始する耐久度の閾値を設定する。
 * 耐久度が閾値を下回ると確率的に補修が始まる。
 *
 * API では 0〜1 の比率でやり取りするが、UI 上では 0〜100 のパーセント表記で扱う。
 * 比率への変換はウィンドウ本体（保存時）が行い、このタブはパーセント値だけを扱う。
 */

import { useCallback } from "react";
import { useTranslation } from "react-i18next";

/** 補修閾値の対象。道路と公共建物で別々の閾値を持つ */
export type RepairTarget = "building" | "path";

const ROWS: { target: RepairTarget; labelKey: string }[] = [
  { target: "building", labelKey: "regulation.repairThresholdBuilding" },
  { target: "path", labelKey: "regulation.repairThresholdPath" },
];

export function RepairTab({
  cityThresholds,
  pendingThresholds,
  onChange,
}: {
  /** API から取得した確定値（0〜100 のパーセント） */
  cityThresholds: Record<RepairTarget, number>;
  /** 編集中値。未編集の対象は null */
  pendingThresholds: Record<RepairTarget, number | null>;
  onChange: (target: RepairTarget, value: number) => void;
}) {
  const { t } = useTranslation();

  const handleChange = useCallback(
    (target: RepairTarget, rawValue: string) => {
      const parsed = parseFloat(rawValue);
      if (isNaN(parsed) || parsed < 0 || parsed > 100) return;
      onChange(target, parsed);
    },
    [onChange],
  );

  return (
    <>
      <p className="regulation-pane__description">
        {t("regulation.repairDescription")}
      </p>

      <div className="regulation-building-list">
        {ROWS.map((row) => {
          const value = pendingThresholds[row.target] ?? cityThresholds[row.target];
          const modified = value !== cityThresholds[row.target];
          return (
            <div key={row.target} className="regulation-building-row">
              <span
                className={[
                  "regulation-building-row__name",
                  modified && "regulation-building-row__name--modified",
                ]
                  .filter(Boolean)
                  .join(" ")}
              >
                {t(row.labelKey)}
              </span>
              <div className="regulation-building-row__input-group">
                <input
                  type="number"
                  min={0}
                  max={100}
                  step={5}
                  className={[
                    "regulation-building-input",
                    modified && "regulation-building-input--modified",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  value={value}
                  onChange={(e) => handleChange(row.target, e.target.value)}
                />
                <span className="regulation-building-unit">{t("parcel.unitPercent")}</span>
              </div>
            </div>
          );
        })}
      </div>
    </>
  );
}
