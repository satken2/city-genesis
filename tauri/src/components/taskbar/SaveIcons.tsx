/**
 * セーブメニュー・セーブ一覧・エポック実行の確認と結果の線画アイコン(自作のSVG)。
 * 24×24 の座標で描き、線の色は文字色(currentColor)に従う。大きさはCSSで決める。
 */
import type { ReactNode } from "react";
import type { Season } from "../../utils/season";

type IconProps = { className?: string };

function Icon({ className, children }: IconProps & { children: ReactNode }) {
  return (
    <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.8}
      strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
      {children}
    </svg>
  );
}

export function SaveIcon(props: IconProps) {
  return <Icon {...props}>
    <path d="M4 5a2 2 0 0 1 2-2h10l4 4v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z" />
    <path d="M8 3v5h7V3" />
    <path d="M7 21v-7h10v7" />
  </Icon>;
}

export function SaveAsIcon(props: IconProps) {
  return <Icon {...props}>
    <path d="M10.5 21H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h10l4 4v3.5" />
    <path d="M8 3v5h7V3" />
    <path d="M13 21v-2.6l5.4-5.4 2.6 2.6-5.4 5.4z" />
  </Icon>;
}

export function ScenarioIcon(props: IconProps) {
  return <Icon {...props}>
    <path d="M3 6.5 9 4l6 2.5L21 4v13.5L15 20l-6-2.5L3 20z" />
    <path d="M9 4v13.5" />
    <path d="M15 6.5V20" />
  </Icon>;
}

export function LoadIcon(props: IconProps) {
  return <Icon {...props}>
    <path d="M3.5 18.5V6a1.5 1.5 0 0 1 1.5-1.5h4.2l2 2H18a1.5 1.5 0 0 1 1.5 1.5v2.5" />
    <path d="M3.5 18.5 6.4 11.9a1.5 1.5 0 0 1 1.4-.9h13.1a1 1 0 0 1 .9 1.4l-2.6 6.2a1.5 1.5 0 0 1-1.4.9H4.5a1 1 0 0 1-1-1z" />
  </Icon>;
}

export function NewGameIcon(props: IconProps) {
  return <Icon {...props}>
    <path d="M11 3.5l1.9 5.1 5.1 1.9-5.1 1.9-1.9 5.1-1.9-5.1-5.1-1.9 5.1-1.9z" />
    <path d="M18.5 14.5l.8 2 2 .8-2 .8-.8 2-.8-2-2-.8 2-.8z" />
  </Icon>;
}

export function HomeIcon(props: IconProps) {
  return <Icon {...props}>
    <path d="M3.5 11 12 4l8.5 7" />
    <path d="M6 9.5V20h4.5v-5.5h3V20H18V9.5" />
  </Icon>;
}

export function PowerIcon(props: IconProps) {
  return <Icon {...props}>
    <path d="M12 3.5v8" />
    <path d="M6.6 6.8a7.5 7.5 0 1 0 10.8 0" />
  </Icon>;
}

export function BackIcon(props: IconProps) {
  return <Icon {...props}>
    <path d="M19.5 12h-15" />
    <path d="M10.5 6l-6 6 6 6" />
  </Icon>;
}

export function TrashIcon(props: IconProps) {
  return <Icon {...props}>
    <path d="M4 7h16" />
    <path d="M9 7V4.5h6V7" />
    <path d="M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12" />
    <path d="M10 11v6M14 11v6" />
  </Icon>;
}

export function PopulationIcon(props: IconProps) {
  return <Icon {...props}>
    <circle cx="9" cy="8" r="3" />
    <path d="M3.5 20a5.5 5.5 0 0 1 11 0" />
    <path d="M15.5 5.2a3 3 0 0 1 0 5.6" />
    <path d="M17.5 14.3A5.5 5.5 0 0 1 20.5 20" />
  </Icon>;
}

export function CalendarIcon(props: IconProps) {
  return <Icon {...props}>
    <rect x="4" y="5" width="16" height="15" rx="2" />
    <path d="M4 10h16M8.5 3v4M15.5 3v4" />
  </Icon>;
}

export function ClockIcon(props: IconProps) {
  return <Icon {...props}>
    <circle cx="12" cy="12" r="8.5" />
    <path d="M12 7.5V12l3 2" />
  </Icon>;
}

/** 季節(春=芽・夏=太陽・秋=葉・冬=雪の結晶) */
export function SeasonIcon({ season, ...props }: IconProps & { season: Season }) {
  switch (season) {
    case "spring":
      return <Icon {...props}>
        <path d="M12 20.5v-8" />
        <path d="M12 12.5C12 8.6 9.6 6 5.5 6c0 3.9 2.5 6.5 6.5 6.5z" />
        <path d="M12 10.5c0-3.9 2.4-6.5 6.5-6.5 0 3.9-2.5 6.5-6.5 6.5z" />
      </Icon>;
    case "summer":
      return <Icon {...props}>
        <circle cx="12" cy="12" r="4" />
        <path d="M12 2.5v2.5M12 19v2.5M2.5 12H5M19 12h2.5M5.3 5.3l1.8 1.8M16.9 16.9l1.8 1.8M5.3 18.7l1.8-1.8M16.9 7.1l1.8-1.8" />
      </Icon>;
    case "autumn":
      return <Icon {...props}>
        <path d="M5 19C5 10.5 10 5 19 5c0 9-5.5 14-14 14z" />
        <path d="M5 19l8.5-8.5M9.5 14.5h3.5M9.5 14.5V11" />
      </Icon>;
    case "winter":
      return <Icon {...props}>
        <path d="M12 3v18M4.2 7.5l15.6 9M4.2 16.5l15.6-9" />
        <path d="M9.5 4.5 12 6.5l2.5-2M9.5 19.5 12 17.5l2.5 2M4.9 10.2l3-.9-.6-3M19.1 13.8l-3 .9.6 3M4.9 13.8l3 .9-.6 3M19.1 10.2l-3-.9.6-3" />
      </Icon>;
  }
}

export function MinusIcon(props: IconProps) {
  return <Icon {...props}>
    <path d="M5.5 12h13" />
  </Icon>;
}

export function PlusIcon(props: IconProps) {
  return <Icon {...props}>
    <path d="M5.5 12h13M12 5.5v13" />
  </Icon>;
}

/** 画像の無いセーブの代わりに置く街並み */
export function CityIcon(props: IconProps) {
  return <Icon {...props}>
    <path d="M3 20.5h18" />
    <path d="M5 20.5V11l5-2.5v12" />
    <path d="M10 20.5V4h6v16.5" />
    <path d="M16 20.5V11h3v9.5" />
    <path d="M12.5 7.5h1M12.5 10.5h1M12.5 13.5h1M7 13h1M7 16h1" />
  </Icon>;
}
