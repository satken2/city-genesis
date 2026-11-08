/**
 * 都市の自動開発と道路だけの作成で、アルゴリズム選択と地図を共用する。
 * 範囲は入力方法ごとに保持し、アルゴリズムを切り替えても地図の位置と縮尺を保つ。
 */
import { useEffect, useMemo, useRef, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import { PATH_STREET, PLAY_MODE_SCENARIO } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { usePlayMode } from "../../hooks/usePlayMode";
import { buildablePathMaster, buildCategoryVariants, type PathVariantOption } from "../path/pathBuildCategories";
import { clearHintbarTextIf, setHintbarText } from "../../utils/hintbar";
import { IPC_EVENT_CAMERA_STATE, sendCameraQueryState, sendSessionReload } from "../../utils/ipc";
import { CityMapCanvas, type CityMapCanvasHandle } from "../map/CityMapCanvas";
import { DEVELOPMENT_PATTERNS, useDevelopmentBrush, type DevelopmentPattern } from "./useDevelopmentBrush";
import { BLOCK_PATTERNS, STREET_PATTERNS, useStreetGrid, type StreetPattern } from "./useStreetGrid";
import "./CityDevelopmentWindow.css";

type Algorithm = DevelopmentPattern | StreetPattern;
type Operation = "preview" | "create" | null;

export type DevelopmentWorkflowOptions = {
  apiBaseUrl: string;
  variants: number[];
  running: Operation;
  setRunning: (operation: Operation) => void;
  reload: () => Promise<void>;
};

const CAMERA_HALF_EXTENT_M = 600;
const NO_VARIANTS: number[] = [];
const DEVELOPMENT_LAYERS = { elevation: true, parcels: true, buildings: false };
const STREET_LAYERS = { elevation: true, buildings: false };

// APIの既定と同じく、勾配制限が厳しい規格を優先し、同じ制限なら幅が広い規格を選ぶ。
function defaultRoad(choices: PathVariantOption[], lanes: number) {
  const grade = ({ data }: PathVariantOption) => {
    const limit = data.max_grade ?? 0;
    return limit > 0 ? limit : Infinity;
  };
  return choices.filter(choice => choice.data.lanes === lanes).sort((a, b) =>
    (grade(a) - grade(b)) || (b.data.width - a.data.width) || (a.id - b.id),
  )[0];
}

export function CityDevelopmentWindow({ apiBaseUrl, initialRoadsOnly = false }: { apiBaseUrl: string; initialRoadsOnly?: boolean }) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const scenarioMode = usePlayMode(apiBaseUrl) === PLAY_MODE_SCENARIO;
  const mapRef = useRef<CityMapCanvasHandle>(null);
  const [algorithm, setAlgorithm] = useState<Algorithm>(initialRoadsOnly ? "relaxed_grid" : "arterial");
  const [running, setRunning] = useState<Operation>(null);
  const [overlayVersion, setOverlayVersion] = useState(0);
  const developmentPattern = DEVELOPMENT_PATTERNS.find(pattern => pattern === algorithm);
  const blockMode = BLOCK_PATTERNS.some(pattern => pattern === algorithm);
  const variantGroup = developmentPattern ?? (blockMode ? "block" : "street");
  const [variantSelections, setVariantSelections] = useState<Partial<Record<typeof variantGroup, number[]>>>({});

  const choices = useMemo(() => buildCategoryVariants(buildablePathMaster(masterData, scenarioMode), PATH_STREET)
    .filter(({ data }) => !data.height_editable && data.frontage_allowed && (!blockMode || data.lanes === 1)),
  [masterData, scenarioMode, blockMode]);

  const selection = variantSelections[variantGroup];
  const variants = useMemo(() => {
    const available = selection?.filter(id => choices.some(choice => choice.id === id));
    if (available && (available.length > 0 || selection?.length === 0)) return available;
    if (choices.length === 0) return NO_VARIANTS;
    const local = defaultRoad(choices, 1);
    const arterial = algorithm === "arterial" ? defaultRoad(choices, 2) : undefined;
    return local && arterial ? [local.id, arterial.id] : [(local ?? arterial ?? choices[0]).id];
  }, [selection, choices, algorithm]);

  const toggleVariant = (id: number, checked: boolean) => {
    setVariantSelections(prev => ({
      ...prev,
      [variantGroup]: checked ? [...variants, id] : variants.filter(value => value !== id),
    }));
  };

  const reload = async () => {
    await sendSessionReload();
    mapRef.current?.reload();
  };

  const options = { apiBaseUrl, running, setRunning, reload };
  const development = useDevelopmentBrush(developmentPattern, { ...options, variants: developmentPattern ? variants : NO_VARIANTS });
  const streetPattern = STREET_PATTERNS.find(pattern => pattern === algorithm)
    ?? BLOCK_PATTERNS.find(pattern => pattern === algorithm);
  const streetGrid = useStreetGrid(streetPattern, { ...options, variants: streetPattern ? variants : NO_VARIANTS });
  const workflow = developmentPattern ? development : streetGrid;
  const { hint, mapProps } = workflow;
  const busy = running !== null;

  useEffect(() => {
    void setHintbarText(hint).catch(console.error);
    return () => {
      void clearHintbarTextIf(hint).catch(console.error);
    };
  }, [hint]);

  useEffect(() => setOverlayVersion(version => version + 1), [mapProps.overlayDraw]);

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
      void subscription.then(unlisten => unlisten()).catch(console.error);
    };
  }, []);

  return (
    <div className="city-development">
      <aside className="city-development__sidebar">
        <label className="city-development__field">
          <span>{t("cityDevelopment.pattern")}</span>
          <select
            value={algorithm}
            disabled={busy}
            data-guide="city_development.pattern"
            onChange={event => setAlgorithm(event.currentTarget.value as Algorithm)}
          >
            <optgroup label={t("windowType.city_development")}>
              {DEVELOPMENT_PATTERNS.map(key => (
                <option key={key} value={key}>{t(`cityDevelopment.patterns.${key}`)}</option>
              ))}
            </optgroup>
            <optgroup label={t("scenarioTools.terrainPatterns")}>
              {STREET_PATTERNS.map(key => (
                <option key={key} value={key}>{t(`scenarioTools.patterns.${key}`)}</option>
              ))}
            </optgroup>
            <optgroup label={t("scenarioTools.blockPatterns")}>
              {BLOCK_PATTERNS.map(key => (
                <option key={key} value={key}>{t(`scenarioTools.patterns.${key}`)}</option>
              ))}
            </optgroup>
          </select>
        </label>
        <fieldset className="city-development__variants" disabled={busy}>
          <legend>{t("cityDevelopment.roadVariants")}</legend>
          {choices.map(({ id, data }) => (
            <label key={id} className="city-development__variant">
              <input type="checkbox" checked={variants.includes(id)} onChange={e => toggleVariant(id, e.currentTarget.checked)} />
              {data.display_name}
            </label>
          ))}
        </fieldset>
        {workflow.settings}
        <div className="city-development__actions">
          <button type="button" disabled={busy || !workflow.hasArea} onClick={workflow.clearArea}>
            {t("cityDevelopment.clear")}
          </button>
          <button
            type="button"
            disabled={!workflow.canPreview}
            data-guide="city_development.preview"
            onClick={() => void workflow.preview()}
          >
            {running === "preview" ? t("cityDevelopment.previewing") : t("cityDevelopment.preview")}
          </button>
          <button
            type="button"
            className="city-development__primary"
            disabled={!workflow.canCreate}
            data-guide="city_development.create"
            onClick={() => void workflow.create()}
          >
            {running === "create" ? t("cityDevelopment.creating") : t("cityDevelopment.create")}
          </button>
        </div>
      </aside>
      <div className="city-development__map">
        <CityMapCanvas
          ref={mapRef}
          apiBaseUrl={apiBaseUrl}
          layers={developmentPattern ? DEVELOPMENT_LAYERS : STREET_LAYERS}
          interactive
          showCompass
          overlayVersion={overlayVersion}
          cursor="crosshair"
          {...mapProps}
        />
        {workflow.message && <div className="city-development__message" role="status">{workflow.message}</div>}
      </div>
    </div>
  );
}
