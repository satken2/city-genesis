import type { ComponentType } from "react";

/**
 * Content for shell-created window types unknown to App. The page entry point registers them before rendering
 * App (used by the browser development map page `web/debug-map/` to add a map replacing Unity, etc.). Product
 * pages register nothing, excluding these components from product builds.
 */
export type ExtraWindowComponent = ComponentType<{ apiBaseUrl: string }>;

const extraWindows = new Map<string, ExtraWindowComponent>();

export function registerExtraWindow(label: string, component: ExtraWindowComponent): void {
  extraWindows.set(label, component);
}

export function extraWindow(label: string): ExtraWindowComponent | undefined {
  return extraWindows.get(label);
}
