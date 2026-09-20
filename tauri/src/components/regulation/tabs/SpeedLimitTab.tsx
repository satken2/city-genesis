/**
 * 制限速度タブ
 *
 * path_variant（道路のバリアント）ごとに制限速度 (km/h) を設定する。
 * - 表示値は city.max_speed にオーバーライドがあればそれ、なければマスターの default_speed_limit
 * - path_type ごとにグループ化した開閉可能なアコーディオンで並べる（既定は全て開いた状態）
 * - 入力欄と名称の強調表示は「マスターの既定速度から変えているか」を表す
 *
 * 保存すると敷設済みのパスにも即座に反映される（個別に制限速度を指定したパスは除く）。
 */

import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMasterData } from "../../../contexts/MasterDataContext";
import { PATH_MONORAIL, PATH_MOTORWAY, PATH_STREET, PATH_TRACK } from "../../../constants";
import type { PathVariantInfo, SpeedMap } from "../regulationTypes";

/**
 * 制限速度を設定できる path_type の ID（許可リスト）
 *
 * 制限速度が実際に通過時間へ効くのは、自動車が走る道路（街路・高速道路）と、
 * 公共交通の車両が実際に走る下地パス（線路・モノレール）だけ。
 * 歩道は歩行者速度パラメータ、人員搬送はバリアント固有の巡航速度で動き、
 * 公共交通の区間パス・インフラ・駅舎はそもそも通過時間を持たないため対象外。
 *
 * 除外リストではなく許可リストにしているのは、マスターへパス種別が増えたときに
 * 「効かない設定」が黙って画面に出るのを防ぐため。
 */
const EDITABLE_PATH_TYPE_IDS = new Set([PATH_STREET, PATH_MOTORWAY, PATH_TRACK, PATH_MONORAIL]);

/** マスターに default_speed_limit が無いバリアントの既定速度 (km/h) */
export const FALLBACK_SPEED_LIMIT = 40;

export function SpeedLimitTab({
  pathVariants,
  cityMaxSpeed,
  pendingSpeeds,
  onChange,
}: {
  pathVariants: PathVariantInfo[];
  /** API から取得した確定値（未設定のバリアントはキーを持たない） */
  cityMaxSpeed: SpeedMap;
  /** 編集中値 */
  pendingSpeeds: SpeedMap;
  onChange: (variantId: number, rawValue: string) => void;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  /**
   * バリアントを path_type ごとにグループ化する
   * キー: path_type ID、値: そのタイプに属するバリアント配列
   */
  const groupedVariants = useMemo(() => {
    const groups = new Map<number, PathVariantInfo[]>();
    for (const variant of pathVariants) {
      if (!EDITABLE_PATH_TYPE_IDS.has(variant.type)) continue;
      const existing = groups.get(variant.type);
      if (existing) {
        existing.push(variant);
      } else {
        groups.set(variant.type, [variant]);
      }
    }
    return groups;
  }, [pathVariants]);

  /** パス種別 ID → 表示名。アコーディオンのヘッダに使う */
  const pathTypeNames = useMemo(() => {
    if (!masterData?.path_types) return {} as Record<number, string>;
    const result: Record<number, string> = {};
    for (const [id, entry] of Object.entries(masterData.path_types)) {
      result[Number(id)] = (entry as { display_name: string }).display_name;
    }
    return result;
  }, [masterData?.path_types]);

  /**
   * 開いている path_type グループの ID セット
   * タブ全体を使える広さがあるため、初期状態では全グループを開く
   */
  const [expandedGroups, setExpandedGroups] = useState<Set<number>>(
    () => new Set(groupedVariants.keys()),
  );

  const toggleGroup = useCallback((typeId: number) => {
    setExpandedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(typeId)) {
        next.delete(typeId);
      } else {
        next.add(typeId);
      }
      return next;
    });
  }, []);

  /** 入力欄に表示する速度（編集中値 → 確定値 → マスター既定値の順で解決する） */
  const getDisplaySpeed = useCallback(
    (variant: PathVariantInfo): number => {
      const key = String(variant.id);
      if (key in pendingSpeeds) return pendingSpeeds[key];
      if (key in cityMaxSpeed) return cityMaxSpeed[key];
      return variant.default_speed_limit;
    },
    [pendingSpeeds, cityMaxSpeed],
  );

  /** マスターの既定速度から変更されているか */
  const isModified = useCallback(
    (variant: PathVariantInfo): boolean => getDisplaySpeed(variant) !== variant.default_speed_limit,
    [getDisplaySpeed],
  );

  return (
    <>
      <p className="regulation-pane__description">
        {t("regulation.speedLimitDescription")}
      </p>

      <div className="regulation-speed-list">
        {Array.from(groupedVariants.entries()).map(([typeId, variants]) => {
          const isExpanded = expandedGroups.has(typeId);
          return (
            <div key={typeId} className="regulation-speed-group">
              <button
                type="button"
                className="regulation-speed-group__header"
                onClick={() => toggleGroup(typeId)}
                aria-expanded={isExpanded}
              >
                <span className="regulation-speed-group__toggle-icon">
                  {isExpanded ? "▼" : "▶"}
                </span>
                <h4 className="regulation-speed-group__label">
                  {pathTypeNames[typeId] ?? t("regulation.unknownType")}
                </h4>
                <span className="regulation-speed-group__count">{variants.length}</span>
              </button>
              {isExpanded && (
                <div className="regulation-speed-group__content">
                  {variants.map((variant) => (
                    <div key={variant.id} className="regulation-speed-row">
                      <span
                        className={[
                          "regulation-speed-row__name",
                          isModified(variant) && "regulation-speed-row__name--modified",
                        ]
                          .filter(Boolean)
                          .join(" ")}
                        title={variant.display_name}
                      >
                        {variant.display_name}
                      </span>
                      <div className="regulation-speed-row__input-group">
                        <input
                          type="number"
                          min={0}
                          step={1}
                          className={[
                            "regulation-speed-input",
                            isModified(variant) && "regulation-speed-input--modified",
                          ]
                            .filter(Boolean)
                            .join(" ")}
                          value={getDisplaySpeed(variant)}
                          onChange={(e) => onChange(variant.id, e.target.value)}
                        />
                        <span className="regulation-speed-unit">
                          {t("regulation.speedUnit")}
                        </span>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </>
  );
}
