/**
 * 軌道カテゴリのバリアント選択UI。
 *
 * 「鉄道(PATH_TRACK)」「モノレール(PATH_MONORAIL)」「ロープウェイ(PATH_ROPEWAY)」を選び、
 * さらに track_type(通常/橋梁/駅)と電化スイッチで対象を絞り込む。同じ組み合わせでも
 * 複数の線路規格がありうるため、最後に道路と同じプルダウンで最終バリアントを確定する。
 * そのうえで線路数(1〜10)・線路間隔・各線の進行方向を指定して複線を構成する。
 * 線路間隔の既定はバリアントのマスター幅で、広げる/縮めるボタンで一定範囲内を
 * 調整できる(パス1本確定ごとに既定へ戻る)。単線では間隔が意味を持たないため非表示。
 *
 * 電化は左右にスライドするON/OFFスイッチで切り替える。電化・非電化のバリアントが
 * 分かれているのは鉄道(PATH_TRACK)だけなので、スイッチは鉄道選択時のみ表示する。
 * 進行方向は線路数ぶんの画像ボタン(上矢印=順方向 / 下矢印=逆方向 / 両矢印=両方向)で表し、
 * クリックで 順方向→逆方向→両方向 と循環する。既定では偶数本は半分ずつ順/逆
 * (左右の割り当ては都市の通行方向による)、奇数本は中央1本が両方向になる
 * (算出は railBuild.defaultTravelDirections)。
 *
 * ロープウェイは描画・敷設とも道路と同じ「両方向の単一パス1本」扱いのため、
 * 線路数・進行方向の指定は表示しない。
 */

import { useTranslation } from "react-i18next";
import type { MasterData, PathMasterEntry } from "../../types";
import {
  PATH_TRACK,
  PATH_MONORAIL,
  PATH_ROPEWAY,
  TRACK_NORMAL,
  TRACK_BRIDGE,
  TRACK_STATION,
  DIR_FORWARD,
  DIR_BACKWARD,
} from "../../constants";
import { NumberStepper } from "../common/NumberStepper";
import { MIN_TRACK_COUNT, MAX_TRACK_COUNT } from "./railBuild";
import upArrowIcon from "../../assets/images/ui/up-arrow.png";
import downArrowIcon from "../../assets/images/ui/down-arrow.png";
import bothArrowIcon from "../../assets/images/ui/both-arrow.png";

const RAIL_TYPE_ORDER = [PATH_TRACK, PATH_MONORAIL, PATH_ROPEWAY];

type RailVariantSelectorProps = {
  masterData: MasterData;
  railType: number | null;
  trackType: number;
  electrified: boolean;
  trackCount: number;
  /** 実効の線路間隔(m)。表示専用で、変更は onAdjustSpacing 経由で行う。 */
  trackSpacing: number;
  canWidenSpacing: boolean;
  canShrinkSpacing: boolean;
  trackDirections: number[];
  trackTypeOptions: number[];
  electrificationOptions: { hasElectrified: boolean; hasNonElectrified: boolean };
  /** 現在の種別・構造・電化に一致する最終バリアントの候補一覧(プルダウンに表示) */
  railVariantOptions: { id: number; data: PathMasterEntry }[];
  /** プルダウンで選択中の最終バリアントID(未解決なら null) */
  selectedVariant: number | null;
  onSelectRailType: (type: number) => void;
  onSelectTrackType: (trackType: number) => void;
  onToggleElectrified: (val: boolean) => void;
  onSelectVariant: (variantId: number) => void;
  onChangeTrackCount: (n: number) => void;
  /** 線路間隔を1段階 広げる(+1)/縮める(-1)。 */
  onAdjustSpacing: (direction: 1 | -1) => void;
  onCycleDirection: (index: number) => void;
  disabled?: boolean;
};

export function RailVariantSelector({
  masterData,
  railType,
  trackType,
  electrified,
  trackCount,
  trackSpacing,
  canWidenSpacing,
  canShrinkSpacing,
  trackDirections,
  trackTypeOptions,
  electrificationOptions,
  railVariantOptions,
  selectedVariant,
  onSelectRailType,
  onSelectTrackType,
  onToggleElectrified,
  onSelectVariant,
  onChangeTrackCount,
  onAdjustSpacing,
  onCycleDirection,
  disabled = false,
}: RailVariantSelectorProps) {
  const { t } = useTranslation();

  const trackTypeLabel = (tt: number): string => {
    if (tt === TRACK_NORMAL) return t("path_construction.rail.trackNormal", "通常");
    if (tt === TRACK_BRIDGE) return t("path_construction.rail.trackBridge", "橋梁");
    if (tt === TRACK_STATION) return t("path_construction.rail.trackStation", "駅");
    return String(tt);
  };

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

  // 電化スイッチの操作可否: 電化・非電化の両バリアントが揃っているときだけ切替可能。
  const electrifyToggleEnabled =
    !disabled && electrificationOptions.hasElectrified && electrificationOptions.hasNonElectrified;

  return (
    <div className="pc-section">
      {/* 鉄道種別ボタン(鉄道/モノレール/ロープウェイ) */}
      <div className="pc-type-group">
        {RAIL_TYPE_ORDER.map((type) => (
          <button
            key={type}
            type="button"
            className={`pc-type-btn ${railType === type ? "active" : ""}`}
            disabled={disabled}
            onClick={() => {
              if (railType !== type) onSelectRailType(type);
            }}
          >
            {masterData.path_types[type]?.display_name ?? `Type ${type}`}
          </button>
        ))}
      </div>

      {railType === null ? (
        <div className="pc-empty-variants" style={{ marginTop: 10 }}>
          {t("path_construction.rail.selectRailTypeFirst", "鉄道種別を選択してください")}
        </div>
      ) : (
        <>
          {/* track_type ボタン(通常/橋梁/駅) */}
          <div className="pc-rail-field">
            <span className="pc-settings-label">
              {t("path_construction.rail.trackType", "構造")}
            </span>
            <div className="pc-type-group pc-type-group-compact">
              {trackTypeOptions.map((tt) => (
                <button
                  key={tt}
                  type="button"
                  className={`pc-type-btn ${trackType === tt ? "active" : ""}`}
                  disabled={disabled}
                  onClick={() => {
                    if (trackType !== tt) onSelectTrackType(tt);
                  }}
                >
                  {trackTypeLabel(tt)}
                </button>
              ))}
            </div>
          </div>

          {/* 電化(左右スライドのON/OFFスイッチ)。電化・非電化が分かれるのは鉄道のみ */}
          {railType === PATH_TRACK && (
          <div className="pc-rail-field">
            <span className="pc-settings-label">
              {t("path_construction.rail.electrified", "電化")}
            </span>
            <button
              type="button"
              role="switch"
              aria-checked={electrified}
              className={`pc-switch ${electrified ? "on" : ""}`}
              disabled={!electrifyToggleEnabled}
              onClick={() => onToggleElectrified(!electrified)}
              title={
                electrifyToggleEnabled
                  ? undefined
                  : t("path_construction.rail.electrifiedFixed", "この構造では電化の切り替えができません")
              }
            >
              <span className="pc-switch-thumb" />
            </button>
            <span className="pc-switch-status">
              {electrified
                ? t("path_construction.rail.electrifiedOn", "電化")
                : t("path_construction.rail.electrifiedOff", "非電化")}
            </span>
          </div>
          )}

          {/* 最終バリアント選択(同じ構造・電化でも複数の線路規格があるためプルダウンで選ぶ) */}
          {railVariantOptions.length === 0 ? (
            <div className="pc-empty-variants" style={{ marginTop: 8 }}>
              {t("path_construction.rail.noVariantForCombo", "この組み合わせに対応する線路がありません")}
            </div>
          ) : (
            <div className="pc-rail-field">
              <span className="pc-settings-label">
                {t("path_construction.rail.variant", "規格")}
              </span>
              <select
                className="pc-select"
                style={{ flex: 1 }}
                value={selectedVariant ?? ""}
                disabled={disabled}
                onChange={(e) => {
                  const val = e.target.value;
                  if (val) {
                    const variantId = parseInt(val);
                    if (variantId !== selectedVariant) onSelectVariant(variantId);
                  }
                }}
              >
                {railVariantOptions.map((v) => (
                  <option key={v.id} value={v.id}>
                    {v.data.display_name}
                  </option>
                ))}
              </select>
            </div>
          )}

          {/*
            線路数・進行方向。ロープウェイは道路と同じ「両方向の単一パス1本」扱いのため
            複線構成の指定は表示しない。
          */}
          {railType !== PATH_ROPEWAY && (
            <>
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
                線路間隔。既定はバリアントのマスター幅(=コリドーが隙間なく隣接)で、
                広げる/縮めるボタンで一定範囲内を調整する。パス1本確定ごとに既定へ戻る。
                単線では意味を持たないため非表示。
              */}
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
            </>
          )}
        </>
      )}
    </div>
  );
}
