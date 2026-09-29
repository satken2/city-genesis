/**
 * シナリオ管理の「地形を取り込む」の右側に出すプレビュー。
 *
 * 選んだ標高ファイルはコアの heightmap-preview で取り込みと同じ読み方の標高にして受け取り、
 * 詳細地形は標高で色分け・背景地形は淡い単色で重ねて描く(画素は `heightmapPreview.ts`)。
 * 入力欄の値が変わるたびに描き直すが、標高そのものを取り直すのはファイル・海面の高さ・PNGの標高範囲が変わったときだけ。
 */
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { saveError } from "../../utils/saves";
import { SEA_RGB } from "../terrain/terrainData";
import { fetchHeightmapPreview, type HeightmapFormat, type HeightmapPreviewGrid } from "./heightmapImport";
import { renderImportPreview, type PreviewLayer } from "./heightmapPreview";
import "./Scenarios.css";

const PREVIEW_LONG_EDGE = 768;
/** 入力欄を打っている間に取り直しを連発しない待ち時間(ms) */
const REFETCH_DELAY = 250;

export type HeightmapPreviewState = { grid: HeightmapPreviewGrid | null; loading: boolean; error: string };

/**
 * 選んだファイルの間引いた標高を取得する。reading が null(入力値が不正)の間は同じファイルの前の結果を残し、
 * ファイルが無ければ空に戻す。別のファイルに替えたら前のファイルの結果は返さない。新しい取得を始めたら古い取得は捨てる。
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

/** 詳細地形と背景地形の層を重ねて描く。海(標高0m以下)は透明なので、下地は海の色にする。読めなかったファイルの理由は入力欄の側に出す */
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
