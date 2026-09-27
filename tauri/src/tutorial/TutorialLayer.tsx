import { useEffect, useState } from "react";
import { emit, listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { acceptSnapshot, GUIDE_QUERY, GUIDE_STATE, observe, snapshot } from "./bus";
import type { Snapshot } from "./model";
import "./TutorialLayer.css";
import { installUiGuide } from "./dom";

export function useGuideSnapshot() {
  const [state, setState] = useState<Snapshot | null>(snapshot);
  useEffect(() => {
    let stop: (() => void) | undefined, disposed = false;
    void listen<Snapshot>(GUIDE_STATE, e => setState(acceptSnapshot(e.payload))).then(fn => { if (disposed) fn(); else { stop = fn; void emit(GUIDE_QUERY, { request: true }); } });
    return () => { disposed = true; stop?.(); };
  }, []);
  return state;
}

export function TutorialLayer({ windowType, entity }: { windowType: string; entity?: string | null }) {
  const state = useGuideSnapshot();
  useEffect(() => {
    const win = getCurrentWindow();
    let disposed = false;
    const stops: (() => void)[] = [];
    const report = async () => {
      const visible = await win.isVisible();
      if (!disposed) await observe({ label: win.label, window: windowType, visible, entity: entity ? Number(entity) : undefined });
    };
    const keep = (fn: () => void) => { if (disposed) fn(); else stops.push(fn); };
    void listen(GUIDE_QUERY, () => void report()).then(keep);
    void listen("popup-count-changed", () => void report()).then(keep);
    void win.onFocusChanged(() => void report()).then(keep);
    void report();
    return () => { disposed = true; stops.forEach(fn => fn()); };
  }, [windowType, entity, state?.runId, state?.revision]);

  useEffect(() => {
    if (!state?.active || ["tutorial", "notification", "tips"].includes(windowType)) return;
    return installUiGuide(state.ui, windowType);
  }, [state, windowType]);
  return null;
}

export function tutorialVariantAllowed(id: number): boolean {
  const state = snapshot();
  return !state?.active || !state.world.path || state.world.path.variant === id;
}
