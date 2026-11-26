/**
 * 都市の自動開発ウィンドウ。
 * 左パネルをタブ構成とし、1枚目に段階的な「都市の自動拡張」、2枚目に従来のパターン別「範囲開発」を置く。
 * 塗った範囲(strokes)はタブ間で保持され、同じ場所で結果を比較できる。
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import { PATH_STREET, PLAY_MODE_SCENARIO } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { usePlayMode } from "../../hooks/usePlayMode";
import { buildablePathMaster, buildCategoryVariants, type PathVariantOption } from "../path/pathBuildCategories";
import { clearHintbarTextIf, setHintbarText } from "../../utils/hintbar";
import { IPC_EVENT_CAMERA_STATE, sendCameraQueryState, sendSessionReload } from "../../utils/ipc";
import { FlatTabs, type FlatTab } from "../common/FlatTabs";
import { CityMapCanvas, type CityMapCanvasHandle } from "../map/CityMapCanvas";
import { useBrushStrokes } from "./developmentMapOverlay";
import { useAutoGrowthWorkflow } from "./useAutoGrowthWorkflow";
import { DEVELOPMENT_PATTERNS, useDevelopmentBrush, type DevelopmentPattern } from "./useDevelopmentBrush";
import "./CityDevelopmentWindow.css";

type DevelopmentTab = "auto_growth" | "area_fill";

const CAMERA_HALF_EXTENT_M = 600;
const NO_VARIANTS: number[] = [];
const MAP_LAYERS = { elevation: true, parcels: true, buildings: false };

// APIの既定と同じく、勾配制限が厳しい規格を優先し、同じ制限なら幅が広い規格を選ぶ。
function defaultRoad(choices: PathVariantOption[], lanes: number) {
  const grade = ({ data }: PathVariantOption) => {
    const limit = data.max_grade ?? 0;
    return limit > 0 ? limit : Infinity;
  };
  return choices.filter((choice) => choice.data.lanes === lanes).sort((a, b) =>
    (grade(a) - grade(b)) || (b.data.width - a.data.width) || (a.id - b.id),
  )[0];
}

export function CityDevelopmentWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const scenarioMode = usePlayMode(apiBaseUrl) === PLAY_MODE_SCENARIO;
  const mapRef = useRef<CityMapCanvasHandle>(null);
  const [activeTab, setActiveTab] = useState<DevelopmentTab>("auto_growth");
  const [pattern, setPattern] = useState<DevelopmentPattern>("planned_city_1");
  const [overlayVersion, setOverlayVersion] = useState(0);
  const [selection, setSelection] = useState<number[]>();
  const [isBusy, setIsBusy] = useState(false);

  const choices = useMemo(
    () =>
      buildCategoryVariants(buildablePathMaster(masterData, scenarioMode), PATH_STREET)
        .filter(({ data }) => !data.height_editable && data.frontage_allowed),
    [masterData, scenarioMode],
  );

  const variants = useMemo(() => {
    const available = selection?.filter((id) => choices.some((choice) => choice.id === id));
    if (available && (available.length > 0 || selection?.length === 0)) return available;
    if (choices.length === 0) return NO_VARIANTS;
    const local = defaultRoad(choices, 1);
    const arterial = defaultRoad(choices, 2);
    return local && arterial ? [local.id, arterial.id] : [(local ?? arterial ?? choices[0]).id];
  }, [selection, choices]);

  const toggleVariant = (id: number, checked: boolean) => {
    setSelection(checked ? [...variants, id] : variants.filter((value) => value !== id));
  };

  const reload = useCallback(async () => {
    await sendSessionReload();
    mapRef.current?.reload();
  }, []);

  const clearMessagesRef = useRef<() => void>(() => {});
  const brush = useBrushStrokes(isBusy, () => clearMessagesRef.current());

  const autoGrowth = useAutoGrowthWorkflow({
    apiBaseUrl,
    choices,
    strokes: brush.strokes,
    clearArea: brush.clearArea,
    reload,
  });

  const areaFill = useDevelopmentBrush(pattern, {
    apiBaseUrl,
    variants,
    strokes: brush.strokes,
    clearArea: brush.clearArea,
    reload,
  });

  clearMessagesRef.current = () => {
    autoGrowth.clearMessage();
    areaFill.clearMessage();
  };

  const activeWorkflow = activeTab === "auto_growth" ? autoGrowth : areaFill;
  const running = activeWorkflow.running;
  const busy = autoGrowth.running !== null || areaFill.running !== null;

  useEffect(() => {
    setIsBusy(busy);
  }, [busy]);

  const tabs = useMemo<FlatTab<DevelopmentTab>[]>(
    () => [
      { id: "auto_growth", label: t("cityDevelopment.tabs.autoGrowth"), disabled: busy },
      { id: "area_fill", label: t("cityDevelopment.tabs.areaFill"), disabled: busy },
    ],
    [t, busy],
  );

  const handleClearArea = () => {
    brush.clearArea();
    clearMessagesRef.current();
  };

  useEffect(() => {
    const hint = activeWorkflow.hint;
    void setHintbarText(hint).catch(console.error);
    return () => {
      void clearHintbarTextIf(hint).catch(console.error);
    };
  }, [activeWorkflow.hint]);

  useEffect(() => setOverlayVersion((version) => version + 1), [activeWorkflow.overlayDraw, activeTab]);

  useEffect(() => {
    let disposed = false;
    let fitted = false;
    const subscription = listen<{ position?: number[] }>(IPC_EVENT_CAMERA_STATE, ({ payload }) => {
      const pos = payload?.position;
      if (disposed || fitted || !pos || pos.length < 3) return;
      fitted = true;
      mapRef.current?.fitBounds({
        x1: pos[0] - CAMERA_HALF_EXTENT_M,
        z1: pos[2] - CAMERA_HALF_EXTENT_M,
        x2: pos[0] + CAMERA_HALF_EXTENT_M,
        z2: pos[2] + CAMERA_HALF_EXTENT_M,
      });
    });
    void subscription.then(() => {
      if (!disposed) return sendCameraQueryState();
    }).catch(console.error);
    return () => {
      disposed = true;
      void subscription.then((unlisten) => unlisten()).catch(console.error);
    };
  }, []);

  return (
    <div className="city-development">
      <aside className="city-development__sidebar">
        <div className="city-development__tabs">
          <FlatTabs
            tabs={tabs}
            active={activeTab}
            onChange={setActiveTab}
            idPrefix="city-development"
          />
        </div>
        <div
          className="city-development__panel"
          role="tabpanel"
          id={`city-development-panel-${activeTab}`}
          aria-labelledby={`city-development-tab-${activeTab}`}
        >
          {activeTab === "auto_growth" ? (
            autoGrowth.settings
          ) : (
            <>
              <label className="city-development__field">
                <span>{t("cityDevelopment.pattern")}</span>
                <select
                  value={pattern}
                  disabled={busy}
                  data-guide="city_development.pattern"
                  onChange={(event) => setPattern(event.currentTarget.value as DevelopmentPattern)}
                >
                  {DEVELOPMENT_PATTERNS.map((key) => (
                    <option key={key} value={key}>{t(`cityDevelopment.patterns.${key}`)}</option>
                  ))}
                </select>
              </label>
              <fieldset className="city-development__variants" disabled={busy}>
                <legend>{t("cityDevelopment.roadVariants")}</legend>
                {choices.map(({ id, data }) => (
                  <label key={id} className="city-development__variant">
                    <input
                      type="checkbox"
                      checked={variants.includes(id)}
                      onChange={(e) => toggleVariant(id, e.currentTarget.checked)}
                    />
                    {data.display_name}
                  </label>
                ))}
              </fieldset>
              {areaFill.settings}
            </>
          )}
          <div className="city-development__actions">
            <button type="button" disabled={busy || brush.strokes.length === 0} onClick={handleClearArea}>
              {t("cityDevelopment.clear")}
            </button>
            <button
              type="button"
              disabled={!activeWorkflow.canPreview}
              data-guide="city_development.preview"
              onClick={() => void activeWorkflow.preview()}
            >
              {running === "preview" ? t("cityDevelopment.previewing") : t("cityDevelopment.preview")}
            </button>
            <button
              type="button"
              className="city-development__primary"
              disabled={!activeWorkflow.canCreate}
              data-guide="city_development.create"
              onClick={() => void activeWorkflow.create()}
            >
              {running === "create" ? t("cityDevelopment.creating") : t("cityDevelopment.create")}
            </button>
          </div>
        </div>
      </aside>
      <div className="city-development__map">
        <CityMapCanvas
          ref={mapRef}
          apiBaseUrl={apiBaseUrl}
          layers={MAP_LAYERS}
          interactive
          showCompass
          overlayVersion={overlayVersion}
          cursor="crosshair"
          overlayDraw={activeWorkflow.overlayDraw}
          {...brush.pointerProps}
        />
        {activeWorkflow.message && (
          <div className="city-development__message" role="status">{activeWorkflow.message}</div>
        )}
      </div>
    </div>
  );
}
