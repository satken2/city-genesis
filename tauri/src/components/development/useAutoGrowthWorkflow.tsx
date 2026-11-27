/**
 * 「都市の自動拡張」タブのワークフロー。
 * ブラシで塗った範囲内で既存道路網から最適な拡張ポイントを自動的に探し、指定した路地形式・
 * 道路規格・街区数で幹線と沿道街区をプレビュー・確定する。
 */
import { useCallback, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit } from "@tauri-apps/api/event";

import { useMasterData } from "../../contexts/MasterDataContext";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { TAGS_CHANGED_EVENT } from "../../utils/constructionTags";
import { describeError } from "../../utils/errors";
import { resolveIntlLocale } from "../../utils/locale";
import { formatExactAmount } from "../../utils/money";
import { TagPicker } from "../common/TagPicker";
import type { CityMapViewState } from "../map/CityMapCanvas";
import type { PathVariantOption } from "../path/pathBuildCategories";
import { drawDevelopmentOverlay, type PreviewRoad, type Stroke } from "./developmentMapOverlay";

export const GROWTH_STYLES = [
  "blocks",
  "ribbon",
  "sprawl_1",
  "sprawl_2",
  "sprawl_3",
] as const;
export type GrowthStyle = (typeof GROWTH_STYLES)[number];

const DEFAULT_BLOCKS = 8;
const BLOCKS_MIN = 1;
const BLOCKS_MAX = 64;
const DEFAULT_PARCEL_AREA_M2 = 400;
const PARCEL_AREA_MIN_M2 = 50;
const PARCEL_AREA_MAX_M2 = 1_000_000;

type GrowthPlan = {
  roads: PreviewRoad[];
  parcels: [number, number][][];
  rejected: Record<string, number>;
  blocks_added: number;
  area_m2: number;
  estimated_cost: number;
  bounds: [number, number, number, number] | null;
};

type GrowthResult = {
  path_ids: number[];
  parcels: number; // 道路の竣工時にできる区画の件数
  rejected: Record<string, number>;
  blocks_added: number;
};

export function useAutoGrowthWorkflow({
  apiBaseUrl,
  choices,
  strokes,
  clearArea,
  reload,
}: {
  apiBaseUrl: string;
  choices: PathVariantOption[];
  strokes: Stroke[];
  clearArea: () => void;
  reload: () => Promise<void>;
}) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const locale = resolveIntlLocale(i18n.language);
  const paintScratchRef = useRef<HTMLCanvasElement | null>(null);

  const [style, setStyle] = useState<GrowthStyle>("blocks");
  const [arterialVariant, setArterialVariant] = useState<number | null>(null);
  const [laneVariant, setLaneVariant] = useState<number | null>(null);
  const [blocksInput, setBlocksInput] = useState(String(DEFAULT_BLOCKS));
  const [createParcels, setCreateParcels] = useState(true);
  const [parcelAreaInput, setParcelAreaInput] = useState(String(DEFAULT_PARCEL_AREA_M2));
  const [projectTag, setProjectTag] = useState("");
  const [running, setRunning] = useState<"preview" | "create" | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const busy = running !== null;
  const availableIds = useMemo(() => new Set(choices.map((c) => c.id)), [choices]);
  const effectiveArterial = arterialVariant !== null && availableIds.has(arterialVariant) ? arterialVariant : null;
  const effectiveLane = laneVariant !== null && availableIds.has(laneVariant) ? laneVariant : null;

  const blocks = Number(blocksInput);
  const blocksValid = Number.isInteger(blocks) && blocks >= BLOCKS_MIN && blocks <= BLOCKS_MAX;
  const parcelArea = Number(parcelAreaInput);
  const parcelAreaValid = !createParcels || (parcelArea >= PARCEL_AREA_MIN_M2 && parcelArea <= PARCEL_AREA_MAX_M2);

  const request = useMemo(
    () => ({
      style,
      blocks,
      arterial_variant: effectiveArterial,
      lane_variant: effectiveLane,
      create_parcels: createParcels,
      parcel_area: createParcels ? parcelArea : undefined,
      strokes: strokes.map((s) => ({ radius: s.radius, points: s.points.map((p) => [p.x, p.z]) })),
    }),
    [style, blocks, effectiveArterial, effectiveLane, createParcels, parcelArea, strokes],
  );

  const [preview, setPreview] = useState<{ request: typeof request; plan: GrowthPlan } | null>(null);
  const plan = preview?.request === request ? preview.plan : null;

  const overlayDraw = useCallback(
    (ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
      paintScratchRef.current ??= document.createElement("canvas");
      drawDevelopmentOverlay(ctx, view, {
        strokes,
        scratchCanvas: paintScratchRef.current,
        plan,
        masterData,
      });
    },
    [strokes, plan, masterData],
  );

  const postGrowth = <T,>(path: string, extra: Record<string, unknown> = {}) =>
    fetchApi<T>(buildApiUrl(apiBaseUrl, path), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...request, ...extra }),
    });

  const rejectedTotal = (rejected: Record<string, number>) => Object.values(rejected).reduce((a, b) => a + b, 0);
  const withReasons = (text: string, rejected: Record<string, number>) => {
    const reasons = Object.entries(rejected)
      .filter(([, n]) => n > 0)
      .map(([key, n]) => `${t(`scenarioTools.reject.${key}`, { defaultValue: key })}: ${n}`)
      .join(" / ");
    return text + (reasons ? ` (${reasons})` : "");
  };

  const canPreview = strokes.length > 0 && choices.length > 0 && blocksValid && parcelAreaValid && !busy;
  const canCreate = canPreview && plan !== null && (plan.roads.length > 0 || plan.parcels.length > 0);

  async function runPreview() {
    if (!canPreview) return;
    setRunning("preview");
    setMessage(null);
    setPreview(null);
    try {
      const result = await postGrowth<GrowthPlan>("/api/v1/city-development/auto-growth/preview");
      setPreview({ request, plan: result });
    } catch (err) {
      setMessage(describeError(err, t));
    } finally {
      setRunning(null);
    }
  }

  async function runCreate() {
    if (!canCreate) return;
    setRunning("create");
    setMessage(null);
    try {
      const result = await postGrowth<GrowthResult>(
        "/api/v1/city-development/auto-growth",
        projectTag ? { tag: projectTag } : {},
      );
      if (projectTag && result.path_ids.length > 0) {
        void emit(TAGS_CHANGED_EVENT, { tag: projectTag }).catch(console.error);
      }
      const rejected = rejectedTotal(result.rejected);
      const summary = t("cityDevelopment.autoGrowth.created", {
        blocks: result.blocks_added,
        roads: result.path_ids.length,
        parcels: result.parcels,
        rejected,
      });
      setMessage(withReasons(summary, result.rejected));
      clearArea();
      await reload();
    } catch (err) {
      setMessage(describeError(err, t));
    } finally {
      setRunning(null);
    }
  }

  const clearMessage = useCallback(() => setMessage(null), []);

  let previewSummary: string | null = null;
  if (plan) {
    if (plan.roads.length === 0 && plan.parcels.length === 0) {
      previewSummary = t("cityDevelopment.autoGrowth.noCandidate");
    } else {
      previewSummary = withReasons(
        t("cityDevelopment.autoGrowth.previewed", {
          blocks: plan.blocks_added,
          roads: plan.roads.length,
          parcels: plan.parcels.length,
          cost: formatExactAmount(Math.round(plan.estimated_cost), locale, true, { maximumFractionDigits: 0 }),
          rejected: rejectedTotal(plan.rejected),
        }),
        plan.rejected,
      );
    }
  }

  const settings = (
    <>
      <label className="city-development__field">
        <span>{t("cityDevelopment.autoGrowth.style")}</span>
        <select
          value={style}
          disabled={busy}
          data-guide="city_development.growth_style"
          onChange={(e) => setStyle(e.currentTarget.value as GrowthStyle)}
        >
          {GROWTH_STYLES.map((key) => (
            <option key={key} value={key}>
              {t(`cityDevelopment.autoGrowth.styles.${key}`)}
            </option>
          ))}
        </select>
      </label>

      <label className="city-development__field">
        <span>{t("cityDevelopment.autoGrowth.arterialVariant")}</span>
        <select
          value={effectiveArterial === null ? "" : String(effectiveArterial)}
          disabled={busy}
          data-guide="city_development.arterial_variant"
          onChange={(e) => setArterialVariant(e.currentTarget.value ? Number(e.currentTarget.value) : null)}
        >
          <option value="">{t("cityDevelopment.autoGrowth.variantAuto")}</option>
          {choices.map(({ id, data }) => (
            <option key={id} value={String(id)}>
              {data.display_name}
            </option>
          ))}
        </select>
      </label>

      <label className="city-development__field">
        <span>{t("cityDevelopment.autoGrowth.laneVariant")}</span>
        <select
          value={effectiveLane === null ? "" : String(effectiveLane)}
          disabled={busy}
          data-guide="city_development.lane_variant"
          onChange={(e) => setLaneVariant(e.currentTarget.value ? Number(e.currentTarget.value) : null)}
        >
          <option value="">{t("cityDevelopment.autoGrowth.variantAuto")}</option>
          {choices.map(({ id, data }) => (
            <option key={id} value={String(id)}>
              {data.display_name}
            </option>
          ))}
        </select>
      </label>

      <label className="city-development__field">
        <span>{t("cityDevelopment.autoGrowth.blocks")}</span>
        <input
          type="number"
          min={BLOCKS_MIN}
          max={BLOCKS_MAX}
          step={1}
          value={blocksInput}
          aria-invalid={!blocksValid}
          disabled={busy}
          data-guide="city_development.blocks"
          onChange={(e) => setBlocksInput(e.currentTarget.value)}
        />
        <span className="city-development__unit">{t("cityDevelopment.autoGrowth.blocksUnit")}</span>
      </label>

      <label className="city-development__check">
        <input
          type="checkbox"
          checked={createParcels}
          disabled={busy}
          data-guide="city_development.growth_create_parcels"
          onChange={(e) => setCreateParcels(e.currentTarget.checked)}
        />
        {t("cityDevelopment.createParcels")}
      </label>

      {createParcels && (
        <label className="city-development__field">
          <span>{t("cityDevelopment.parcelArea")}</span>
          <input
            type="number"
            min={PARCEL_AREA_MIN_M2}
            max={PARCEL_AREA_MAX_M2}
            step={50}
            value={parcelAreaInput}
            aria-invalid={!parcelAreaValid}
            disabled={busy}
            data-guide="city_development.growth_parcel_area"
            onChange={(e) => setParcelAreaInput(e.currentTarget.value)}
          />
          <span className="city-development__unit">m²</span>
        </label>
      )}

      <div className="city-development__field">
        <span>{t("tag.label")}</span>
        <TagPicker apiBaseUrl={apiBaseUrl} value={projectTag} onChange={setProjectTag} disabled={busy} />
      </div>
    </>
  );

  return {
    settings,
    running,
    hint: t("cityDevelopment.autoGrowth.hint"),
    canPreview,
    canCreate,
    preview: runPreview,
    create: runCreate,
    message: message ?? previewSummary,
    clearMessage,
    overlayDraw,
  };
}
