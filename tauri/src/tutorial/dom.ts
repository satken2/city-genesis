import type { Phase } from "./model";

/** 各WebViewに同じポリシーを適用する。Reactの無効状態を上書きせず、解除時は自分のinertだけ戻す。 */
export function installUiGuide(ui: Phase["ui"], windowType: string): () => void {
  const highlighted = new Set<HTMLElement>(), blocked = new Set<HTMLElement>();
  const targetName = (el: Element | null) => {
    const target = el?.closest<HTMLElement>("[data-guide]");
    if (target) return target.dataset.guide;
    const id = el?.closest<HTMLElement>("button[id]")?.id;
    return id ? `hud.${id}` : undefined;
  };
  const allowed = (el: Element) => {
    if (el.closest(".titlebar, [data-guide-escape]")) return true;
    if (ui.policy === "highlight") return true;
    if (ui.policy === "related" && ui.allow.includes(`window.${windowType}`)) return true;
    const name = targetName(el);
    return !!name && ui.allow.includes(name);
  };
  const reset = () => {
    highlighted.forEach(el => el.removeAttribute("data-tutorial-highlight")); highlighted.clear();
    blocked.forEach(el => { el.removeAttribute("data-tutorial-blocked"); el.inert = false; }); blocked.clear();
  };
  const update = () => {
    reset();
    document.querySelectorAll<HTMLElement>("button, input, select, textarea, a, [role=button], [role=tab], [data-guide]").forEach(el => {
      if (ui.highlight.includes(targetName(el) ?? "")) { el.setAttribute("data-tutorial-highlight", ""); highlighted.add(el); }
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
  observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["data-guide", "id"] });
  update();
  return () => { observer.disconnect(); events.forEach(e => document.removeEventListener(e, capture, true)); reset(); };
}
