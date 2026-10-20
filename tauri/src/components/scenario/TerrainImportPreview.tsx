/**
 * Right-side preview for Import Terrain in scenario management.
 *
 * Read selected files through the core's heightmap-preview with the same elevation interpretation as import.
 * Color detailed terrain by elevation and overlay background terrain in a pale solid color (pixels from
 * `heightmapPreview.ts`).
 * Redraw when inputs change, but refetch elevations only for file, sea level, or PNG elevation range changes.
 */
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { saveError } from "../../utils/saves";
import { SEA_RGB } from "../terrain/terrainData";
import { fetchHeightmapPreview, type HeightmapFormat, type HeightmapPreviewGrid } from "./heightmapImport";
import { renderImportPreview, type PreviewLayer } from "./heightmapPreview";
import "./Scenarios.css";

const PREVIEW_LONG_EDGE = 768;
/** Debounce interval in milliseconds to avoid repeated fetches while typing. */
const REFETCH_DELAY = 250;

export type HeightmapPreviewState = { grid: HeightmapPreviewGrid | null; loading: boolean; error: string };

/**
 * Retrieve downsampled elevations for the selected file. While reading is null (invalid inputs), retain that
 * file's previous result; clear if no file exists.
 * Never return the old file's result after switching files. Discard previous retrievals when a new one starts.
 */
export function useHeightmapPreview(
  apiBaseUrl: string, file: File | null,
  reading: { minHeight: number; maxHeight: number; seaLevel: number } | null,
  options: { format: HeightmapFormat | null; background?: boolean },
): HeightmapPreviewState {
  const { t } = useTranslation();
  const [state, setState] = useState<HeightmapPreviewState & { file: File | null }>({ file: null, grid: null, loading: false, error: "" });
  const { format, background } = options;
  const minHeight = reading?.minHeight, maxHeight = reading?.maxHeight, seaLevel = reading?.seaLevel;
  const active = reading !== null;
  useEffect(() => {
    if (!file) { setState({ file: null, grid: null, loading: false, error: "" }); return; }
    if (!active) return;
    const controller = new AbortController();
    setState(previous => ({ ...previous, loading: true, error: "" }));
    const timer = window.setTimeout(() => {
      fetchHeightmapPreview(apiBaseUrl, file, { minHeight: minHeight ?? 0, maxHeight: maxHeight ?? 0, seaLevel: seaLevel ?? 0 },
        { format, background, signal: controller.signal })
        .then(grid => setState({ file, grid, loading: false, error: "" }))
        .catch((e: unknown) => {
          if (controller.signal.aborted) return;
          console.error("[TerrainImportPreview] failed to read the file", e);
          setState({ file, grid: null, loading: false, error: saveError(e, t) });
        });
    }, REFETCH_DELAY);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [apiBaseUrl, file, active, minHeight, maxHeight, seaLevel, format, background, t]);
  return { grid: state.file === file ? state.grid : null, loading: state.loading, error: state.error };
}

/** Draw detailed/background terrain layers together. Sea (elevation <= 0m) is transparent, so use a sea-colored base. Show unreadable-file reasons beside the inputs. */
export function TerrainImportPreview({ detail, background, loading }: {
  detail: PreviewLayer | null; background: PreviewLayer | null; loading: boolean;
}) {
  const { t } = useTranslation();
  const canvas = useRef<HTMLCanvasElement>(null);
  const image = useMemo(() => {
    try { return renderImportPreview({ detail, background }, PREVIEW_LONG_EDGE); } catch (e) {
      console.error("[TerrainImportPreview] failed to draw the preview", e);
      return null;
    }
  }, [detail, background]);
  useEffect(() => {
    const element = canvas.current;
    const context = element?.getContext("2d");
    if (!element || !context || !image) return;
    element.width = image.width;
    element.height = image.height;
    context.putImageData(new ImageData(image.pixels, image.width, image.height), 0, 0);
  }, [image]);

  return <div className="scenario-import-preview">
    <div className="scenario-import-preview-stage" style={{ background: `rgb(${SEA_RGB.join(",")})` }}>
      {image
        ? <canvas ref={canvas} className="scenario-import-preview-canvas" />
        : <p className="scenario-hint">{loading ? t("scenarios.previewLoading") : t("scenarios.previewEmpty")}</p>}
    </div>
  </div>;
}
