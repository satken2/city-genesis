import { useState, useEffect, useCallback, useMemo } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { ParamGraphOverlay, type ParamGraph } from "./ParamGraphOverlay";
import "./ParameterSetupWindow.css";

type ParameterSetupWindowProps = {
  apiBaseUrl: string;
};

type RawParams = {
  GENERAL_PARAMS: Record<string, unknown>;
  // GENERAL_PARAMS のキー → カテゴリ名(例: "ENVIRONMENT")のマップ。
  // スプレッドシートの category 列から general_params.yaml 経由で届く表示メタデータで、
  // ここに含まれるキーだけがこのウィンドウに表示される(含まれないキー=内部パラメータ)。
  // タブ順はマップの初出順、タブ内の並びはマップのキー順(=シートの行順)。
  GENERAL_PARAMS_CATEGORIES: Record<string, string>;
  // GENERAL_PARAMS のキー → 表示名・説明文。スプレッドシートの display_name / description 列が
  // general_params.yaml 経由で届く。i18n に個別訳(paramSetup.param.{キー})が無いパラメータは
  // これを表示するので、シートに1行足すだけで新パラメータがUIに出せる。
  GENERAL_PARAMS_DISPLAY: Record<string, ParamDisplay>;
  // GENERAL_PARAMS のキー → グラフ編集の定義。ここにあるパラメータはテキストボックスの代わりに
  // グラフ編集を開くボタンで表示する。
  GENERAL_PARAMS_GRAPHS: Record<string, ParamGraph>;
  // パラメータを変更できる状態か（都市創設前 or テンプレート作成モード）。
  can_edit: boolean;
};

type ParamDisplay = {
  label?: string;
  desc?: string;
};

// バックエンドのパラメータ取得APIが返却するレスポンスの型定義。
// 経済シミュレーションのパラメータは、APIレスポンスの parameters プロパティ内に格納されている。
type ApiResponse = {
  success: boolean;
  parameters: {
    GENERAL_PARAMS: Record<string, unknown>;
    GENERAL_PARAMS_CATEGORIES?: Record<string, string>;
    GENERAL_PARAMS_DISPLAY?: Record<string, ParamDisplay>;
    GENERAL_PARAMS_GRAPHS?: Record<string, ParamGraph>;
  };
  can_edit: boolean;
};

// 編集値。数値パラメータと真偽値パラメータ(SPRAWL_ENABLED 等)の両方を扱う。
type EditedGroup = Record<string, number | boolean>;
type EditedParams = {
  GENERAL_PARAMS?: EditedGroup;
};

type CategoryDef = {
  // タブID。カテゴリ名の小文字(例: "environment")。i18nキー paramSetup.category.{id} に対応する。
  id: string;
  keys: string[];
};

// 数値入力のステップ幅。バックエンドはステップ情報を持たないため、UI側の調整値として保持する。
// ここに無いキーは defaultStep() が現在値の桁から自動決定する。
const PARAM_STEPS: Record<string, number> = {
  TFR_TARGET: 0.1,
  MARRIAGE_RATE: 0.5,
  MIN_CHILDBEARING_AGE: 1,
  MAX_CHILDBEARING_AGE: 1,
  INFLOW_MARRIRD_RATIO: 0.05,
  INFLOW_CHILDREN1_RATIO: 0.05,
  INFLOW_CHILDREN2_RATIO: 0.05,
  INFLOW_CHILDREN3_RATIO: 0.05,
  IMMIGRATION_ADULT_AGE_MIN: 1,
  IMMIGRATION_ADULT_AGE_MAX: 1,
  LIFESPAN_FACTOR: 0.01,
  SPACE_SINGLE: 5,
  ADD_SPACE_MARRIED: 5,
  ADD_SPACE_CHILDREN: 5,
  HOUSEHOLD_RENT_INCOME_RATIO: 0.05,
  HOUSEHOLD_TIGHT_BUDGET_RATIO: 0.05,
  HOUSEHOLD_DECIDE_MOVE_COUNT: 1,
  HOUSEHOLD_SHACK_COUNT: 1,
  MIN_PROFIT_RATE: 0.01,
  SPAWN_PROFIT_RATE_THRESHOLD: 0.01,
  DIVIDEND_PAYOUT_RATIO: 0.05,
  MIN_PRODUCTION_LIMIT: 0.01,
  MAX_LONGRUN_BONUS_DAYS: 100,
  EXPORT_COMPETITIVENESS_ZERO_RATIO: 0.05,
  SPAWN_LABOR_CAP_RATIO: 0.05,
  DEVELOPER_PROFIT_RATIO: 0.01,
  INVESTMENT_DEPRECIATION_RATE: 0.01,
  WORK_HOURS: 1,
  NORMAL_LABOR_UTILITY: 0.05,
  HIGH_SKILL_BOUNDARY: 0.1,
  MAX_BASE_SKILL: 0.1,
  MAX_SKILL: 0.5,
  WAGE_OVERSTAFFED_THRESHOLD: 0.05,
  WAGE_UNDERSTAFFED_THRESHOLD: 0.05,
  ELEMENTARY_START_AGE: 1,
  ELEMENTARY_END_AGE: 1,
  UNIVERSITY_END_AGE: 1,
  MIN_WORKING_AGE: 1,
  EDUCATION_CAPACITY_PER_FLOOR: 0.01,
  SCHOOL_SEARCH_MAX_DISTANCE: 10,
  BUILDING_FLOOR_LIMIT: 5,
  FLOOR_PENALTY_FACTOR: 1000,
  BUILDING_ASPECT_RATIO_MAX: 0.5,
  DEMOLITION_RATE: 1,
  REAL_ESTATE_SUPPLY_SURPLUS_RATE: 0.01,
  CONSTRUCTION_DEMAND_SITE_AREA_MIN: 100,
  CONSTRUCTION_DEMAND_SITE_AREA_MAX: 100,
  COLLAPSE_SHAPE: 0.1,
  MAINTENANCE_DURABILITY_THRESHOLD: 0.05,
  MAINTENANCE_COST_RATIO: 0.05,
  DEMOLITION_DURABILITY_THRESHOLD: 0.05,
  SHUTDOWN_GRACE_PERIOD_DAYS: 30,
  NOISE_ROAD_TRAFFIC_COEFF: 0.05,
  NOISE_PED_TRAFFIC_COEFF: 0.05,
  PED_NOISE_THRESHOLD: 1000,
  NOISE_ATTENUATION_M: 10,
  NOISE_SATURATION_LEVEL: 10,
  POLLUTION_ROAD_TRAFFIC_COEFF: 0.05,
  POLLUTION_ATTENUATION_M: 10,
  POLLUTION_SATURATION_LEVEL: 10,
  UNEMPLOYMENT_CRIME_WEIGHT: 0.05,
  CRIME_BLUR_SIGMA_M: 10,
  CRIME_SATURATION_LEVEL: 0.1,
  LIVING_MULTIPLIER_MIN: 0.05,
  LIVING_MULTIPLIER_MAX: 0.05,
  PEDESTRIAN_SPEED: 0.5,
  PATH_ACTIVE_HOURS: 1,
  ROAD_MANY_TRAFFIC_THRESHOLD: 1000,
  PED_MANY_TRAFFIC_THRESHOLD: 1000,
  RESOURCE_EFFICIENCY_THRESHOLD: 0.05,
};

// PARAM_STEPS に無いキーのステップ幅を現在値の桁から自動決定する。
// 先頭桁の1/10(例: 値40.0→ステップ1、値0.05→ステップ0.001)を返す。
function defaultStep(value: number): number {
  const abs = Math.abs(value);
  if (abs === 0 || !isFinite(abs)) return 1;
  return Math.pow(10, Math.floor(Math.log10(abs))) / 10;
}

function stepFor(key: string, value: unknown): number {
  return PARAM_STEPS[key] ?? defaultStep(typeof value === "number" ? value : 0);
}

// カテゴリマップ(キー→カテゴリ名)からタブ定義を組み立てる。
// タブ順はカテゴリの初出順、タブ内のパラメータ順はマップのキー順(=シートの行順)。
function buildCategories(map: Record<string, string>): CategoryDef[] {
  const cats: CategoryDef[] = [];
  const byId = new Map<string, CategoryDef>();
  for (const [key, rawCategory] of Object.entries(map)) {
    const id = rawCategory.toLowerCase();
    let def = byId.get(id);
    if (!def) {
      def = { id, keys: [] };
      byId.set(id, def);
      cats.push(def);
    }
    def.keys.push(key);
  }
  return cats;
}

export function ParameterSetupWindow({ apiBaseUrl }: ParameterSetupWindowProps) {
  const { t } = useTranslation();
  const [rawParams, setRawParams] = useState<RawParams | null>(null);
  const [edited, setEdited] = useState<EditedParams>({});
  // 空文字列は「先頭タブを選択」を意味する(タブ構成はAPI取得後に決まるため)。
  const [activeCategory, setActiveCategory] = useState<string>("");
  const [busy, setBusy] = useState(false);
  const [fetching, setFetching] = useState(false);
  const [fetchError, setFetchError] = useState(false);
  // ゲーム内編集モード（テンプレート作成モード中にアイコンパネルから開いた再編集）かどうか。
  // 初期セットアップ（新規ゲーム開始）では false。編集モードでは完了しても game:start を
  // 送らず、ウィンドウを閉じるだけにする。
  const [editMode, setEditMode] = useState(false);
  // グラフ編集を開いているパラメータ。null なら閉じている。
  const [graphKey, setGraphKey] = useState<string | null>(null);

  useEffect(() => {
    // 起動時にどのモードで開かれたかを Rust 側へ問い合わせる。
    invoke<boolean>("get_parameter_setup_mode")
      .then((mode) => setEditMode(Boolean(mode)))
      .catch(() => setEditMode(false));
  }, []);

  const fetchParams = useCallback(() => {
    if (!apiBaseUrl) return;
    const base = apiBaseUrl.replace(/\/+$/, "");
    setFetching(true);
    setFetchError(false);
    fetch(`${base}/api/v1/parameters`)
      .then((r) => r.json())
      .then((data: ApiResponse) => {
        // APIから返されたオブジェクトのうち、パラメータ本体・カテゴリ分類・編集可否を抽出して保持する。
        setRawParams({
          GENERAL_PARAMS: data.parameters.GENERAL_PARAMS,
          GENERAL_PARAMS_CATEGORIES: data.parameters.GENERAL_PARAMS_CATEGORIES ?? {},
          GENERAL_PARAMS_DISPLAY: data.parameters.GENERAL_PARAMS_DISPLAY ?? {},
          GENERAL_PARAMS_GRAPHS: data.parameters.GENERAL_PARAMS_GRAPHS ?? {},
          can_edit: data.can_edit,
        });
        setFetching(false);
      })
      .catch(() => {
        setFetchError(true);
        setFetching(false);
      });
  }, [apiBaseUrl]);

  useEffect(() => {
    fetchParams();
  }, [fetchParams]);

  // APIのカテゴリ分類からタブ構成を組み立てる(カテゴリ未設定のパラメータは表示しない)。
  const categories = useMemo(
    () => buildCategories(rawParams?.GENERAL_PARAMS_CATEGORIES ?? {}),
    [rawParams]
  );

  // 型は問わず現在値をそのまま返す(辞書値パラメータの表示にも使うため)。
  const getValue = useCallback(
    (key: string): unknown => {
      const editedVal = edited.GENERAL_PARAMS?.[key];
      if (editedVal !== undefined) return editedVal;
      return rawParams?.GENERAL_PARAMS?.[key];
    },
    [edited, rawParams]
  );

  // グラフの式に渡す数値パラメータの現在値(このウィンドウで編集中の値を含む)。
  const numericValues = useMemo(() => {
    const vars: Record<string, number> = {};
    for (const [key, val] of Object.entries({ ...rawParams?.GENERAL_PARAMS, ...edited.GENERAL_PARAMS })) {
      if (typeof val === "number") vars[key] = val;
    }
    return vars;
  }, [rawParams, edited]);

  const setValue = useCallback((key: string, val: number | boolean) => {
    setEdited((prev) => ({
      ...prev,
      GENERAL_PARAMS: { ...prev.GENERAL_PARAMS, [key]: val },
    }));
  }, []);

  // 編集済みのパラメータがあれば PATCH で反映する。失敗しても処理は続行する
  // （初期セットアップではゲームは開始し、編集モードでは単に反映されないだけ）。
  const patchEdits = useCallback(async () => {
    const base = apiBaseUrl.replace(/\/+$/, "");
    const hasEdits = Object.keys(edited.GENERAL_PARAMS ?? {}).length > 0;
    if (!hasEdits) return;
    try {
      await fetch(`${base}/api/v1/parameters`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(edited),
      });
    } catch {
      /* 失敗しても続行する */
    }
  }, [apiBaseUrl, edited]);

  // 初期セットアップ（新規ゲーム）: パラメータを反映してゲームを開始する。
  const handleStart = useCallback(async () => {
    if (busy) return;
    setBusy(true);
    await patchEdits();
    await invoke("finish_parameter_setup");
  }, [busy, patchEdits]);

  // 初期セットアップ（新規ゲーム）: パラメータを変更せずデフォルトで開始する。
  const handleSkip = useCallback(async () => {
    if (busy) return;
    setBusy(true);
    await invoke("finish_parameter_setup");
  }, [busy]);

  // ゲーム内編集モード: パラメータを反映してウィンドウを閉じる（game:start は送らない）。
  const handleApply = useCallback(async () => {
    if (busy) return;
    setBusy(true);
    await patchEdits();
    await invoke("close_parameter_setup_window");
  }, [busy, patchEdits]);

  // ゲーム内編集モード: 変更を破棄してウィンドウを閉じる。
  const handleClose = useCallback(async () => {
    if (busy) return;
    setBusy(true);
    await invoke("close_parameter_setup_window");
  }, [busy]);

  // activeCategory が未選択(初期状態)や構成変更で消えた場合は先頭タブへフォールバックする。
  const activeCat = categories.find((c) => c.id === activeCategory) ?? categories[0];

  // i18n未登録ならシート由来の表示名、それも無ければキー名を出す。
  const labelOf = (key: string) =>
    t(`paramSetup.param.${key}.label`, { defaultValue: rawParams?.GENERAL_PARAMS_DISPLAY[key]?.label || key });

  return (
    <div className="param-setup-window">
      <div className="param-setup-header" data-tauri-drag-region>
        <h1 className="param-setup-title">{t("paramSetup.title")}</h1>
        <p className="param-setup-desc">{t("paramSetup.description")}</p>
      </div>

      {fetchError ? (
        <div className="param-setup-error">
          <span>{t("paramSetup.fetchError")}</span>
          <button className="param-setup-reload-btn" onClick={fetchParams} type="button">
            {t("paramSetup.reload")}
          </button>
        </div>
      ) : rawParams === null || fetching ? (
        <div className="param-setup-loading">
          <span>{t("paramSetup.loading")}</span>
        </div>
      ) : (
        <div className="param-setup-body">
          <nav className="param-setup-nav">
            {categories.map((cat) => (
              <button
                key={cat.id}
                className={`param-setup-nav-btn ${activeCat?.id === cat.id ? "active" : ""}`}
                onClick={() => setActiveCategory(cat.id)}
                type="button"
                disabled={busy}
              >
                {/* 未知のカテゴリ(i18n未登録)はカテゴリ名をそのまま表示する */}
                {t(`paramSetup.category.${cat.id}`, { defaultValue: cat.id })}
              </button>
            ))}
          </nav>

          <div className="param-setup-params">
            {(activeCat?.keys ?? []).map((key) => {
              const val = getValue(key);
              const step = stepFor(key, val);
              const display = rawParams.GENERAL_PARAMS_DISPLAY[key] ?? {};
              return (
                <div key={key} className="param-setup-row">
                  <div className="param-setup-label-block">
                    <span className="param-setup-label">{labelOf(key)}</span>
                    <span className="param-setup-desc-text">
                      {t(`paramSetup.param.${key}.desc`, { defaultValue: display.desc ?? "" })}
                    </span>
                  </div>
                  {typeof val === "boolean" ? (
                    <input
                      className="param-setup-input param-setup-input--check"
                      type="checkbox"
                      checked={val}
                      onChange={(e) => setValue(key, e.target.checked)}
                      disabled={busy}
                    />
                  ) : typeof val === "number" && rawParams.GENERAL_PARAMS_GRAPHS[key] ? (
                    <button
                      className="param-setup-input param-setup-graph-btn"
                      type="button"
                      onClick={() => setGraphKey(key)}
                      disabled={busy}
                    >
                      <svg viewBox="0 0 16 16" aria-hidden="true">
                        <polyline points="1,14 6,9 9,11 15,3" fill="none" stroke="currentColor" strokeWidth="1.6" />
                      </svg>
                      {String(val)}
                    </button>
                  ) : typeof val === "number" ? (
                    <input
                      className="param-setup-input"
                      type="number"
                      step={step}
                      value={String(val)}
                      onChange={(e) => {
                        const n = parseFloat(e.target.value);
                        if (!isNaN(n)) setValue(key, n);
                      }}
                      disabled={busy}
                    />
                  ) : (
                    /* 辞書値パラメータ(HOUSEHOLD_DEMAND_SCALE 等)はこのウィンドウでは編集できない。
                       数値入力に落とすと値を壊すので、現在値を読み取り専用で見せるだけにする。 */
                    <input
                      className="param-setup-input param-setup-input--readonly"
                      type="text"
                      value={JSON.stringify(val ?? null)}
                      readOnly
                      disabled
                    />
                  )}
                </div>
              );
            })}
          </div>
        </div>
      )}

      <div className="param-setup-footer">
        <div className="param-setup-footer-left">
          {/* 初期セットアップでは「スキップして開始」、編集モードでは「閉じる（破棄）」 */}
          <button
            className="param-setup-btn param-setup-btn--skip"
            onClick={() => void (editMode ? handleClose() : handleSkip())}
            disabled={busy}
            type="button"
          >
            {editMode ? t("paramSetup.close") : t("paramSetup.skip")}
          </button>
          <button
            className="param-setup-btn param-setup-btn--reload"
            onClick={fetchParams}
            disabled={busy || fetching}
            type="button"
            title={t("paramSetup.reloadHint")}
          >
            {fetching ? t("paramSetup.loading") : t("paramSetup.reload")}
          </button>
        </div>
        {/* 初期セットアップでは「ゲーム開始」、編集モードでは「適用して閉じる」 */}
        <button
          className="param-setup-btn param-setup-btn--start"
          onClick={() => void (editMode ? handleApply() : handleStart())}
          disabled={busy || rawParams === null}
          type="button"
        >
          {editMode ? t("paramSetup.apply") : t("paramSetup.start")}
        </button>
      </div>

      {graphKey && rawParams?.GENERAL_PARAMS_GRAPHS[graphKey] && (
        <ParamGraphOverlay
          paramKey={graphKey}
          label={labelOf(graphKey)}
          graph={rawParams.GENERAL_PARAMS_GRAPHS[graphKey]}
          vars={numericValues}
          initialValue={numericValues[graphKey]}
          step={stepFor(graphKey, numericValues[graphKey])}
          onApply={(value) => {
            setValue(graphKey, value);
            setGraphKey(null);
          }}
          onClose={() => setGraphKey(null)}
        />
      )}
    </div>
  );
}
