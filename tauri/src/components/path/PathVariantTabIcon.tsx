import type { PathVariantTab } from "./pathBuildCategories";

/** 建設一覧の分類を表す自作の図形。色と寸法はタブのスタイルに追従する。 */
export function PathVariantTabIcon({ icon }: { icon: PathVariantTab["icon"] }) {
  return (
    <svg className="pc-variant-tab-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {icon === "track" && <>
        <path d="M8 3 5 21 M16 3 19 21 M7.5 6h9 M7 10h10 M6.3 15h11.4 M5.5 20h13" />
      </>}
      {icon === "passenger" && <>
        <circle cx="9" cy="5" r="2" />
        <path d="M5 15v-4a4 4 0 0 1 8 0v4 M7 12v9 M11 12v9 M16 5h5v13h-5 M15 21h7" />
      </>}
      {icon === "freight" && <>
        <path d="m12 3 8 4.5v9L12 21l-8-4.5v-9L12 3Z M4 7.5l8 4.5 8-4.5 M12 12v9 M8 5.3l8 4.5v4" />
      </>}
    </svg>
  );
}
