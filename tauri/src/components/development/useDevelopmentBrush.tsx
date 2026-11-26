/**
 * ブラシの筆跡は塗り始めの半径(m)と点列のままコアに渡す。範囲の面はコアが作る。
 * 範囲はアルゴリズム間・タブ間で共有する。確定は現在の入力でプレビューが済んでから行う。
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit } from "@tauri-apps/api/event";

import { type CityMapViewState } from "../map/CityMapCanvas";
import { TagPicker } from "../common/TagPicker";
import { TAGS_CHANGED_EVENT } from "../../utils/constructionTags";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { describeError } from "../../utils/errors";
import { useMasterData } from "../../contexts/MasterDataContext";
import { formatArea } from "../../utils/formatArea";
import { resolveIntlLocale } from "../../utils/locale";
import { drawDevelopmentOverlay, type PreviewRoad, type Stroke } from "./developmentMapOverlay";

export const DEVELOPMENT_PATTERNS = [
  "grid_1", "grid_2", "sprawl_1", "sprawl_2", "sprawl_3", "planned_city_1", "split_1",
] as const;
export type DevelopmentPattern = (typeof DEVELOPMENT_PATTERNS)[number];

/** 道路の間隔(m)と区画の面積(m²)の既定値と範囲。範囲はコアの検証と同じ */
const DEFAULT_SPACING_M = 70;
const SPACING_MIN_M = 20;
const SPACING_MAX_M = 2000;
const DEFAULT_PARCEL_AREA_M2 = 600;
const PARCEL_AREA_MIN_M2 = 50;
const PARCEL_AREA_MAX_M2 = 1_000_000;

/** コアの plan_city_development の戻り値 */
type DevelopmentPlan = {
  roads: PreviewRoad[];
  parcels: [number, number][][];
  rejected: Record<string, number>;
  area_m2: number;
};

/** コアの create_city_development の戻り値 */
type DevelopmentResult = {
  path_ids: number[];
  parcel_ids: number[];
  rejected: Record<string, number>;
};

export function useDevelopmentBrush(
  pattern: DevelopmentPattern,
  {
    apiBaseUrl,
    variants,
    strokes,
    clearArea,
    reload,
  }: {
    apiBaseUrl: string;
    variants: number[];
    strokes: Stroke[];
    clearArea: () => void;
    reload: () => Promise<void>;
  },
) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  // 分割1だけは街区の長辺に平行な道路(道路の間隔)と直交する道路(街区の長さ)の間隔を別に指定する。
  const splitMode = pattern === "split_1";
  // 入力途中の空欄もそのまま表示し、送信できるかを別に判定する。
  const [spacingInput, setSpacingInput] = useState(String(DEFAULT_SPACING_M));
  const [blockLengthInput, setBlockLengthInput] = useState(String(DEFAULT_SPACING_M));
  const [createParcels, setCreateParcels] = useState(true);
  const [parcelAreaInput, setParcelAreaInput] = useState(String(DEFAULT_PARCEL_AREA_M2));
  const spacing = Number(spacingInput);
  const blockLength = Number(blockLengthInput);
  const parcelArea = Number(parcelAreaInput);
  const widestRoad = Math.max(0, ...variants.map((id) => masterData?.paths[id]?.width ?? 0));
  const minSpacing = Math.max(SPACING_MIN_M, widestRoad);
  const isSpacing = (value: number) => value >= minSpacing && value <= SPACING_MAX_M && value > widestRoad;
  const spacingValid = isSpacing(spacing);
  const blockLengthValid = !splitMode || isSpacing(blockLength);
  const parcelAreaValid = !createParcels || (parcelArea >= PARCEL_AREA_MIN_M2 && parcelArea <= PARCEL_AREA_MAX_M2);

  const [running, setRunning] = useState<"preview" | "create" | null>(null);
  const busy = running !== null;
  const [message, setMessage] = useState<string | null>(null);
  const [projectTag, setProjectTag] = useState("");
  const request = useMemo(
    () => ({
      pattern,
      path_variants: variants,
      spacing,
      block_length: splitMode ? blockLength : undefined,
      create_parcels: createParcels,
      parcel_area: createParcels ? parcelArea : undefined,
      strokes: strokes.map((s) => ({ radius: s.radius, points: s.points.map((p) => [p.x, p.z]) })),
    }),
    [pattern, variants, spacing, splitMode, blockLength, createParcels, parcelArea, strokes],
  );
  const [preview, setPreview] = useState<{ request: typeof request; plan: DevelopmentPlan } | null>(null);
  const plan = preview?.request === request ? preview.plan : null;
  useEffect(() => setMessage(null), [pattern]);

  const paintScratchRef = useRef<HTMLCanvasElement | null>(null);

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

  const postDevelopment = <T,>(path: string, extra: Record<string, unknown> = {}) =>
    fetchApi<T>(buildApiUrl(apiBaseUrl, path), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...request, ...extra }),
    });

  const withReasons = (text: string, rejected: Record<string, number>) => {
    const reasons = Object.entries(rejected)
      .filter(([, n]) => n > 0)
      .map(([key, n]) => `${t(`scenarioTools.reject.${key}`, { defaultValue: key })}: ${n}`)
      .join(" / ");
    return text + (reasons ? ` (${reasons})` : "");
  };
  const rejectedTotal = (rejected: Record<string, number>) => Object.values(rejected).reduce((a, b) => a + b, 0);

  async function previewDevelopment() {
    if (!canPreview) return;
    setRunning("preview");
    setMessage(null);
    setPreview(null);
    try {
      const result = await postDevelopment<DevelopmentPlan>("/api/v1/city-development/preview");
      setPreview({ request, plan: result });
    } catch (err) {
      setMessage(describeError(err, t));
    } finally {
      setRunning(null);
    }
  }

  async function createDevelopment() {
    if (!canCreate) return;
    setRunning("create");
    setMessage(null);
    try {
      const result = await postDevelopment<DevelopmentResult>(
        "/api/v1/city-development",
        projectTag ? { tag: projectTag } : {},
      );
      // 開いている建設プロジェクトの一覧とタグの選択肢を取り直させる。
      if (projectTag && result.path_ids.length > 0) {
        void emit(TAGS_CHANGED_EVENT, { tag: projectTag }).catch(console.error);
      }
      const rejected = rejectedTotal(result.rejected);
      const summary = t("cityDevelopment.created", {
        roads: result.path_ids.length,
        parcels: result.parcel_ids.length,
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

  const canPreview = strokes.length > 0 && variants.length > 0 && spacingValid && blockLengthValid && parcelAreaValid && !busy;
  const canCreate = canPreview && plan !== null && (plan.roads.length > 0 || plan.parcels.length > 0);
  const settings = (
    <>
      <label className="city-development__field">
        <span>{t("cityDevelopment.spacing")}</span>
        <input
          type="number"
          min={minSpacing}
          max={SPACING_MAX_M}
          step={10}
          value={spacingInput}
          aria-invalid={!spacingValid}
          disabled={busy}
          data-guide="city_development.spacing"
          onChange={(e) => setSpacingInput(e.currentTarget.value)}
        />
        <span className="city-development__unit">m</span>
      </label>
      {splitMode && (
        <label className="city-development__field">
          <span>{t("cityDevelopment.blockLength")}</span>
          <input
            type="number"
            min={minSpacing}
            max={SPACING_MAX_M}
            step={10}
            value={blockLengthInput}
            aria-invalid={!blockLengthValid}
            disabled={busy}
            data-guide="city_development.block_length"
            onChange={(e) => setBlockLengthInput(e.currentTarget.value)}
          />
          <span className="city-development__unit">m</span>
        </label>
      )}
      <label className="city-development__check">
        <input
          type="checkbox"
          checked={createParcels}
          disabled={busy}
          data-guide="city_development.create_parcels"
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
            data-guide="city_development.parcel_area"
            onChange={(e) => setParcelAreaInput(e.currentTarget.value)}
          />
          <span className="city-development__unit">m²</span>
        </label>
      )}
      {splitMode && <p className="city-development__note">{t("cityDevelopment.splitHint")}</p>}
      <div className="city-development__field">
        <span>{t("tag.label")}</span>
        <TagPicker apiBaseUrl={apiBaseUrl} value={projectTag} onChange={setProjectTag} disabled={busy} />
      </div>
    </>
  );

  let previewSummary: string | null = null;
  if (plan) {
    previewSummary = withReasons(
      t("cityDevelopment.previewed", {
        area: formatArea(plan.area_m2, resolveIntlLocale(i18n.language)),
        roads: plan.roads.length,
        parcels: plan.parcels.length,
        rejected: rejectedTotal(plan.rejected),
      }),
      plan.rejected,
    );
  }

  return {
    settings,
    running,
    hint: t("cityDevelopment.hint"),
    canPreview,
    canCreate,
    preview: previewDevelopment,
    create: createDevelopment,
    message: message ?? previewSummary,
    clearMessage,
    overlayDraw,
  };
}
