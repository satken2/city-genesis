/**
 * 市民の追跡で使う自前のSVGアイコン(外部素材を取り込まないため、クレジット表記の対象にならない)。
 * 線の色は currentColor で、ボタンの文字色に従う。
 */

/** カメラで追いかける。 */
export function CameraIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round" aria-hidden="true">
      <path d="M4 8h3l1.5-2h7L17 8h3v11H4z" />
      <circle cx="12" cy="13.5" r="3.2" />
    </svg>
  );
}

/** 追跡リストに留める(しおり)。 */
export function TrackIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round" aria-hidden="true">
      <path d="M7 3.5h10v17l-5-3.6-5 3.6z" />
    </svg>
  );
}

/** 追跡リストから外す。 */
export function RemoveIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" aria-hidden="true">
      <path d="M6 6l12 12M18 6L6 18" />
    </svg>
  );
}
