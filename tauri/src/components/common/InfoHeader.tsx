/**
 * 情報ヘッダーコンポーネント
 *
 * エンティティのアイコン・ステータス・名前を横並びで表示する。
 * 経済主体・土地区画・地区・パス・リソースの各詳細パネルで流用する。
 *
 * ステータスが建設中(CONSTRUCTING)の場合は、
 * constructionProgress(0〜100)に応じたプログレスバーを表示する。
 * その他のステータスは STATUS_STYLE_MAP に基づく単色のバッジ表示となる。
 *
 * iconLabel を指定すると、アイコンを角丸枠で縁取りし、
 * 枠の下部にラベルを黒文字で表示する(公共施設の明示などに使用)。
 * 枠色は既定で黄色。iconFrameColor を渡すと用途ごとに色分けできる。
 *
 * onNameSave を渡すと名称がトグル式の編集可能ラベル(EditableLabel)になる。
 * 名称を変更できるのはプレイヤーが管理する対象(公共施設・パス・地区)だけなので、
 * 変更可否の判断は呼び出し側が行い、ハンドラの有無で表現する。
 *
 * onFocusCamera を渡すと、右上(ステータスバーの右)に3Dビューのカメラを対象へ動かすボタンを出す。
 * 映す位置を持たない対象では渡さない。
 */

import { useTranslation } from "react-i18next";
import { STATUS_STYLE_MAP } from "../../local-constants";
import { CameraFocusButton } from "./CameraFocusButton";
import { EditableLabel } from "./EditableLabel";
import "./InfoHeader.css";

type InfoHeaderProps = {
  /** 解決済みのアイコン画像パス */
  iconSrc: string;
  /** ステータスコード */
  status?: number;
  /** 現在のステータスになった理由(REASON_*)。0(REASON_NONE)以外はバッジに括弧書きで併記する */
  statusReason?: number;
  /** バリアント名(種別名)。ステータスと名称の間に表示する */
  variantName?: string;
  /** エンティティ名 */
  name: string;
  /** 建設進捗率(0〜100)。建設中ステータスの場合に使用 */
  constructionProgress?: number;
  /** ステータスを表示するかどうか(デフォルトtrue) */
  showStatus?: boolean;
  /** アイコンの縁取り枠内に表示するラベル(例: 公共施設)。未指定なら通常のアイコン表示 */
  iconLabel?: string;
  /** 縁取り枠の色(CSSカラー)。未指定なら黄色 */
  iconFrameColor?: string;
  /** 名称の保存処理。指定すると名称が編集可能ラベルになる。失敗時は例外を投げること */
  onNameSave?: (next: string) => Promise<void> | void;
  /** 名称編集時の初期値。name がID表記へのフォールバックの場合に生の名称を渡す */
  nameValue?: string;
  /** 3Dビューのカメラを対象へ動かす処理。指定するとボタンを出す */
  onFocusCamera?: () => void;
};

export function InfoHeader({
  iconSrc,
  status,
  statusReason,
  variantName,
  name,
  constructionProgress,
  showStatus = true,
  iconLabel,
  iconFrameColor,
  onNameSave,
  nameValue,
  onFocusCamera,
}: InfoHeaderProps) {
  const { t } = useTranslation();
  const style = status !== undefined ? STATUS_STYLE_MAP[status] ?? { bg: "#6b7280", text: "#ffffff" } : { bg: "#6b7280", text: "#ffffff" };
  const reasonLabel =
    statusReason !== undefined && statusReason !== 0
      ? t(`reason.${statusReason}`, { defaultValue: "" })
      : "";
  const statusLabel = status !== undefined
    ? t(`status.${status}`, { defaultValue: `Status ${status}` })
      + (reasonLabel ? `（${reasonLabel}）` : "")
    : "";
  const progress = Math.max(0, Math.min(100, constructionProgress ?? 0));

  return (
    <div className="info-header">
      {iconLabel ? (
        // ラベル付きの場合は角丸枠でアイコンを縁取り、下部余白にラベルを表示する
        <div
          className="info-header-icon-frame"
          style={iconFrameColor ? { backgroundColor: iconFrameColor } : undefined}
        >
          <img className="info-header-icon" src={iconSrc} alt={name} />
          <span className="info-header-icon-frame-label">{iconLabel}</span>
        </div>
      ) : (
        <img className="info-header-icon" src={iconSrc} alt={name} />
      )}
      <div className="info-header-main">
        <div className="info-header-body">
          {showStatus && status !== undefined && (
            <div
              className={
                style.showProgress
                  ? "info-header-status info-header-status--progress"
                  : "info-header-status"
              }
              style={{ backgroundColor: style.bg, color: style.text }}
            >
              {style.showProgress ? (
                <div className="info-header-progress-bar">
                  <div
                    className="info-header-progress-fill"
                    style={{ width: `${progress}%`, backgroundColor: style.bg }}
                  />
                  <span className="info-header-progress-label">
                    {statusLabel} {progress.toFixed(0)}%
                  </span>
                </div>
              ) : (
                <span>{statusLabel}</span>
              )}
            </div>
          )}
          {variantName && (
            <span className="info-header-variant">{variantName}</span>
          )}
          {onNameSave ? (
            <EditableLabel
              value={nameValue ?? name}
              displayText={name}
              textClassName="info-header-name"
              onSave={onNameSave}
            />
          ) : (
            <span className="info-header-name">{name}</span>
          )}
        </div>
        {onFocusCamera && <CameraFocusButton onClick={onFocusCamera} />}
      </div>
    </div>
  );
}
