/**
 * 軌道の複線構成(線路数・各線の進行方向・線路間隔)の設定セクション。
 *
 * 線路間隔の既定はバリアントのマスター幅(=各線のコリドーが隙間なく隣接)で、
 * 広げる/縮めるボタンで一定範囲内を調整できる(パス1本確定ごとに既定へ戻る)。
 * 単線では間隔が意味を持たないため非表示。
 *
 * 進行方向は線路数ぶんの画像ボタン(上矢印=順方向 / 下矢印=逆方向 / 両矢印=両方向)で表し、
 * クリックで 順方向→逆方向→両方向 と循環する。既定では偶数本は半分ずつ順/逆
 * (左右の割り当ては都市の通行方向による)、奇数本は中央1本が両方向になる
 * (算出は railBuild.defaultTravelDirections)。
 *
 * 人員搬送(ロープウェイ等)は描画・敷設とも道路と同じ「両方向の単一パス1本」扱いのため、
 * 軌道以外(駅舎など)と同じくセクションごと描画しない(区切り線も出ない)。
 *
 * 複線一体の線路(線路系で lanes≥2。駅を含む)も単一パス1本で敷くので線路数・進行方向・
 * 線路間隔は出さず、「1本で上下線を表す」注記だけを同じ位置に出す(規格の切り替えで
 * 下の段が大きく上下しないようにセクション自体は残す)。
 */

import { useTranslation } from "react-i18next";
import { PATH_CONVEYOR, DIR_FORWARD, DIR_BACKWARD } from "../../constants";
import type { PathMasterEntry } from "../../types";
import { NumberStepper } from "../common/NumberStepper";
import { MIN_TRACK_COUNT, MAX_TRACK_COUNT, isMultiTrackRailVariant, isRailType } from "./railBuild";
import upArrowIcon from "../../assets/images/ui/up-arrow.png";
import downArrowIcon from "../../assets/images/ui/down-arrow.png";
import bothArrowIcon from "../../assets/images/ui/both-arrow.png";

type RailTrackLayoutSettingsProps = {
  /** 選択中のバリアント。複線一体の線路なら設定の代わりに注記を出す。 */
  variant: PathMasterEntry | null;
  trackCount: number;
  /** 実効の線路間隔(m)。表示専用で、変更は onAdjustSpacing 経由で行う。 */
  trackSpacing: number;
  canWidenSpacing: boolean;
  canShrinkSpacing: boolean;
  trackDirections: number[];
  onChangeTrackCount: (n: number) => void;
  /** 線路間隔を1段階 広げる(+1)/縮める(-1)。 */
  onAdjustSpacing: (direction: 1 | -1) => void;
  onCycleDirection: (index: number) => void;
  disabled?: boolean;
};

export function RailTrackLayoutSettings({
  variant,
  trackCount,
  trackSpacing,
  canWidenSpacing,
  canShrinkSpacing,
  trackDirections,
  onChangeTrackCount,
  onAdjustSpacing,
  onCycleDirection,
  disabled = false,
}: RailTrackLayoutSettingsProps) {
  const { t } = useTranslation();

  /** 進行方向を画像で表す。順方向=上矢印 / 逆方向=下矢印 / 両方向=両矢印。 */
  const directionImage = (dir: number) => {
    if (dir === DIR_FORWARD) {
      return <img src={upArrowIcon} alt="" className="pc-track-arrow-img" draggable={false} />;
    }
    if (dir === DIR_BACKWARD) {
      return <img src={downArrowIcon} alt="" className="pc-track-arrow-img" draggable={false} />;
    }
    // DIR_BOTH: 両方向
    return <img src={bothArrowIcon} alt="" className="pc-track-arrow-img" draggable={false} />;
  };
  const directionTitle = (dir: number): string => {
    if (dir === DIR_FORWARD) return t("path_construction.rail.dirForward", "順方向");
    if (dir === DIR_BACKWARD) return t("path_construction.rail.dirBackward", "逆方向");
    return t("path_construction.rail.dirBoth", "両方向");
  };

  if (!variant || !isRailType(variant.type) || variant.type === PATH_CONVEYOR) return null;

  if (isMultiTrackRailVariant(variant)) {
    return (
      <div className="pc-section">
        <div className="pc-section-title">{t("path_construction.rail.parallel", "並走")}</div>
        <div className="pc-description-text">{t("path_construction.rail.builtInDoubleTrack")}</div>
      </div>
    );
  }

  return (
    <div className="pc-section">
      <div className="pc-section-title">{t("path_construction.rail.parallel", "並走")}</div>

      {/* 線路数 */}
      <div className="pc-rail-field">
        <span className="pc-settings-label">
          {t("path_construction.rail.trackCount", "線路数")}
        </span>
        <div className="pc-stepper-row">
          <NumberStepper
            value={trackCount}
            onChange={onChangeTrackCount}
            min={MIN_TRACK_COUNT}
            max={MAX_TRACK_COUNT}
            step={1}
            integerOnly
            disabled={disabled}
          />
        </div>
      </div>

      {/*
        進行方向(線路数ぶんの画像ボタン)。
        キャプションは表示しないが、他の設定行とラベル幅を揃えてボタン位置を保つため、
        空のラベル枠は残す。
      */}
      <div className="pc-rail-field">
        <span className="pc-settings-label" aria-hidden="true" />
        <div className="pc-track-direction-row">
          {trackDirections.map((dir, i) => (
            <button
              key={i}
              type="button"
              className="pc-track-arrow"
              disabled={disabled}
              title={directionTitle(dir)}
              onClick={() => onCycleDirection(i)}
            >
              {directionImage(dir)}
            </button>
          ))}
        </div>
      </div>

      {/* 線路間隔(単線では意味を持たないため非表示) */}
      {trackCount >= 2 && (
        <div className="pc-rail-field">
          <span className="pc-settings-label">
            {t("path_construction.rail.trackSpacing", "線路間隔")}
          </span>
          <button
            type="button"
            className="pc-spacing-btn"
            disabled={disabled || !canShrinkSpacing}
            onClick={() => onAdjustSpacing(-1)}
          >
            {t("path_construction.rail.spacingShrink", "縮める")}
          </button>
          <span className="pc-spacing-value">{trackSpacing.toFixed(1)} m</span>
          <button
            type="button"
            className="pc-spacing-btn"
            disabled={disabled || !canWidenSpacing}
            onClick={() => onAdjustSpacing(1)}
          >
            {t("path_construction.rail.spacingWiden", "広げる")}
          </button>
        </div>
      )}
    </div>
  );
}
