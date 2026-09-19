import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { fetchApi } from "../../utils/api";
import { sendEditPathShapeCommand } from "../../utils/ipc";
import type { PathShapeUpdatePayload, RouteProposalShape } from "./pathBuildConstants";
import { describePathValidation, type PathValidationResult } from "./pathValidation";

type Settings = {
  apiBaseUrl: string;
  payload: PathShapeUpdatePayload | null;
};

type RouteProposalResult = Pick<RouteProposalShape,
  "revision" | "points" | "controls_in" | "controls_out" | "embankments" | "generated">;

/** 応答を編集中の世代へ結び付ける。取消し後・別形状・別規格への応答は反映しない。 */
export function useRouteProposal(settings: Settings) {
  const { t } = useTranslation();
  const [heightMode, setHeightMode] = useState<"fixed" | "adjust">("fixed");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const request = useRef<AbortController | null>(null);
  const currentShape = useRef<RouteProposalShape>();
  const currentKey = useRef("");
  const shape = settings.payload?.route_proposal;
  const phase = settings.payload?.phase;
  currentShape.current = shape;
  const key = JSON.stringify(shape ? {
    api_base_url: settings.apiBaseUrl,
    revision: shape.revision,
    enabled: shape.enabled,
    height_editable: shape.height_editable,
    path_variant: shape.path_variant,
    follow_terrain: shape.follow_terrain,
    components: shape.components,
    platform_side: shape.platform_side,
    align_station_tracks: shape.align_station_tracks,
    exclude_path_id: shape.exclude_path_id,
  } : null);
  currentKey.current = key;

  useEffect(() => {
    request.current?.abort(); request.current = null; setBusy(false); setMessage("");
  }, [key, heightMode]);

  useEffect(() => () => request.current?.abort(), []);

  async function propose() {
    const start = currentShape.current;
    if (!start || phase !== "editing" || !start.enabled || !settings.apiBaseUrl || request.current) return;
    const controller = new AbortController();
    request.current = controller;
    setBusy(true); setMessage("");
    const mode = start.height_editable ? heightMode : "fixed";
    try {
      const response = await fetchApi<{
        status: string; revision: string; route?: RouteProposalResult;
        diagnostics?: PathValidationResult & { fixed_height_error?: number };
      }>(`${settings.apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/propose-route`, {
        method: "POST", headers: { "Content-Type": "application/json" }, signal: controller.signal,
        body: JSON.stringify({
          path_variant: start.path_variant,
          follow_terrain: start.follow_terrain,
          components: start.components,
          platform_side: start.platform_side,
          align_station_tracks: start.align_station_tracks,
          exclude_path_id: start.exclude_path_id,
          shape: start,
          height_mode: mode,
        }),
      });
      if (controller.signal.aborted || currentKey.current !== key || currentShape.current?.revision !== start.revision) return;
      if (response.status !== "ok" || !response.route) {
        const details = response.diagnostics?.alignments
          ? describePathValidation(response.diagnostics, t).join(" ") : "";
        setMessage(t(response.diagnostics?.fixed_height_error ? "route_proposal.heightFailure" : "route_proposal.notFound") + (details ? ` ${details}` : ""));
        return;
      }
      await sendEditPathShapeCommand({ op: "apply_route", revision: start.revision,
        route_json: JSON.stringify({ ...response.route, height_mode: mode }) });
    } catch (error) {
      if (!controller.signal.aborted) setMessage(t("route_proposal.failed", { reason: error instanceof Error ? error.message : String(error) }));
    } finally {
      if (request.current === controller) { request.current = null; setBusy(false); }
    }
  }

  return {
    busy, message, heightMode, setHeightMode, heightEditable: shape?.height_editable === true,
    visible: shape?.enabled === true, available: phase === "editing" && !!shape && !!settings.apiBaseUrl && !busy,
    canUndo: !!shape?.can_undo && !busy, propose,
    cancel: () => { request.current?.abort(); request.current = null; setBusy(false); },
    undo: () => shape && sendEditPathShapeCommand({ op: "undo_route", revision: shape.revision }),
  };
}
