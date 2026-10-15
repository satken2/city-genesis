import type { PopupMenuRequest } from "../shared/shellApi";
import "./popupMenu.css";

let active: { label: string; id: string; close: () => void } | undefined;

export function closePopupMenu(label?: string, id?: string): void {
  if ((!label || active?.label === label) && (!id || active?.id === id)) active?.close();
}

/** OS のメニューと同じくページ全体の前面へ出し、画面端では候補が収まる側へ配置する。 */
export function showPopupMenu(label: string, request: PopupMenuRequest, onSelect: (id: string) => void, onClose: () => void): void {
  closePopupMenu();
  const menu = document.createElement("dialog");
  menu.className = "shell-popup-menu";
  menu.setAttribute("role", "menu");
  menu.style.fontSize = `${request.fontSize}px`;
  const close = () => {
    active = undefined;
    window.removeEventListener("resize", close);
    window.removeEventListener("blur", close);
    menu.close();
    menu.remove();
    onClose();
  };
  active = { label, id: request.id, close };
  for (const item of request.items) {
    const button = document.createElement("button");
    button.type = "button";
    button.disabled = item.enabled === false;
    button.setAttribute("role", "menuitemcheckbox");
    button.setAttribute("aria-checked", String(item.checked === true));
    const text = document.createElement("span");
    text.textContent = item.text;
    button.appendChild(text);
    button.addEventListener("click", () => { close(); onSelect(item.id); });
    menu.appendChild(button);
  }
  menu.addEventListener("cancel", event => { event.preventDefault(); close(); });
  menu.addEventListener("pointerdown", event => {
    const rect = menu.getBoundingClientRect();
    if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) close();
  });
  menu.addEventListener("keydown", event => {
    const buttons = Array.from(menu.querySelectorAll<HTMLButtonElement>("button:not(:disabled)"));
    const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
    let next: number;
    switch (event.key) {
      case "ArrowDown": next = (index + 1) % buttons.length; break;
      case "ArrowUp": next = (index - 1 + buttons.length) % buttons.length; break;
      case "Home": next = 0; break;
      case "End": next = buttons.length - 1; break;
      case "Tab": event.preventDefault(); close(); return;
      default: return;
    }
    event.preventDefault();
    buttons[next]?.focus();
  });
  document.body.appendChild(menu);
  menu.showModal();
  const { width, height } = menu.getBoundingClientRect();
  menu.style.left = `${Math.max(8, Math.min(request.at.x, window.innerWidth - width - 8))}px`;
  const y = request.at.y + height <= window.innerHeight - 8 ? request.at.y : request.at.y - height;
  menu.style.top = `${Math.max(8, Math.min(y, window.innerHeight - height - 8))}px`;
  (menu.querySelector<HTMLButtonElement>('button[aria-checked="true"]:not(:disabled)')
    ?? menu.querySelector<HTMLButtonElement>("button:not(:disabled)"))?.focus();
  window.addEventListener("resize", close);
  window.addEventListener("blur", close);
}
