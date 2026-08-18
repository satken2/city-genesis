/**
 * 規制ウィンドウコンポーネント
 *
 * 都市全体の規制・ポリシー設定を管理するウィンドウ。
 * 「制限速度」「建築規制」「補修設定」「移民規制」の4タブ構成で、
 * このコンポーネントはデータの取得・編集中値の保持・保存とタブ切替だけを担い、
 * 各タブの描画は `tabs/` 配下のコンポーネントに委ねる。
 *
 * 保存ボタンはタブ共通で1つだけ持ち、押すと全タブの変更をまとめて送信する。
 * 表示していないタブの変更を見落とさないよう、未保存の変更があるタブには印を付ける。
 *
 * 保存先は2系統:
 * - PATCH /api/v1/city … 制限速度 (max_speed)・補修閾値 (repair_threshold*)
 * - PATCH /api/v1/city/regulations … 建築規制・移民規制
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { PathMasterEntry } from "../../types";
import type {
  BuildingRegKey,
  CityData,
  ImmigrationRegulations,
  PathVariantInfo,
  RegulationsData,
  SpeedMap,
} from "./regulationTypes";
import { FALLBACK_SPEED_LIMIT, SpeedLimitTab } from "./tabs/SpeedLimitTab";
import { BUILDING_REG_KEYS, BuildingRegulationTab } from "./tabs/BuildingRegulationTab";
import { RepairTab, type RepairTarget } from "./tabs/RepairTab";
import { IMMIGRATION_SCALAR_KEYS, ImmigrationTab } from "./tabs/ImmigrationTab";
import "./CityRegulationWindow.css";

/** タブの並び。ラベルは各セクションの見出しをそのまま使う */
const TABS = [
  { id: "speed", labelKey: "regulation.speedLimitTitle" },
  { id: "building", labelKey: "regulation.buildingRegulationTitle" },
  { id: "repair", labelKey: "regulation.repairTitle" },
  { id: "immigration", labelKey: "regulation.immigrationTitle" },
] as const;

type TabId = (typeof TABS)[number]["id"];

export function CityRegulationWindow({
  apiBaseUrl,
  onClose,
}: {
  apiBaseUrl: string;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [activeTab, setActiveTab] = useState<TabId>("speed");

  /** 制限速度。確定値と編集中値（キーは path_variant の ID 文字列、値は km/h） */
  const [cityMaxSpeed, setCityMaxSpeed] = useState<SpeedMap>({});
  const [pendingSpeeds, setPendingSpeeds] = useState<SpeedMap>({});

  /**
   * 補修閾値の確定値・編集中値（編集していない対象は null）
   * building=公共建物、path=道路。
   * API では 0〜1 の比率でやり取りするが、UI 上では 0〜100 のパーセント表記で保持する。
   */
  const [cityRepair, setCityRepair] = useState<Record<RepairTarget, number>>({
    building: 30,
    path: 30,
  });
  const [pendingRepair, setPendingRepair] = useState<Record<RepairTarget, number | null>>({
    building: null,
    path: null,
  });

  /** 建築規制の確定値・編集中値（編集中値は API 単位。キーが無い項目は確定値を使う） */
  const [cityRegulations, setCityRegulations] = useState<RegulationsData | null>(null);
  const [pendingRegulations, setPendingRegulations] = useState<Partial<RegulationsData>>({});

  /**
   * 移民規制の確定値・編集中値
   * bool / dict 型のフィールドを含むため建築規制とは別に管理する。
   */
  const [cityImmigration, setCityImmigration] = useState<ImmigrationRegulations | null>(null);
  const [pendingImmigration, setPendingImmigration] = useState<Partial<ImmigrationRegulations>>({});

  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  /**
   * マスターデータから取得した全パスバリアントの一覧（ID順）
   * 保存ペイロードは除外対象も含めた全バリアント分を送るため、ここでは絞り込まない。
   */
  const pathVariants = useMemo<PathVariantInfo[]>(() => {
    if (!masterData?.paths) return [];
    return Object.entries(masterData.paths)
      .map(([id, entry]) => ({
        id: Number(id),
        display_name: (entry as PathMasterEntry).display_name,
        type: (entry as PathMasterEntry).type,
        default_speed_limit: (entry as PathMasterEntry).default_speed_limit ?? FALLBACK_SPEED_LIMIT,
      }))
      .sort((a, b) => a.id - b.id);
  }, [masterData?.paths]);

  /* ==== 初期データ読み込み ==== */

  useEffect(() => {
    const controller = new AbortController();

    async function loadCityData() {
      try {
        const [cityRes, regulationsRes] = await Promise.all([
          fetchApi<CityData>(buildApiUrl(apiBaseUrl, "/api/v1/city"), {
            signal: controller.signal,
          }),
          fetchApi<RegulationsData>(buildApiUrl(apiBaseUrl, "/api/v1/city/regulations"), {
            signal: controller.signal,
          }),
        ]);

        const maxSpeed: SpeedMap = {};
        for (const [k, v] of Object.entries(cityRes.city.max_speed)) {
          maxSpeed[k] = Number(v);
        }
        setCityMaxSpeed(maxSpeed);
        setPendingSpeeds(maxSpeed);

        // 補修閾値は API の 0〜1 比率を 0〜100 パーセントへ変換して保持する
        setCityRepair((prev) => ({
          building: cityRes.city.repair_threshold != null ? cityRes.city.repair_threshold * 100 : prev.building,
          path: cityRes.city.repair_threshold_path != null ? cityRes.city.repair_threshold_path * 100 : prev.path,
        }));
        setPendingRepair({ building: null, path: null });

        setCityRegulations(regulationsRes);
        setPendingRegulations({});

        // 移民規制フィールドを別 state に切り出して保持する。
        // レスポンスは1つの RegulationsData だが、bool/dict 型を含むため編集中値は分けて管理する。
        setCityImmigration({
          immigration_age_min: regulationsRes.immigration_age_min,
          immigration_age_max: regulationsRes.immigration_age_max,
          immigration_allow_single: regulationsRes.immigration_allow_single,
          immigration_allow_married_no_children: regulationsRes.immigration_allow_married_no_children,
          immigration_allow_with_children: regulationsRes.immigration_allow_with_children,
          immigration_gender_policy: regulationsRes.immigration_gender_policy,
          immigration_skill_thresholds: { ...regulationsRes.immigration_skill_thresholds },
          immigration_quota: regulationsRes.immigration_quota,
        });
        setPendingImmigration({});
      } catch (err) {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
        }
      }
    }

    loadCityData();
    return () => controller.abort();
  }, [apiBaseUrl]);

  /* ==== 変更ハンドラ ==== */

  const handleSpeedChange = useCallback((variantId: number, rawValue: string) => {
    const parsed = parseFloat(rawValue);
    if (isNaN(parsed) || parsed < 0) return;
    setPendingSpeeds((prev) => ({ ...prev, [String(variantId)]: parsed }));
  }, []);

  const handleRegulationChange = useCallback((key: BuildingRegKey, apiValue: number) => {
    setPendingRegulations((prev) => ({ ...prev, [key]: apiValue }));
  }, []);

  const handleRepairChange = useCallback((target: RepairTarget, value: number) => {
    setPendingRepair((prev) => ({ ...prev, [target]: value }));
  }, []);

  const handleImmNumberChange = useCallback(
    (
      key: "immigration_age_min" | "immigration_age_max" | "immigration_gender_policy" | "immigration_quota",
      value: number,
    ) => {
      setPendingImmigration((prev) => ({ ...prev, [key]: value }));
    },
    [],
  );

  const handleImmBoolChange = useCallback(
    (
      key: "immigration_allow_single" | "immigration_allow_married_no_children" | "immigration_allow_with_children",
      value: boolean,
    ) => {
      setPendingImmigration((prev) => ({ ...prev, [key]: value }));
    },
    [],
  );

  /**
   * スキル要件スライダーの変更。
   * skill_thresholds は一部だけ更新するため、既存辞書をコピーして差し替える。
   */
  const handleSkillThresholdChange = useCallback(
    (skillId: number, value: number) => {
      setPendingImmigration((prev) => {
        const current =
          prev.immigration_skill_thresholds ?? cityImmigration?.immigration_skill_thresholds ?? {};
        return {
          ...prev,
          immigration_skill_thresholds: { ...current, [String(skillId)]: value },
        };
      });
    },
    [cityImmigration],
  );

  /* ==== 未保存の変更の判定 ==== */

  /**
   * タブごとの「未保存の変更があるか」。
   * 保存ボタンが1つしかないため、表示していないタブの編集をタブの印で気付けるようにする。
   */
  const dirtyTabs = useMemo<Record<TabId, boolean>>(() => {
    const speed = Object.entries(pendingSpeeds).some(([key, value]) => {
      if (key in cityMaxSpeed) return value !== cityMaxSpeed[key];
      // 確定値に無いバリアントはマスターの既定速度が現在値
      const variant = pathVariants.find((v) => String(v.id) === key);
      return value !== (variant?.default_speed_limit ?? FALLBACK_SPEED_LIMIT);
    });

    const building =
      cityRegulations != null &&
      BUILDING_REG_KEYS.some((key) => (pendingRegulations[key] ?? cityRegulations[key]) !== cityRegulations[key]);

    const repair = (["building", "path"] as RepairTarget[]).some(
      (target) => pendingRepair[target] != null && pendingRepair[target] !== cityRepair[target],
    );

    let immigration = false;
    if (cityImmigration) {
      immigration = IMMIGRATION_SCALAR_KEYS.some(
        (key) => (pendingImmigration[key] ?? cityImmigration[key]) !== cityImmigration[key],
      );
      // スキル要件は辞書なので、両者のキーを合わせて中身を比較する
      const pendingSkills = pendingImmigration.immigration_skill_thresholds;
      if (!immigration && pendingSkills) {
        const citySkills = cityImmigration.immigration_skill_thresholds;
        const allKeys = new Set([...Object.keys(pendingSkills), ...Object.keys(citySkills)]);
        immigration = Array.from(allKeys).some((k) => (pendingSkills[k] ?? 0) !== (citySkills[k] ?? 0));
      }
    }

    return { speed, building, repair, immigration };
  }, [
    pendingSpeeds,
    cityMaxSpeed,
    pathVariants,
    pendingRegulations,
    cityRegulations,
    pendingRepair,
    cityRepair,
    pendingImmigration,
    cityImmigration,
  ]);

  const hasUnsavedChanges = TABS.some((tab) => dirtyTabs[tab.id]);

  /* ==== 保存 ==== */

  const handleSave = useCallback(async () => {
    setIsSaving(true);
    setError(null);

    try {
      // 制限速度は差分ではなく全バリアント分を送る
      const speedPayload: Record<string, number> = {};
      for (const variant of pathVariants) {
        const key = String(variant.id);
        speedPayload[key] = pendingSpeeds[key] ?? variant.default_speed_limit;
      }

      // PATCH /api/v1/city に送るペイロード（制限速度 + 補修閾値）
      // 補修閾値は UI の 0〜100 パーセントを API の 0〜1 比率へ戻す
      const cityPatchPayload: Record<string, unknown> = { max_speed: speedPayload };
      if (pendingRepair.building != null) {
        cityPatchPayload.repair_threshold = pendingRepair.building / 100;
      }
      if (pendingRepair.path != null) {
        cityPatchPayload.repair_threshold_path = pendingRepair.path / 100;
      }

      // 建築規制 + 移民規制のペイロード。
      // 移民規制は bool/dict 型のフィールドを含むため、Partial<RegulationsData> ではなく
      // Record<string, unknown> で保持して JSON 送信する。
      const regulationPayload: Record<string, unknown> = {};
      if (cityRegulations) {
        for (const key of BUILDING_REG_KEYS) {
          regulationPayload[key] = pendingRegulations[key] ?? cityRegulations[key];
        }
      }
      if (cityImmigration) {
        for (const key of IMMIGRATION_SCALAR_KEYS) {
          regulationPayload[key] = pendingImmigration[key] ?? cityImmigration[key];
        }
        regulationPayload.immigration_skill_thresholds =
          pendingImmigration.immigration_skill_thresholds ?? cityImmigration.immigration_skill_thresholds;
      }

      const [speedRes, regulationRes] = await Promise.all([
        fetch(buildApiUrl(apiBaseUrl, "/api/v1/city"), {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(cityPatchPayload),
        }),
        fetch(buildApiUrl(apiBaseUrl, "/api/v1/city/regulations"), {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(regulationPayload),
        }),
      ]);

      if (!speedRes.ok) {
        throw new Error(`Speed update failed: HTTP ${speedRes.status}`);
      }
      if (!regulationRes.ok) {
        throw new Error(`Regulation update failed: HTTP ${regulationRes.status}`);
      }

      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save");
    } finally {
      setIsSaving(false);
    }
  }, [
    apiBaseUrl,
    pathVariants,
    pendingSpeeds,
    pendingRepair,
    cityRegulations,
    pendingRegulations,
    cityImmigration,
    pendingImmigration,
    onClose,
  ]);

  /* ==== レンダリング ==== */

  if (error && Object.keys(cityMaxSpeed).length === 0 && !cityRegulations) {
    return <div className="regulation-window__error">{error}</div>;
  }

  if (pathVariants.length === 0 || !cityRegulations || !cityImmigration) {
    return <div className="regulation-window__loading">{t("common.loading")}</div>;
  }

  return (
    <div className="regulation-window">
      <nav className="regulation-window__tabs">
        {TABS.map((tab) => (
          <button
            key={tab.id}
            type="button"
            className={[
              "regulation-tab",
              tab.id === activeTab && "regulation-tab--active",
            ]
              .filter(Boolean)
              .join(" ")}
            onClick={() => setActiveTab(tab.id)}
          >
            <span className="regulation-tab__label">{t(tab.labelKey)}</span>
            {dirtyTabs[tab.id] && (
              <span className="regulation-tab__dot" title={t("regulation.unsavedChanges")} />
            )}
          </button>
        ))}
      </nav>

      <div className="regulation-window__content">
        {activeTab === "speed" && (
          <SpeedLimitTab
            pathVariants={pathVariants}
            cityMaxSpeed={cityMaxSpeed}
            pendingSpeeds={pendingSpeeds}
            onChange={handleSpeedChange}
          />
        )}
        {activeTab === "building" && (
          <BuildingRegulationTab
            cityRegulations={cityRegulations}
            pendingRegulations={pendingRegulations}
            onChange={handleRegulationChange}
          />
        )}
        {activeTab === "repair" && (
          <RepairTab
            cityThresholds={cityRepair}
            pendingThresholds={pendingRepair}
            onChange={handleRepairChange}
          />
        )}
        {activeTab === "immigration" && (
          <ImmigrationTab
            cityImmigration={cityImmigration}
            pendingImmigration={pendingImmigration}
            onNumberChange={handleImmNumberChange}
            onBoolChange={handleImmBoolChange}
            onSkillThresholdChange={handleSkillThresholdChange}
          />
        )}
      </div>

      {error && <div className="regulation-window__error-inline">{error}</div>}

      <div className="regulation-window__footer">
        {hasUnsavedChanges && (
          <span className="regulation-window__unsaved">{t("regulation.unsavedChanges")}</span>
        )}
        <button
          className="regulation-window__save-btn"
          onClick={handleSave}
          disabled={isSaving}
          type="button"
        >
          {isSaving ? t("common.saving") : t("common.save")}
        </button>
      </div>
    </div>
  );
}
