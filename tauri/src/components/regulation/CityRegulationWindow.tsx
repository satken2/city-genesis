/**
 * 規制ウィンドウコンポーネント
 *
 * 都市全体の規制・ポリシー設定を管理するウィンドウ。
 * 「制限速度」と「建築規制」の2つのセクションをタイル状に並べて表示する。
 *
 * 制限速度セクション:
 * - path_variant（道路のバリアント）ごとに制限速度 (km/h) を設定する
 * - バリアント名はマスターデータ (useMasterData().paths) から取得
 * - 初期表示値は city.max_speed に設定があればそれ、なければ master の default_speed_limit
 * - path_type ごとにグループ化し、開閉可能なアコーディオン形式で表示する
 * - 保存は PATCH /api/v1/city で max_speed 辞書を一括更新する
 *
 * 建築規制セクション:
 * - 都市全体の建築規制（高さ規制・容積率・建ぺい率）を設定する
 * - 初期値は GET /api/v1/city/regulations から取得
 * - 保存は PATCH /api/v1/city/regulations で一括更新する
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { PathMasterEntry, SkillEntry } from "../../types";
import "./CityRegulationWindow.css";

/**
 * GET /api/v1/city レスポンスの city オブジェクト型
 * max_speed 以外のフィールドは本コンポーネントでは使用しない
 */
type CityData = {
  city: {
    max_speed: Record<string, number>;
    repair_threshold?: number;
    repair_threshold_path?: number;
  };
};

/**
 * GET /api/v1/city/regulations レスポンスの型
 * 建築規制（容積率・高さ制限・建ぺい率）と移民規制を含む。
 */
type RegulationsData = {
  limit_volume_ratio: number;
  limit_height: number;
  limit_coverage_ratio: number;
  // 移民規制（プレイヤーが政策として設定する受け入れ条件）
  immigration_age_min: number;
  immigration_age_max: number;
  immigration_allow_single: boolean;
  immigration_allow_married_no_children: boolean;
  immigration_allow_with_children: boolean;
  immigration_gender_policy: number; // 0=制限なし, 1=男性のみ(世帯主), 2=女性のみ(世帯主)
  immigration_skill_thresholds: Record<string, number>; // skill_id(文字列) → 最低値
  immigration_quota: number; // 1エポックあたり上限人数。0=無制限
};

/**
 * 移民規制の編集中値を保持する型。
 * 建築規制と異なり bool / dict 型のフィールドを含むため、pendingRegulations とは別に管理する。
 */
type ImmigrationRegulations = {
  immigration_age_min: number;
  immigration_age_max: number;
  immigration_allow_single: boolean;
  immigration_allow_married_no_children: boolean;
  immigration_allow_with_children: boolean;
  immigration_gender_policy: number;
  immigration_skill_thresholds: Record<string, number>;
  immigration_quota: number;
};

/**
 * 建築規制（数値型）のキー。
 * 移民規制は bool/dict 型を含むため、建築規制の数値ハンドラはこのキーのみを扱う。
 */
type BuildingRegKey = "limit_volume_ratio" | "limit_height" | "limit_coverage_ratio";

/**
 * 編集中の制限速度を保持する型
 * キーは path_variant の ID（数値文字列）、値は km/h
 */
type SpeedMap = Record<string, number>;

export function CityRegulationWindow({
  apiBaseUrl,
  onClose,
}: {
  apiBaseUrl: string;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [cityMaxSpeed, setCityMaxSpeed] = useState<SpeedMap>({});
  const [pendingSpeeds, setPendingSpeeds] = useState<SpeedMap>({});

  /**
   * 補修閾値の確定値（APIから取得）
   * repair_threshold: 公共建物の補修を開始する耐久度の閾値
   * repair_threshold_path: 道路の補修を開始する耐久度の閾値
   *
   * APIでは0-1の比率でやり取りするが、UI上では0-100のパーセント表記で扱う。
   * ロード時に100倍、保存時に1/100に変換する。
   */
  const [cityRepairThreshold, setCityRepairThreshold] = useState<number>(30);
  const [cityRepairThresholdPath, setCityRepairThresholdPath] = useState<number>(30);
  /** 補修閾値の編集中値 */
  const [pendingRepairThreshold, setPendingRepairThreshold] = useState<number | null>(null);
  const [pendingRepairThresholdPath, setPendingRepairThresholdPath] = useState<number | null>(null);

  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  /**
   * 建築規制の現在値（APIから取得した確定値）
   */
  const [cityRegulations, setCityRegulations] = useState<RegulationsData | null>(null);
  /**
   * 建築規制の編集中値
   * キーが存在しない場合は cityRegulations の値を表示する
   */
  const [pendingRegulations, setPendingRegulations] = useState<Partial<RegulationsData>>({});

  /**
   * 移民規制の現在値（APIから取得した確定値）
   * bool / dict 型のフィールドを含むため建築規制とは別に管理する。
   */
  const [cityImmigration, setCityImmigration] = useState<ImmigrationRegulations | null>(null);
  /**
   * 移民規制の編集中値
   * キーが存在しない場合は cityImmigration の値を表示する。
   * skill_thresholds は部分更新するため、オブジェクトのコピーを作って差し替える。
   */
  const [pendingImmigration, setPendingImmigration] = useState<Partial<ImmigrationRegulations>>({});

  /**
   * 制限速度セクションで開いている path_type グループの ID セット
   * デフォルトでは全グループを閉じておく（量が増えた際の視認性を考慮）
   */
  const [expandedGroups, setExpandedGroups] = useState<Set<number>>(new Set());

  /**
   * マスターデータから取得した全パスバリアントの一覧
   * ID順でソートし、display_name と default_speed_limit を持つ
   */
  const pathVariants = useMemo(() => {
    if (!masterData?.paths) return [];
    return Object.entries(masterData.paths)
      .map(([id, entry]) => ({
        id: Number(id),
        display_name: (entry as PathMasterEntry).display_name,
        type: (entry as PathMasterEntry).type,
        default_speed_limit: (entry as PathMasterEntry).default_speed_limit ?? 40,
      }))
      .sort((a, b) => a.id - b.id);
  }, [masterData?.paths]);

  /**
   * 除外するパス種別のIDセット
   * 制限速度の設定対象から除外する
   * - 0: 通常 (PATH_DEFAULT) - 基本的なパス
   * - 3: 歩道 (PATH_WALKWAY) - 歩行者专用
   * - 5: 公共交通 (PATH_TRANSPORT) - 公共交通パス
   */
  const excludedPathTypeIds = useMemo(() => new Set([0, 3, 5]), []);

  /**
   * マスターデータから取得した全スキル一覧
   * 移民規制のスキル要件スライダーを描画するために使用する。
   * ID順でソートし、display_name を持つ。
   */
  const skillEntries = useMemo(() => {
    if (!masterData?.skills) return [];
    return Object.entries(masterData.skills)
      .map(([id, entry]) => ({
        id: Number(id),
        display_name: (entry as SkillEntry).display_name,
      }))
      .sort((a, b) => a.id - b.id);
  }, [masterData?.skills]);

  /**
   * パス種別マスター (path_types) を取得し、ID→表示名のマップを作成する
   * バリアントをグループ化して表示する際のヘッダ用
   * 除外対象のpath_typeはマップに含めない
   */
  const pathTypeNames = useMemo(() => {
    if (!masterData?.path_types) return {} as Record<number, string>;
    const result: Record<number, string> = {};
    for (const [id, entry] of Object.entries(masterData.path_types)) {
      const typeId = Number(id);
      if (excludedPathTypeIds.has(typeId)) continue;
      result[typeId] = (entry as { display_name: string }).display_name;
    }
    return result;
  }, [masterData?.path_types, excludedPathTypeIds]);

  /**
   * バリアントを path_type ごとにグループ化する
   * キー: path_type ID、値: そのタイプに属するバリアント配列
   * 除外対象の path_type はグループに含めない
   */
  const groupedVariants = useMemo(() => {
    const groups = new Map<number, typeof pathVariants>();
    for (const variant of pathVariants) {
      if (excludedPathTypeIds.has(variant.type)) continue;
      const existing = groups.get(variant.type);
      if (existing) {
        existing.push(variant);
      } else {
        groups.set(variant.type, [variant]);
      }
    }
    return groups;
  }, [pathVariants, excludedPathTypeIds]);

  /**
   * バリアントの現在の表示速度を取得する
   * city.max_speed にオーバーライドがあればその値、
   * なければマスターデータの default_speed_limit を返す
   */
  const getDisplaySpeed = useCallback(
    (variantId: number): number => {
      const key = String(variantId);
      if (key in pendingSpeeds) {
        return pendingSpeeds[key];
      }
      if (key in cityMaxSpeed) {
        return cityMaxSpeed[key];
      }
      const variant = pathVariants.find((v) => v.id === variantId);
      return variant?.default_speed_limit ?? 40;
    },
    [pendingSpeeds, cityMaxSpeed, pathVariants],
  );

  /**
   * バリアントの速度が初期値（master の default_speed_limit）から変更されているかを判定する
   * 値が変更されている場合に入力欄のスタイルを変えるなどで利用する
   */
  const isModified = useCallback(
    (variantId: number): boolean => {
      const variant = pathVariants.find((v) => v.id === variantId);
      const defaultValue = variant?.default_speed_limit ?? 40;
      return getDisplaySpeed(variantId) !== defaultValue;
    },
    [getDisplaySpeed, pathVariants],
  );

  /* ==== 初期データ読み込み ==== */

  useEffect(() => {
    const controller = new AbortController();

    async function loadCityData() {
      try {
        const [cityRes, regulationsRes] = await Promise.all([
          fetchApi<CityData>(
            buildApiUrl(apiBaseUrl, "/api/v1/city"),
            { signal: controller.signal },
          ),
          fetchApi<RegulationsData>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/regulations"),
            { signal: controller.signal },
          ),
        ]);

        const maxSpeed: SpeedMap = {};
        for (const [k, v] of Object.entries(cityRes.city.max_speed)) {
          maxSpeed[k] = Number(v);
        }
        setCityMaxSpeed(maxSpeed);
        setPendingSpeeds(maxSpeed);

        // 補修閾値を読み込む（APIの0-1比率を0-100パーセントに変換）
        if (cityRes.city.repair_threshold != null) {
          setCityRepairThreshold(cityRes.city.repair_threshold * 100);
        }
        if (cityRes.city.repair_threshold_path != null) {
          setCityRepairThresholdPath(cityRes.city.repair_threshold_path * 100);
        }
        setPendingRepairThreshold(null);
        setPendingRepairThresholdPath(null);

        setCityRegulations(regulationsRes);
        setPendingRegulations({});

        // 移民規制フィールドを別stateに切り出して保持する。
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

  /* ==== 速度変更ハンドラ ==== */

  const handleSpeedChange = useCallback(
    (variantId: number, rawValue: string) => {
      const parsed = parseFloat(rawValue);
      if (isNaN(parsed) || parsed < 0) return;
      setPendingSpeeds((prev) => ({ ...prev, [String(variantId)]: parsed }));
    },
    [],
  );

  /* ==== アコーディオン開閉ハンドラ ==== */

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

  /* ==== 建築規制変更ハンドラ ==== */

  const handleRegulationChange = useCallback(
    (key: BuildingRegKey, rawValue: string) => {
      const parsed = parseFloat(rawValue);
      if (isNaN(parsed) || parsed < 0) return;
      setPendingRegulations((prev) => ({ ...prev, [key]: parsed }));
    },
    [],
  );

  /**
   * 建築規制の表示値を取得する
   */
  const getRegulationDisplay = useCallback(
    (key: BuildingRegKey): number => {
      if (key in pendingRegulations) {
        return pendingRegulations[key]!;
      }
      if (cityRegulations) {
        return cityRegulations[key];
      }
      return 0;
    },
    [pendingRegulations, cityRegulations],
  );

  /**
   * 建築規制が初期値から変更されているかを判定する
   */
  const isRegulationModified = useCallback(
    (key: BuildingRegKey): boolean => {
      if (!cityRegulations) return false;
      return getRegulationDisplay(key) !== cityRegulations[key];
    },
    [getRegulationDisplay, cityRegulations],
  );

  /* ==== 移民規制ハンドラ ==== */

  /* ==== 補修閾値ハンドラ ==== */

  /**
   * 補修閾値の表示値を取得する（編集中値を優先）
   */
  const getRepairThresholdDisplay = useCallback(
    (isPath: boolean): number => {
      if (isPath) {
        return pendingRepairThresholdPath ?? cityRepairThresholdPath;
      }
      return pendingRepairThreshold ?? cityRepairThreshold;
    },
    [pendingRepairThreshold, cityRepairThreshold, pendingRepairThresholdPath, cityRepairThresholdPath],
  );

  /**
   * 補修閾値が初期値から変更されているか
   */
  const isRepairThresholdModified = useCallback(
    (isPath: boolean): boolean => {
      if (isPath) {
        return (pendingRepairThresholdPath ?? cityRepairThresholdPath) !== cityRepairThresholdPath;
      }
      return (pendingRepairThreshold ?? cityRepairThreshold) !== cityRepairThreshold;
    },
    [pendingRepairThreshold, cityRepairThreshold, pendingRepairThresholdPath, cityRepairThresholdPath],
  );

  const handleRepairThresholdChange = useCallback(
    (isPath: boolean, rawValue: string) => {
      const parsed = parseFloat(rawValue);
      if (isNaN(parsed) || parsed < 0 || parsed > 100) return;
      if (isPath) {
        setPendingRepairThresholdPath(parsed);
      } else {
        setPendingRepairThreshold(parsed);
      }
    },
    [],
  );

  /**
   * 移民規制の現在の表示値（スカラー項目）を取得する。
   * 編集中値があればそれを、なければ API から取得した確定値を返す。
   */
  const getImmScalar = useCallback(
    <K extends keyof ImmigrationRegulations>(key: K): ImmigrationRegulations[K] | undefined => {
      if (key in pendingImmigration) {
        return pendingImmigration[key];
      }
      return cityImmigration ? cityImmigration[key] : undefined;
    },
    [pendingImmigration, cityImmigration],
  );

  /**
   * 指定スキルIDの要件閾値（表示値）を取得する。
   * スライダーの現在位置として使う。未設定の場合は 0。
   */
  const getSkillThreshold = useCallback(
    (skillId: number): number => {
      const key = String(skillId);
      const pending = pendingImmigration.immigration_skill_thresholds;
      if (pending && key in pending) {
        return pending[key];
      }
      return cityImmigration?.immigration_skill_thresholds[key] ?? 0;
    },
    [pendingImmigration, cityImmigration],
  );

  /**
   * 移民規制の number 項目（年齢・性別ポリシー・クオータ）の変更ハンドラ
   */
  const handleImmNumberChange = useCallback(
    (
      key: "immigration_age_min" | "immigration_age_max" | "immigration_gender_policy" | "immigration_quota",
      rawValue: string,
    ) => {
      const parsed = parseInt(rawValue, 10);
      if (isNaN(parsed) || parsed < 0) return;
      setPendingImmigration((prev) => ({ ...prev, [key]: parsed }));
    },
    [],
  );

  /**
   * 移民規制の bool 項目（世帯構成の許可フラグ）の変更ハンドラ
   */
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
   * スキル要件スライダーの変更ハンドラ。
   * skill_thresholds は一部だけ更新するため、既存辞書をコピーして差し替える。
   */
  const handleSkillThresholdChange = useCallback(
    (skillId: number, rawValue: number) => {
      const key = String(skillId);
      setPendingImmigration((prev) => {
        const current = prev.immigration_skill_thresholds ?? cityImmigration?.immigration_skill_thresholds ?? {};
        return {
          ...prev,
          immigration_skill_thresholds: { ...current, [key]: rawValue },
        };
      });
    },
    [cityImmigration],
  );

  /**
   * 移民規制の指定項目が初期値から変更されているかを判定する。
   * skill_thresholds は辞書の内容を比較する。
   */
  const isImmModified = useCallback(
    (key: keyof ImmigrationRegulations): boolean => {
      if (!cityImmigration) return false;
      const pending = pendingImmigration[key];
      if (pending === undefined) return false;
      if (key !== "immigration_skill_thresholds") {
        return pending !== cityImmigration[key];
      }
      const pendingDict = pending as Record<string, number>;
      const cityDict = cityImmigration[key] as Record<string, number>;
      const allKeys = new Set([...Object.keys(pendingDict), ...Object.keys(cityDict)]);
      for (const k of allKeys) {
        if ((pendingDict[k] ?? 0) !== (cityDict[k] ?? 0)) return true;
      }
      return false;
    },
    [pendingImmigration, cityImmigration],
  );

  /* ==== 保存 ==== */

  const handleSave = useCallback(async () => {
    setIsSaving(true);
    setError(null);

    try {
      // 制限速度のペイロードを構築
      const speedPayload: Record<string, number> = {};
      for (const variant of pathVariants) {
        const key = String(variant.id);
        speedPayload[key] = pendingSpeeds[key] ?? variant.default_speed_limit ?? 40;
      }

      // PATCH /api/v1/city に送るペイロード（制限速度 + 補修閾値）
      const cityPatchPayload: Record<string, unknown> = {
        max_speed: speedPayload,
      };
      if (isRepairThresholdModified(false)) {
        // UIの0-100パーセントをAPIの0-1比率に変換
        cityPatchPayload.repair_threshold = (pendingRepairThreshold ?? cityRepairThreshold) / 100;
      }
      if (isRepairThresholdModified(true)) {
        cityPatchPayload.repair_threshold_path = (pendingRepairThresholdPath ?? cityRepairThresholdPath) / 100;
      }

      // 建築規制 + 移民規制のペイロードを構築
      // 移民規制は bool/dict 型のフィールドを含むため、Partial<RegulationsData> ではなく
      // Record<string, unknown> で保持して JSON 送信する。
      const regulationPayload: Record<string, unknown> = {};
      if (cityRegulations) {
        for (const key of (["limit_volume_ratio", "limit_height", "limit_coverage_ratio"] as BuildingRegKey[])) {
          regulationPayload[key] = pendingRegulations[key] ?? cityRegulations[key];
        }
      }
      // 移民規制フィールドをペイロードに追加する。
      // 編集中値があればそれを、なければ API 取得値を使う。
      if (cityImmigration) {
        const immScalarKeys = [
          "immigration_age_min",
          "immigration_age_max",
          "immigration_allow_single",
          "immigration_allow_married_no_children",
          "immigration_allow_with_children",
          "immigration_gender_policy",
          "immigration_quota",
        ] as const;
        for (const key of immScalarKeys) {
          regulationPayload[key] = pendingImmigration[key] ?? cityImmigration[key];
        }
        regulationPayload.immigration_skill_thresholds =
          pendingImmigration.immigration_skill_thresholds ?? cityImmigration.immigration_skill_thresholds;
      }

      // 両方のAPIを並列で呼び出す（補修閾値は PATCH /api/v1/city に含める）
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
  }, [apiBaseUrl, pendingSpeeds, pathVariants, cityRegulations, pendingRegulations, cityImmigration, pendingImmigration, onClose, pendingRepairThreshold, cityRepairThreshold, pendingRepairThresholdPath, cityRepairThresholdPath, isRepairThresholdModified]);

  /* ==== レンダリング ==== */

  if (error && Object.keys(cityMaxSpeed).length === 0 && !cityRegulations) {
    return (
      <div className="regulation-window__error">{error}</div>
    );
  }

  if (pathVariants.length === 0 || !cityRegulations || !cityImmigration) {
    return (
      <div className="regulation-window__loading">{t("common.loading")}</div>
    );
  }

  return (
    <div className="regulation-window">
      <div className="regulation-window__columns">
        {/* 左の列: 制限速度 */}
        <div className="regulation-column">
          {/* 制限速度セクション */}
          <section className="regulation-tile">
          <h3 className="regulation-tile__title">
            {t("regulation.speedLimitTitle")}
          </h3>
          <p className="regulation-tile__description">
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
                    <span className="regulation-speed-group__count">
                      {variants.length}
                    </span>
                  </button>
                  {isExpanded && (
                    <div className="regulation-speed-group__content">
                      {variants.map((variant) => (
                        <div key={variant.id} className="regulation-speed-row">
                          <span
                            className={[
                              "regulation-speed-row__name",
                              isModified(variant.id) && "regulation-speed-row__name--modified",
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
                                isModified(variant.id) && "regulation-speed-input--modified",
                              ]
                                .filter(Boolean)
                                .join(" ")}
                              value={getDisplaySpeed(variant.id)}
                              onChange={(e) => handleSpeedChange(variant.id, e.target.value)}
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
        </section>
        </div>

        {/* 真ん中の列: 建築規制 ＆ 補修設定 */}
        <div className="regulation-column">
          {/* 建築規制セクション */}
          <section className="regulation-tile">
          <h3 className="regulation-tile__title">
            {t("regulation.buildingRegulationTitle")}
          </h3>
          <p className="regulation-tile__description">
            {t("regulation.buildingRegulationDescription")}
          </p>

          <div className="regulation-building-list">
            <div className="regulation-building-row">
              <span
                className={[
                  "regulation-building-row__name",
                  isRegulationModified("limit_height") && "regulation-building-row__name--modified",
                ]
                  .filter(Boolean)
                  .join(" ")}
              >
                {t("regulation.limitHeight")}
              </span>
              <div className="regulation-building-row__input-group">
                <input
                  type="number"
                  min={0}
                  step={0.5}
                  className={[
                    "regulation-building-input",
                    isRegulationModified("limit_height") && "regulation-building-input--modified",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  value={getRegulationDisplay("limit_height")}
                  onChange={(e) => handleRegulationChange("limit_height", e.target.value)}
                />
                <span className="regulation-building-unit">
                  {t("parcel.unitMeter")}
                </span>
              </div>
            </div>

            <div className="regulation-building-row">
              <span
                className={[
                  "regulation-building-row__name",
                  isRegulationModified("limit_volume_ratio") && "regulation-building-row__name--modified",
                ]
                  .filter(Boolean)
                  .join(" ")}
              >
                {t("regulation.limitVolumeRatio")}
              </span>
              <div className="regulation-building-row__input-group">
                <input
                  type="number"
                  min={0}
                  step={0.1}
                  className={[
                    "regulation-building-input",
                    isRegulationModified("limit_volume_ratio") && "regulation-building-input--modified",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  value={getRegulationDisplay("limit_volume_ratio")}
                  onChange={(e) => handleRegulationChange("limit_volume_ratio", e.target.value)}
                />
                <span className="regulation-building-unit">
                  {t("parcel.unitPercent")}
                </span>
              </div>
            </div>

            <div className="regulation-building-row">
              <span
                className={[
                  "regulation-building-row__name",
                  isRegulationModified("limit_coverage_ratio") && "regulation-building-row__name--modified",
                ]
                  .filter(Boolean)
                  .join(" ")}
              >
                {t("regulation.limitCoverageRatio")}
              </span>
              <div className="regulation-building-row__input-group">
                <input
                  type="number"
                  min={0}
                  max={1}
                  step={0.01}
                  className={[
                    "regulation-building-input",
                    isRegulationModified("limit_coverage_ratio") && "regulation-building-input--modified",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  value={getRegulationDisplay("limit_coverage_ratio")}
                  onChange={(e) => handleRegulationChange("limit_coverage_ratio", e.target.value)}
                />
                <span className="regulation-building-unit">
                  {t("parcel.unitPercent")}
                </span>
              </div>
            </div>
          </div>
        </section>

        {/* 補修設定セクション */}
        <section className="regulation-tile">
          <h3 className="regulation-tile__title">
            {t("regulation.repairTitle")}
          </h3>
          <p className="regulation-tile__description">
            {t("regulation.repairDescription")}
          </p>

          <div className="regulation-building-list">
            <div className="regulation-building-row">
              <span
                className={[
                  "regulation-building-row__name",
                  isRepairThresholdModified(false) && "regulation-building-row__name--modified",
                ]
                  .filter(Boolean)
                  .join(" ")}
              >
                {t("regulation.repairThresholdBuilding")}
              </span>
              <div className="regulation-building-row__input-group">
                <input
                  type="number"
                  min={0}
                  max={100}
                  step={5}
                  className={[
                    "regulation-building-input",
                    isRepairThresholdModified(false) && "regulation-building-input--modified",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  value={getRepairThresholdDisplay(false)}
                  onChange={(e) => handleRepairThresholdChange(false, e.target.value)}
                />
                <span className="regulation-building-unit">
                  {t("parcel.unitPercent")}
                </span>
              </div>
            </div>

            <div className="regulation-building-row">
              <span
                className={[
                  "regulation-building-row__name",
                  isRepairThresholdModified(true) && "regulation-building-row__name--modified",
                ]
                  .filter(Boolean)
                  .join(" ")}
              >
                {t("regulation.repairThresholdPath")}
              </span>
              <div className="regulation-building-row__input-group">
                <input
                  type="number"
                  min={0}
                  max={100}
                  step={5}
                  className={[
                    "regulation-building-input",
                    isRepairThresholdModified(true) && "regulation-building-input--modified",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  value={getRepairThresholdDisplay(true)}
                  onChange={(e) => handleRepairThresholdChange(true, e.target.value)}
                />
                <span className="regulation-building-unit">
                  {t("parcel.unitPercent")}
                </span>
              </div>
            </div>
          </div>
        </section>
        </div>

        {/* 右の列: 移民規制 */}
        <div className="regulation-column">
          {/* 移民規制セクション */}
          <section className="regulation-tile">
          <h3 className="regulation-tile__title">
            {t("regulation.immigrationTitle")}
          </h3>
          <p className="regulation-tile__description">
            {t("regulation.immigrationDescription")}
          </p>

          <div className="regulation-immigration-list">
            {/* 世帯主の年齢範囲 */}
            <div className="regulation-immigration-row">
              <span
                className={[
                  "regulation-immigration-row__name",
                  (isImmModified("immigration_age_min") || isImmModified("immigration_age_max")) &&
                    "regulation-immigration-row__name--modified",
                ]
                  .filter(Boolean)
                  .join(" ")}
              >
                {t("regulation.immigrationAgeRange")}
              </span>
              <div className="regulation-immigration-row__input-group">
                <input
                  type="number"
                  min={0}
                  max={120}
                  step={1}
                  className={[
                    "regulation-immigration-input",
                    isImmModified("immigration_age_min") && "regulation-immigration-input--modified",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  value={getImmScalar("immigration_age_min") ?? 20}
                  onChange={(e) => handleImmNumberChange("immigration_age_min", e.target.value)}
                />
                <span className="regulation-immigration-separator">〜</span>
                <input
                  type="number"
                  min={0}
                  max={120}
                  step={1}
                  className={[
                    "regulation-immigration-input",
                    isImmModified("immigration_age_max") && "regulation-immigration-input--modified",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  value={getImmScalar("immigration_age_max") ?? 50}
                  onChange={(e) => handleImmNumberChange("immigration_age_max", e.target.value)}
                />
              </div>
            </div>

            {/* 世帯構成 */}
            <div className="regulation-immigration-row regulation-immigration-row--check">
              <span className="regulation-immigration-row__name">
                {t("regulation.immigrationHouseholdComposition")}
              </span>
              <div className="regulation-immigration-checkboxes">
                <label className="regulation-immigration-checkbox">
                  <input
                    type="checkbox"
                    checked={getImmScalar("immigration_allow_single") ?? true}
                    onChange={(e) => handleImmBoolChange("immigration_allow_single", e.target.checked)}
                  />
                  <span className={isImmModified("immigration_allow_single") ? "regulation-immigration-checkbox--modified" : ""}>
                    {t("regulation.immigrationAllowSingle")}
                  </span>
                </label>
                <label className="regulation-immigration-checkbox">
                  <input
                    type="checkbox"
                    checked={getImmScalar("immigration_allow_married_no_children") ?? true}
                    onChange={(e) => handleImmBoolChange("immigration_allow_married_no_children", e.target.checked)}
                  />
                  <span className={isImmModified("immigration_allow_married_no_children") ? "regulation-immigration-checkbox--modified" : ""}>
                    {t("regulation.immigrationAllowMarriedNoChildren")}
                  </span>
                </label>
                <label className="regulation-immigration-checkbox">
                  <input
                    type="checkbox"
                    checked={getImmScalar("immigration_allow_with_children") ?? true}
                    onChange={(e) => handleImmBoolChange("immigration_allow_with_children", e.target.checked)}
                  />
                  <span className={isImmModified("immigration_allow_with_children") ? "regulation-immigration-checkbox--modified" : ""}>
                    {t("regulation.immigrationAllowWithChildren")}
                  </span>
                </label>
              </div>
            </div>

            {/* 性別（世帯主） */}
            <div className="regulation-immigration-row">
              <span
                className={[
                  "regulation-immigration-row__name",
                  isImmModified("immigration_gender_policy") && "regulation-immigration-row__name--modified",
                ]
                  .filter(Boolean)
                  .join(" ")}
              >
                {t("regulation.immigrationGenderPolicy")}
              </span>
              <select
                className="regulation-immigration-select"
                value={getImmScalar("immigration_gender_policy") ?? 0}
                onChange={(e) => handleImmNumberChange("immigration_gender_policy", e.target.value)}
              >
                <option value={0}>{t("regulation.immigrationGenderAny")}</option>
                <option value={1}>{t("regulation.immigrationGenderMale")}</option>
                <option value={2}>{t("regulation.immigrationGenderFemale")}</option>
              </select>
            </div>

            {/* スキル要件（OR条件） */}
            <div className="regulation-immigration-skills">
              <span className="regulation-immigration-skills__title">
                {t("regulation.immigrationSkillThresholds")}
              </span>
              <p className="regulation-immigration-skills__hint">
                {t("regulation.immigrationSkillHint")}
              </p>
              <div className="regulation-skill-list">
                {skillEntries.map((skill) => {
                  const value = getSkillThreshold(skill.id);
                  const cityValue = cityImmigration?.immigration_skill_thresholds[String(skill.id)] ?? 0;
                  const modified = value !== cityValue;
                  return (
                    <div key={skill.id} className="regulation-skill-row">
                      <span
                        className={[
                          "regulation-skill-row__name",
                          modified && "regulation-skill-row__name--modified",
                        ]
                          .filter(Boolean)
                          .join(" ")}
                      >
                        {skill.display_name}
                      </span>
                      <div className="regulation-skill-row__control">
                        <input
                          type="range"
                          min={0}
                          max={1.5}
                          step={0.05}
                          className="regulation-skill-slider"
                          value={value}
                          onChange={(e) => handleSkillThresholdChange(skill.id, parseFloat(e.target.value))}
                        />
                        <span className="regulation-skill-value">{value.toFixed(2)}</span>
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>

            {/* 受け入れ上限人数（クオータ） */}
            <div className="regulation-immigration-row">
              <span
                className={[
                  "regulation-immigration-row__name",
                  isImmModified("immigration_quota") && "regulation-immigration-row__name--modified",
                ]
                  .filter(Boolean)
                  .join(" ")}
              >
                {t("regulation.immigrationQuota")}
              </span>
              <div className="regulation-immigration-row__input-group">
                <input
                  type="number"
                  min={0}
                  step={1}
                  className={[
                    "regulation-immigration-input",
                    isImmModified("immigration_quota") && "regulation-immigration-input--modified",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  value={getImmScalar("immigration_quota") ?? 0}
                  onChange={(e) => handleImmNumberChange("immigration_quota", e.target.value)}
                />
                <span className="regulation-immigration-separator">
                  {t("regulation.immigrationQuotaUnit")}
                </span>
              </div>
            </div>
            <p className="regulation-immigration-quota-hint">
              {t("regulation.immigrationQuotaHint")}
            </p>
          </div>
        </section>
        </div>
      </div>

      {error && <div className="regulation-window__error-inline">{error}</div>}

      <div className="regulation-window__footer">
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
