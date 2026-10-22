import type { Phase } from "./model";

/** Keep saving, recovery, exit, and related screens independent of the scenario allowlist. */
const unrestrictedWindows = new Set([
  "tutorial", "info_dialog", "notification", "tips", "system-tray", "settings", "credits",
  "save_load", "save_list", "new_game", "confirm_dialog",
]);

/** Apply the same policy to each WebView. Do not override React disabled state; on release, restore only inert set by this code. */
export function installUiGuide(ui: Phase["ui"], windowType: string): () => void {
  if (unrestrictedWindows.has(windowType)) return () => {};
  const highlighted = new Set<HTMLElement>(), blocked = new Set<HTMLElement>();
  // Elements sharing an action name (such as construction cards) carry data-guide-value; `name:value` selects one.
  const targetNames = (el: Element | null): string[] => {
    const target = el?.closest<HTMLElement>("[data-guide]");
    if (target) {
      const name = target.dataset.guide ?? "", value = target.dataset.guideValue;
      const values = target.dataset.guideValues?.split(/\s+/).filter(Boolean) ?? [];
      if (value !== undefined) values.push(value);
      return [name, ...values.map(value => `${name}:${value}`)];
    }
    const id = el?.closest<HTMLElement>("button[id]")?.id;
    return id ? [`hud.${id}`] : [];
  };
  const listed = (list: string[], el: Element) => targetNames(el).some(name => list.includes(name));
  // Icon panel and cockpit are entry points for each stage (including Start to advance time), so only allow
  // permitted/highlighted buttons regardless of policy. This prevents leaving the guide even during explanation-only stages.
  const menu = windowType === "icon-panel" || windowType === "cockpit";
  const allowed = (el: Element) => {
    if (el.closest(".titlebar, [data-guide-escape]")) return true;
    if (menu) return listed(ui.allow, el) || listed(ui.highlight, el);
    if (ui.policy === "highlight") return true;
    if (ui.policy === "related" && ui.allow.includes(`window.${windowType}`)) return true;
    return listed(ui.allow, el);
  };
  const reset = () => {
    highlighted.forEach(el => el.removeAttribute("data-tutorial-highlight")); highlighted.clear();
    blocked.forEach(el => { el.removeAttribute("data-tutorial-blocked"); el.inert = false; }); blocked.clear();
  };
  const update = () => {
    reset();
    document.querySelectorAll<HTMLElement>("button, input, select, textarea, a, [role=button], [role=tab], [data-guide]").forEach(el => {
      // Do not highlight disabled elements (such as unrelated variant cards). For containers with data-guide
      // (such as fare inputs), highlight only the container without duplicating borders on child buttons/inputs.
      if (listed(ui.highlight, el) && !el.matches(":disabled") && (el.hasAttribute("data-guide") || !el.parentElement?.closest("[data-guide]"))) { el.setAttribute("data-tutorial-highlight", ""); highlighted.add(el); }
      if (!allowed(el) && !el.inert) { el.inert = true; el.setAttribute("data-tutorial-blocked", ""); blocked.add(el); }
    });
  };
  const capture = (event: Event) => {
    if (event instanceof KeyboardEvent && ["Tab", "Escape", "PageDown", "PageUp"].includes(event.key)) return;
    if (event.target instanceof Element && !allowed(event.target)) { event.preventDefault(); event.stopImmediatePropagation(); }
  };
  const events = ["pointerdown", "mousedown", "click", "dblclick", "keydown", "submit", "change"];
  events.forEach(e => document.addEventListener(e, capture, true));
  const observer = new MutationObserver(update);
  observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["data-guide", "data-guide-value", "data-guide-values", "id", "disabled"] });
  update();
  return () => { observer.disconnect(); events.forEach(e => document.removeEventListener(e, capture, true)); reset(); };
}
