import { useEffect, useRef, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useTranslation } from "react-i18next";
import { fetchApi } from "../../utils/api";
import { IPC_EVENT_EDIT_PATH_UPDATE_SHAPE, sendEditPathShapeCommand } from "../../utils/ipc";
import type { CombinedPathComponent } from "../../types";
import type { PathShapeUpdatePayload, RouteProposalShape } from "./pathBuildConstants";
import { describePathValidation, type PathValidationResult } from "./pathValidation";

type Settings = {
  apiBaseUrl?: string;
  path_variant: number | null;
  follow_terrain: number;
  components: CombinedPathComponent[] | null;
  platform_side: number;
  align_station_tracks: boolean;
  exclude_path_id: number | null;
  enabled: boolean;
  heightEditable: boolean;
};

/** 応答を編集中の世代へ結び付ける。取消し後・別形状・別規格への応答は反映しない。 */
export function useRouteProposal(settings: Settings) {
  const { t } = useTranslation();
  const [shape, setShape] = useState<RouteProposalShape>();
  const [phase, setPhase] = useState<string>();
  const [heightMode, setHeightMode] = useState<"fixed" | "adjust">("fixed");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const request = useRef<AbortController | null>(null);
  const currentShape = useRef<RouteProposalShape>();
  const currentKey = useRef("");
  const key = JSON.stringify(settings);
  currentKey.current = key;

  useEffect(() => {
    let disposed = false;
    let unlisten: (() => void) | undefined;
    void getCurrentWindow().listen<PathShapeUpdatePayload>(IPC_EVENT_EDIT_PATH_UPDATE_SHAPE, ({ payload }) => {
      if (currentShape.current?.revision !== payload.route_proposal?.revision) {
        request.current?.abort();
        request.current = null;
        setBusy(false);
        setMessage("");
      }
      currentShape.current = payload.route_proposal;
      setShape(payload.route_proposal);
      setPhase(payload.phase);
    }).then((fn) => { if (disposed) fn(); else unlisten = fn; }).catch(console.error);
    return () => { disposed = true; unlisten?.(); request.current?.abort(); };
  }, []);

  useEffect(() => {
    request.current?.abort(); request.current = null; setBusy(false); setMessage("");
  }, [key, heightMode]);

  async function propose() {
    const start = currentShape.current;
    if (!start || phase !== "editing" || !settings.enabled || !settings.apiBaseUrl || request.current) return;
    const controller = new AbortController();
    request.current = controller;
    setBusy(true); setMessage("");
    const mode = settings.heightEditable ? heightMode : "fixed";
    try {
      const response = await fetchApi<{
        status: string; revision: string; route?: RouteProposalShape;
        diagnostics?: PathValidationResult & { fixed_height_error?: number };
      }>(`${settings.apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/propose-route`, {
        method: "POST", headers: { "Content-Type": "application/json" }, signal: controller.signal,
        body: JSON.stringify({ ...settings, shape: start, height_mode: mode }),
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
    busy, message, heightMode, setHeightMode, heightEditable: settings.heightEditable,
    visible: settings.enabled, available: phase === "editing" && !!shape && !!settings.apiBaseUrl && !busy,
    canUndo: !!shape?.can_undo && !busy, propose,
    cancel: () => { request.current?.abort(); request.current = null; setBusy(false); },
    undo: () => shape && sendEditPathShapeCommand({ op: "undo_route", revision: shape.revision }),
  };
}
