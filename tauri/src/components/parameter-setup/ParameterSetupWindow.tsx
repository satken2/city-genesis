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
  // Map from GENERAL_PARAMS keys to category names (e.g. "ENVIRONMENT").
  // Display metadata comes from the spreadsheet's category column through general_params.yaml;
  // only included keys appear in this window (omitted keys are internal parameters).
  // Tabs follow first category occurrence; parameters follow map key order (= spreadsheet row order).
  GENERAL_PARAMS_CATEGORIES: Record<string, string>;
  // Map from GENERAL_PARAMS keys to display names/descriptions. The spreadsheet's display_name / description columns
  // arrive through general_params.yaml. Parameters without a dedicated i18n translation (paramSetup.param.{key})
  // use this metadata, so adding a spreadsheet row alone can expose a new parameter in the UI.
  GENERAL_PARAMS_DISPLAY: Record<string, ParamDisplay>;
  // Map from GENERAL_PARAMS keys to graph editor definitions. These parameters use buttons opening
  // the graph editor instead of text fields.
  GENERAL_PARAMS_GRAPHS: Record<string, ParamGraph>;
  // Whether parameters are editable (before founding the city or in scenario authoring mode).
  can_edit: boolean;
};

type ParamDisplay = {
  label?: string;
  desc?: string;
};

// Response type for the backend parameter retrieval API.
// Economic simulation parameters are stored in the API response's parameters property.
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

// Edited values, covering both numeric and boolean parameters (such as SPRAWL_ENABLED).
type EditedGroup = Record<string, number | boolean>;
type EditedParams = {
  GENERAL_PARAMS?: EditedGroup;
};

type CategoryDef = {
  // Tab ID: lowercase category name (e.g. "environment"), matching i18n paramSetup.category.{id}.
  id: string;
  keys: string[];
};

// Numeric input steps. The backend provides no step metadata, so these are UI tuning values.
// For unlisted keys, defaultStep() derives the step from the current value's magnitude.
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

// Derive steps for keys absent from PARAM_STEPS from the current value's magnitude.
// Return one tenth of its leading place value (e.g. 40.0 → 1, 0.05 → 0.001).
function defaultStep(value: number): number {
  const abs = Math.abs(value);
  if (abs === 0 || !isFinite(abs)) return 1;
  return Math.pow(10, Math.floor(Math.log10(abs))) / 10;
}

function stepFor(key: string, value: unknown): number {
  return PARAM_STEPS[key] ?? defaultStep(typeof value === "number" ? value : 0);
}

// Build tab definitions from the category map (key → category name).
// Tabs follow first category occurrence; parameters follow map key order (= spreadsheet row order).
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
  // An empty string selects the first tab; tab structure is determined only after API retrieval.
  const [activeCategory, setActiveCategory] = useState<string>("");
  const [busy, setBusy] = useState(false);
  const [fetching, setFetching] = useState(false);
  const [fetchError, setFetchError] = useState(false);
  // Whether this is in-game editing (reopened from the icon panel in scenario authoring mode).
  // False during initial setup for a new game. Completing edit mode closes the window
  // without sending game:start.
  const [editMode, setEditMode] = useState(false);
  // Parameter currently open in the graph editor; null when closed.
  const [graphKey, setGraphKey] = useState<string | null>(null);

  useEffect(() => {
    // Ask Rust which mode the window opened in on startup.
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
        // Extract parameters, category assignments, and editability from the API response.
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

  // Build tabs from API categories; do not show parameters without a category.
  const categories = useMemo(
    () => buildCategories(rawParams?.GENERAL_PARAMS_CATEGORIES ?? {}),
    [rawParams]
  );

  // Return the current value regardless of type, since dictionary parameters also use this for display.
  const getValue = useCallback(
    (key: string): unknown => {
      const editedVal = edited.GENERAL_PARAMS?.[key];
      if (editedVal !== undefined) return editedVal;
      return rawParams?.GENERAL_PARAMS?.[key];
    },
    [edited, rawParams]
  );

  // Current numeric parameters for graph formulas, including unsaved values in this window.
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

  // Apply edited parameters via PATCH, continuing even on failure.
  // (Initial setup still starts the game; edit mode simply leaves the changes unapplied.)
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
      /* Continue even on failure. */
    }
  }, [apiBaseUrl, edited]);

  // Initial setup (new game): apply parameters and start the game.
  const handleStart = useCallback(async () => {
    if (busy) return;
    setBusy(true);
    await patchEdits();
    await invoke("finish_parameter_setup");
  }, [busy, patchEdits]);

  // Initial setup (new game): start with defaults without changing parameters.
  const handleSkip = useCallback(async () => {
    if (busy) return;
    setBusy(true);
    await invoke("finish_parameter_setup");
  }, [busy]);

  // In-game editing: apply parameters and close, without sending game:start.
  const handleApply = useCallback(async () => {
    if (busy) return;
    setBusy(true);
    await patchEdits();
    await invoke("close_parameter_setup_window");
  }, [busy, patchEdits]);

  // In-game editing: discard changes and close.
  const handleClose = useCallback(async () => {
    if (busy) return;
    setBusy(true);
    await invoke("close_parameter_setup_window");
  }, [busy]);

  // Fall back to the first tab if activeCategory is unset initially or disappears after a structure change.
  const activeCat = categories.find((c) => c.id === activeCategory) ?? categories[0];

  // Without an i18n entry, use the spreadsheet display name, then the key as a final fallback.
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
                {/* Display unknown categories (without i18n entries) using their category names. */}
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
                    /*
 * Dictionary parameters (such as HOUSEHOLD_DEMAND_SCALE) cannot be edited in this window.
 * Numeric input would corrupt their values, so show current values as read-only.
 */
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
          {/* Initial setup: Skip and Start; edit mode: Close (Discard). */}
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
        {/* Initial setup: Start Game; edit mode: Apply and Close. */}
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
