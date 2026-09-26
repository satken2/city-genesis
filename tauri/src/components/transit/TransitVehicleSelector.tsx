/**
 * 公共交通の車両外観セレクタ
 *
 * 路線ごとにプレイヤーが選ぶ車両の外観(API の vehicle_model)を、見本画像付きのタイルで選ばせる。
 * 外観は Unity の描画だけが使う値で、定員・費用・速度は変わらない。
 *
 * 候補の正は data/transitVehicleModels.json(路線種別=区間パスのバリアント → 外観の候補列)。
 * variant は Unity の vehicle_model_config.yaml にある車種の候補番号、prop はその外観の先頭車を写した
 * 自由配置別名のプロップ種別IDで、そのサムネイル画像を見本に使う。Unity 側の対応
 * (Resources/Config/transit_vehicle_models.json の車種と vehicle_model_config.yaml の候補)と
 * 番号を揃えること。候補外の番号は Unity が路線IDと編成番号による既定の選択へ戻して描く。
 *
 * 左ドックの新設パネル(bc-*)と右パネルの路線詳細(agent-detail-*)の両方に置くため、
 * タイルは地色を持たない半透明の中立色で描く。
 */

import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import catalog from "../../data/transitVehicleModels.json";
import { resolvePropThumbnail } from "../../utils/propThumbnail";
import "./TransitVehicleSelector.css";

/** 車両外観の候補1件。 */
export type TransitVehicleOption = {
  /** Unity の vehicle_model_config.yaml の候補番号(= API の vehicle_model) */
  variant: number;
  /** 見本画像に使う自由配置別名のプロップ種別ID(assets/images/props/{prop}.png) */
  prop: number;
  /** 表示名の i18n キー(transit.vehicleModels.<key>) */
  key: string;
};

const OPTIONS_BY_PATH_VARIANT: Record<string, TransitVehicleOption[]> = catalog;

/** 路線種別(区間パスのバリアント)の外観候補。カタログに無い種別は空。 */
export function transitVehicleOptions(pathVariant: number): TransitVehicleOption[] {
  return OPTIONS_BY_PATH_VARIANT[String(pathVariant)] ?? [];
}

/** 新設時に選んでおく外観。候補の先頭で、候補の無い種別は0。 */
export function defaultTransitVehicleModel(pathVariant: number): number {
  return transitVehicleOptions(pathVariant)[0]?.variant ?? 0;
}

/** 見本画像。画像が未生成・読み込み失敗なら fallback を出す。 */
function VehicleThumbnail({ prop, className, fallback }: { prop: number; className?: string; fallback: ReactNode }) {
  const [failed, setFailed] = useState(false);
  const url = resolvePropThumbnail(prop);
  return url && !failed ? (
    <img className={className} src={url} alt="" loading="lazy" draggable={false} onError={() => setFailed(true)} />
  ) : (
    <>{fallback}</>
  );
}

export function TransitVehicleSelector({
  pathVariant,
  value,
  onChange,
  disabled = false,
}: {
  pathVariant: number;
  value: number;
  onChange: (vehicleModel: number) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation();
  const options = transitVehicleOptions(pathVariant);
  if (options.length === 0) return null;

  return (
    <div className="tvs-grid" role="group" aria-label={t("transit.vehicleModel")}>
      {options.map((option) => {
        const name = t(`transit.vehicleModels.${option.key}`);
        return (
          <button
            key={option.variant}
            type="button"
            className="tvs-tile"
            disabled={disabled}
            aria-pressed={value === option.variant}
            title={name}
            onClick={() => onChange(option.variant)}
          >
            <span className="tvs-thumbnail">
              <VehicleThumbnail
                key={option.prop}
                prop={option.prop}
                fallback={<span className="tvs-thumbnail-missing">{t("prop_placement.noThumbnail")}</span>}
              />
            </span>
            <span className="tvs-name">{name}</span>
          </button>
        );
      })}
    </div>
  );
}

/** 選択中の外観を小さな見本画像と名前で示す読み取り表示。候補に無い値は「—」。 */
export function TransitVehicleLabel({ pathVariant, value }: { pathVariant: number; value: number }) {
  const { t } = useTranslation();
  const option = transitVehicleOptions(pathVariant).find((o) => o.variant === value);
  if (!option) return <>—</>;
  return (
    <span className="tvs-label">
      <VehicleThumbnail key={option.prop} prop={option.prop} className="tvs-label-thumbnail" fallback={null} />
      <span>{t(`transit.vehicleModels.${option.key}`)}</span>
    </span>
  );
}
