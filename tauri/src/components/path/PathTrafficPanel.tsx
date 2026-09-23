/**
 * パスの交通・速度設定パネル
 *
 * パスの交通量情報と制限速度の設定を担うコンポーネント。
 *
 * 表示・編集項目:
 * - override_max_speed: 市の規制(規制ウィンドウのバリアント別制限速度)をこのパスで上書きするか。
 *   ONの間はこのパスだけ規制の変更を受けず、OFFに戻すとバックエンドが規制の値へ戻す
 * - max_speed: override_max_speed が true の場合のみ編集可能な制限速度(km/h)
 * - base_speed / min_curve_radius / max_grade: 形状から決まる通過速度と線形指標(読み取り専用)。
 *   制限速度が通過速度を大きく上回るほど事故が起きやすいため、制限速度設定の隣に並べる
 * - utilization: 現在の利用率をメーターで表示する
 * - parked_cars / street_parking_capacity: 路上駐車台数を合法に停められる台数に対する割合でメーター表示する。
 *   台数は容量を超えうる(建物の駐車場からあふれた車が停まる)ので、実数を併記する。
 *   容量を持つ道路(稼働中の地上の生活道路)と、実際に車が停まっている道路だけに出す
 * - path_traffic: 車両種別ごとの交通量(1日あたりの通過トリップ数)を表形式で表示する
 *
 * 制限速度設定の変更は「保存」ボタン押下時に PATCH リクエストとして送信する。
 */

import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { PathData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import { resolveIntlLocale } from "../../utils/locale";
import { parkingUsage } from "../../utils/parking";
import { Meter } from "../common/Meter";
import {
  PATH_MOTORWAY,
  PATH_STREET,
  PATH_WALKWAY,
  PATH_TRACK,
  PATH_TRANSPORT,
} from "../../constants";
import "./PathTrafficPanel.css";

// バックエンド geo_path_alignment.NO_CURVATURE_RADIUS(直線扱いの半径上限)。これ以上は「直線」と表示する。
const STRAIGHT_RADIUS_THRESHOLD = 1.0e6;

type PathTrafficPanelProps = {
  path: PathData;
  apiBaseUrl: string;
  onSaved: (updated: PathData) => void;
};

export function PathTrafficPanel({ path, apiBaseUrl, onSaved }: PathTrafficPanelProps) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const locale = resolveIntlLocale(i18n.language);

  const [overrideMaxSpeed, setOverrideMaxSpeed] = useState(
    path.override_max_speed
  );
  const [maxSpeed, setMaxSpeed] = useState<string>(
    path.max_speed != null ? String(path.max_speed) : ""
  );
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  /**
   * 制限速度設定を PATCH リクエストで保存する
   */
  async function handleSave() {
    setSaving(true);
    setSaveError(null);
    const base = apiBaseUrl.replace(/\/+$/, "");
    const url = `${base}/api/v1/paths/${path.path_id}`;

    const body: Record<string, unknown> = {
      override_max_speed: overrideMaxSpeed,
    };
    if (overrideMaxSpeed) {
      const parsed = parseFloat(maxSpeed);
      body.max_speed = isNaN(parsed) ? null : parsed;
    }

    try {
      const res = await fetch(url, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const updated = await res.json() as PathData;
      onSaved(updated);
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : "Unknown error");
    } finally {
      setSaving(false);
    }
  }

  /**
   * 車両種別マスターに基づいて path_traffic の行データを構築し、
   * カテゴリごとにグループ化する。
   * パスの種類に応じて表示するカテゴリをフィルタリングする。
   * マスターに存在しないキーは「ID:xxx」表示にフォールバックする。
   */
  const trafficByCategory = (() => {
    if (!path.path_traffic) return {};
    
    // パス種別に応じて表示を許可するカテゴリを判定
    let allowedCategories: Set<string> | null = null;
    if (path.path_type === PATH_MOTORWAY || path.path_type === PATH_STREET) {
      allowedCategories = new Set(["0", "1"]); // 徒歩と道路
    } else if (path.path_type === PATH_WALKWAY || path.path_type === PATH_TRANSPORT) {
      allowedCategories = new Set(["0"]); // 徒歩のみ
    } else if (path.path_type === PATH_TRACK) {
      allowedCategories = new Set(["2"]); // 鉄道のみ
    }
    
    const groups: Record<string, { key: string; displayName: string; count: number }[]> = {};
    
    for (const [key, count] of Object.entries(path.path_traffic)) {
      const id = Number(key);
      const vehicleType = masterData?.vehicle_types?.[id];
      const displayName = vehicleType?.display_name ?? `ID:${key}`;
      const category = vehicleType?.category ?? "unknown";
      
      const catKey = String(category);
      
      // 許可されていないカテゴリはスキップ
      if (allowedCategories && !allowedCategories.has(catKey)) {
        continue;
      }
      
      if (!groups[catKey]) {
        groups[catKey] = [];
      }
      groups[catKey].push({ key, displayName, count });
    }
    
    return groups;
  })();

  const hasTraffic = Object.keys(trafficByCategory).length > 0;

  // 路上駐車は道路1本あたりの台数が小さく端数が意味を持つため、小数1桁で表示する
  const parkingOnPath =
    path.street_parking_capacity != null
      ? parkingUsage(path.parked_cars ?? 0, path.street_parking_capacity, 1)
      : null;
  const streetParking =
    parkingOnPath && (parkingOnPath.capacity > 0 || parkingOnPath.used > 0) ? parkingOnPath : null;
  const formatParkingCars = (value: number) =>
    value.toLocaleString(locale, { minimumFractionDigits: 1, maximumFractionDigits: 1 });

  return (
    <section className="agent-detail-section path-traffic-section">
      <h3 className="agent-detail-section-title">{t("path.trafficSettings", "交通・速度設定")}</h3>
      {/* 制限速度設定 */}
      <div className="path-speed-block">
        <label className="path-speed-override-label">
          <input
            type="checkbox"
            className="path-speed-override-check"
            checked={overrideMaxSpeed}
            onChange={(e) => setOverrideMaxSpeed(e.target.checked)}
          />
          <span className="agent-detail-label">
            {t("path.overrideMaxSpeed")}
          </span>
        </label>

        <div className="path-speed-input-row">
          <span
            className={`agent-detail-label ${!overrideMaxSpeed ? "path-speed-label--disabled" : ""}`}
          >
            {t("path.maxSpeed")}
          </span>
          <input
            type="number"
            className="path-speed-input"
            value={maxSpeed}
            min={0}
            disabled={!overrideMaxSpeed}
            onChange={(e) => setMaxSpeed(e.target.value)}
          />
          <span
            className={`path-speed-unit ${!overrideMaxSpeed ? "path-speed-label--disabled" : ""}`}
          >
            km/h
          </span>
        </div>
      </div>

      {/* 形状から決まる通過速度と線形指標(読み取り専用) */}
      {path.base_speed != null && (
        <div className="agent-detail-grid path-alignment-grid">
          <span className="agent-detail-label">{t("path.baseSpeed")}</span>
          <span className="agent-detail-value">{Math.round(path.base_speed)} km/h</span>
          {path.min_curve_radius != null && (
            <>
              <span className="agent-detail-label">{t("path.minCurveRadius")}</span>
              <span className="agent-detail-value">
                {path.min_curve_radius >= STRAIGHT_RADIUS_THRESHOLD
                  ? t("path.straightLine")
                  : `${Math.round(path.min_curve_radius)} m`}
              </span>
            </>
          )}
          {path.max_grade != null && (
            <>
              <span className="agent-detail-label">{t("path.maxGrade")}</span>
              <span className="agent-detail-value">{path.max_grade.toFixed(1)} %</span>
            </>
          )}
        </div>
      )}

      {/* 保存ボタン */}
      <div className="path-save-row">
        {saveError && (
          <span className="path-save-error">{saveError}</span>
        )}
        <button
          className="path-save-btn"
          type="button"
          onClick={() => void handleSave()}
          disabled={saving}
        >
          {saving ? t("common.saving") : t("common.save")}
        </button>
      </div>

      {/* 利用率 */}
      {path.utilization != null && (
        <div className="path-utilization">
          <Meter
            label={t("path.utilization")}
            value={path.utilization}
          />
        </div>
      )}

      {streetParking && (
        <div className="path-street-parking">
          <Meter
            label={t("path.streetParking")}
            value={streetParking.ratio}
            polarity="negative"
          />
          <span className="path-street-parking-value">
            {t("path.streetParkingUsage", {
              parked: formatParkingCars(streetParking.used),
              capacity: formatParkingCars(streetParking.capacity),
            })}
          </span>
        </div>
      )}

      {/* 車両種別ごとの交通量 */}
      {hasTraffic && (
        <div className="path-traffic-table-block">
          <span className="agent-detail-label">{t("path.trafficByVehicle")}</span>
          
          {Object.entries(trafficByCategory).map(([catKey, rows]) => (
            <div key={catKey} className="path-traffic-category-group">
              <h4 className="path-traffic-category-title">
                {t(`path.category.${catKey}`, { defaultValue: t("path.category.unknown") })}
              </h4>
              <table className="path-traffic-table">
                <tbody>
                  {rows.map(({ key, displayName, count }) => (
                    <tr key={key}>
                      <td className="path-traffic-vehicle">{displayName}</td>
                      {/* 交通量は日次の実数(端数あり)なので小数1桁で丸めて単位を付ける */}
                      <td className="path-traffic-count">
                        {count.toLocaleString(undefined, { maximumFractionDigits: 1 })}
                        {t("path.trafficUnitPerDay", "/日")}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}
