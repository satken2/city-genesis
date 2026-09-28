import type { Phase } from "./model";

/** 保存・復旧・終了とその関連画面は、シナリオの許可リストに依存させない。 */
const unrestrictedWindows = new Set([
  "tutorial", "tutorial_intro", "notification", "tips", "system-tray", "settings", "credits",
  "save_load", "save_list", "new_game", "confirm_dialog",
]);

/** 各WebViewに同じポリシーを適用する。Reactの無効状態を上書きせず、解除時は自分のinertだけ戻す。 */
export function installUiGuide(ui: Phase["ui"], windowType: string): () => void {
  if (unrestrictedWindows.has(windowType)) return () => {};
  const highlighted = new Set<HTMLElement>(), blocked = new Set<HTMLElement>();
  // 同じ操作名の要素が並ぶもの(建設パネルのカードなど)は data-guide-value を持ち、「名前:値」で1つだけ指せる。
  const targetNames = (el: Element | null): string[] => {
    const target = el?.closest<HTMLElement>("[data-guide]");
    if (target) {
      const name = target.dataset.guide ?? "", value = target.dataset.guideValue;
      return value === undefined ? [name] : [name, `${name}:${value}`];
    }
    const id = el?.closest<HTMLElement>("button[id]")?.id;
    return id ? [`hud.${id}`] : [];
  };
  const listed = (list: string[], el: Element) => targetNames(el).some(name => list.includes(name));
  // アイコンパネルは各段階の操作の入口なので、方針に関わらず許可・強調したボタンだけを押せるようにする。
  const menu = windowType === "icon-panel";
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
      // 押せない要素(案内外のバリアントのカードなど)は強調しない。
      if (listed(ui.highlight, el) && !el.matches(":disabled")) { el.setAttribute("data-tutorial-highlight", ""); highlighted.add(el); }
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
  observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["data-guide", "data-guide-value", "id", "disabled"] });
  update();
  return () => { observer.disconnect(); events.forEach(e => document.removeEventListener(e, capture, true)); reset(); };
}
