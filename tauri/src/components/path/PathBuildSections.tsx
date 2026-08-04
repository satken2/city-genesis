/**
 * パス建設パネルの共有ボディ
 *
 * 道路建設(PathConstructionPanel)と軌道建設(TrackConstructionPanel)は、
 * バリアント選択UI(カテゴリ別セレクタ)より下のセクション ——
 * 説明文・形状(標高/地形追従/盛土/ホーム設置側)・グリッド設定・形状プレビュー・
 * コストカード・確定/キャンセルのアクションバー —— が完全に共通である。
 * この重複(~250行)を避けるため、その共通ボディをこのコンポーネントへ切り出し、
 * 両パネルから `usePathConstruction` の結果とグリッド状態・確定/キャンセルハンドラを
 * 受け取って描画する。
 *
 * ここに置く設定の意図(道路建設パネルから継承):
 *   - 曲線/直線・吸着の操作はUnity側(Ctrlラジアルメニュー)へ移管済みのため置かない。
 *   - 標高・盛土は既存ノードを選んで数値で微調整する用途があるためステッパーを残す
 *     (真実源はUnity。ステッパー変更を change_node/change_embankment で送り返す双方向同期)。
 *   - ホーム設置側は建設時にしか指定できないためここに置く(プラットフォーム系のみ表示)。
 */

import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import { NumberStepper } from "../common/NumberStepper";
import { ConstructionInfoCard } from "../common/ConstructionInfoCard";
import { resolvePathIcon } from "../../utils/pathIcon";
import { PathShapeMapPreview } from "./PathShapeMapPreview";
import { GridSettingsPanel } from "./GridSettingsPanel";
import type { GridDisplayMode } from "./GridSettingsPanel";
import { FOLLOW_TERRAIN_UNSET, usePathConstruction } from "./usePathConstruction";
import {
  DIR_BOTH,
  PLATFORM_SIDE_OUTSIDE,
  PLATFORM_SIDE_INSIDE,
  PLATFORM_SIDE_BOTH,
} from "../../constants";

/** usePathConstruction フックの返り値型。両パネルから丸ごと受け取って共有する。 */
export type PathBuildState = ReturnType<typeof usePathConstruction>;

type PathBuildSectionsProps = {
  pc: PathBuildState;
  apiBaseUrl?: string;
  /** 既存パス編集のID。新規敷設では null。グリッドの原状復帰パラメータに使う。 */
  editPathId: number | null;

  /* ---- グリッド設定状態(親が保持し、原状復帰リスナーと同期する) ---- */
  gridMode: GridDisplayMode;
  gridSize: number;
  gridAngle: number;
  onGridModeChange: (mode: GridDisplayMode) => void;
  onGridSizeChange: (size: number) => void;
  onGridAngleChange: (angle: number) => void;
  onGridOriginChange: (origin: { x: number; z: number } | null) => void;
  onStartSetAngleMode: () => void;
  onEndSetAngleMode: () => void;

  /* ---- 確定/キャンセル ---- */
  isConfirming: boolean;
  onConfirm: () => void;
  onCancel: () => void;

  /**
   * ホーム設置側(外側/内側/両側)トグルを表示してよいか。
   * 道路/インフラでは false、軌道のプラットフォームタブでのみ true にする。
   * 実際の表示は pc.hasPlatform(platform_width>0)との論理積で決まる。
   */
  showPlatformSide: boolean;
};

export function PathBuildSections({
  pc,
  apiBaseUrl,
  editPathId,
  gridMode,
  gridSize,
  gridAngle,
  onGridModeChange,
  onGridSizeChange,
  onGridAngleChange,
  onGridOriginChange,
  onStartSetAngleMode,
  onEndSetAngleMode,
  isConfirming,
  onConfirm,
  onCancel,
  showPlatformSide,
}: PathBuildSectionsProps) {
  const { t } = useTranslation();

  /**
   * 標高ステッパー変更ハンドラ。入力欄で1文字入力するごとにフォーカスが外れる問題を
   * 防ぐため、ここでは sendFocusGraphic() による Unity へのフォーカス強制は行わない。
   */
  const handleHeightChange = useCallback(
    (value: number) => {
      pc.updateHeight(value);
    },
    [pc],
  );

  /** 盛土ステッパー変更ハンドラ。標高と同様に sendFocusGraphic() は呼ばない。 */
  const handleEmbankmentHeightChange = useCallback(
    (value: number) => {
      pc.updateEmbankmentHeight(value);
    },
    [pc],
  );

  return (
    <>
      {/* 説明文 */}
      {pc.selectedVariantData && (
        <div className="pc-section">
          <div className="pc-description-frame">
            <img
              src={resolvePathIcon(pc.selectedVariantData.type)}
              alt={pc.selectedVariantData.display_name}
              className="pc-description-icon"
              draggable={false}
            />
            <div className="pc-description-body">
              <div className="pc-description-name">{pc.selectedVariantData.display_name}</div>
              <div className="pc-description-text">
                {pc.selectedVariantData.description || "—"}
              </div>
            </div>
          </div>
        </div>
      )}

      {/*
        形状セクション。曲線/直線・吸着の操作はUnity側(Ctrlラジアルメニュー)へ移管したが、
        標高・盛土は既存ノードを選んで数値で微調整する用途があるためステッパーを残す。
        ホーム設置側は建設時にしか指定できないためここに置く。
        高さ編集不可(街路)かつインフラでない場合でも盛土は使えるので、いずれかを持つとき表示する。
      */}
      {pc.selectedVariant !== null &&
        (pc.heightEditable || !pc.isInfrastructure || pc.hasPlatform) && (
        <div className="pc-section">
          <div className="pc-section-title">{t("path_construction.shape", "形状")}</div>

          {/*
            標高（相対高さ）。height_editable=true のバリアント(高架道路・地下鉄用線路など)のみ表示。
            下限はマスターの min_height。負の値を持つバリアント(地下)だけが高さ<0に建設できる。
          */}
          {pc.heightEditable && (
            <div className="pc-settings-row">
              <span className="pc-settings-label">{t("path_construction.elevation", "標高 (Y)")}</span>
              <div className="pc-stepper-row">
                <NumberStepper
                  value={pc.height}
                  onChange={handleHeightChange}
                  min={pc.selectedVariantData?.min_height ?? 0}
                  max={500}
                  step={1}
                  integerOnly={true}
                />
                <span className="pc-stepper-unit">m</span>
              </div>
            </div>
          )}

          {/*
            地形追従の個別設定。height_editable のバリアントのみ表示。
            「非追従」にすると、丘に差し掛かる区間が自動的に切り通し・トンネルになり、
            谷を渡る区間は橋になる（手動で標高を刻む必要がなくなる）。
            未設定はバリアント既定に従う。
          */}
          {pc.heightEditable && (
            <div className="pc-settings-row">
              <span className="pc-settings-label">{t("path_construction.followTerrain", "地形追従")}</span>
              <div className="pc-platform-side-group">
                {[
                  { value: FOLLOW_TERRAIN_UNSET, label: t("path_construction.followTerrainDefault", "既定") },
                  { value: 1, label: t("path_construction.followTerrainFollow", "追従") },
                  { value: 0, label: t("path_construction.followTerrainNone", "非追従") },
                ].map((option) => (
                  <button
                    key={option.value}
                    type="button"
                    className={`pc-platform-side-btn ${pc.followTerrain === option.value ? "active" : ""}`}
                    onClick={() => pc.setFollowTerrain(option.value)}
                    title={t(
                      "path_construction.followTerrainHelp",
                      "非追従にすると、丘に差し掛かる区間は自動的に切り通し・トンネルに、谷を渡る区間は橋になります。",
                    )}
                  >
                    {option.label}
                  </button>
                ))}
              </div>
            </div>
          )}

          {/*
            盛土・切土(-5〜+5m)。インフラパス(送電線・導水管)は地形変形を伴わないため非表示。
            道路のY座標は0のまま、着工時にこの高さだけDynamicティアの地形を変形させる。
            地形追従しない(非追従)パスは盛土と概念的に相容れないため非表示にする。
          */}
          {!pc.isInfrastructure && pc.followTerrain !== 0 && (
            <div className="pc-settings-row">
              <span className="pc-settings-label">{t("path_construction.embankmentHeight", "盛土高さ")}</span>
              <div className="pc-stepper-row">
                <NumberStepper
                  value={pc.embankmentHeight}
                  onChange={handleEmbankmentHeightChange}
                  min={-5}
                  max={5}
                  step={1}
                  integerOnly={true}
                />
                <span className="pc-stepper-unit">m</span>
              </div>
            </div>
          )}

          {/*
            駅パスのホーム設置側（platform_width を持つバリアントのみ表示）。
            「内側/外側」は都市の通行方向を基準に解決される（左側通行なら内側=進行方向右）。
            複線（単線2本）の上下線とも内側にすると島式ホーム、外側にすると相対式ホームになる。
            建設時にのみ指定でき、建設後の変更はできない。
          */}
          {showPlatformSide && pc.hasPlatform && (
            <div className="pc-settings-row">
              <span className="pc-settings-label">{t("path_construction.platformSide", "ホーム設置側")}</span>
              <div className="pc-platform-side-group">
                {[
                  { value: PLATFORM_SIDE_OUTSIDE, label: t("path_construction.platformSideOutside", "外側") },
                  { value: PLATFORM_SIDE_INSIDE, label: t("path_construction.platformSideInside", "内側") },
                  { value: PLATFORM_SIDE_BOTH, label: t("path_construction.platformSideBoth", "両側") },
                ].map((option) => (
                  <button
                    key={option.value}
                    type="button"
                    className={`pc-platform-side-btn ${pc.platformSide === option.value ? "active" : ""}`}
                    onClick={() => pc.setPlatformSide(option.value)}
                  >
                    {option.label}
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>
      )}

      {/* グリッド設定（バリアント選択後のみ表示） */}
      {pc.selectedVariant !== null && (
        <div className="pc-section">
          <GridSettingsPanel
            mode={gridMode}
            size={gridSize}
            angle={gridAngle}
            onModeChange={onGridModeChange}
            onSizeChange={onGridSizeChange}
            onAngleChange={onGridAngleChange}
            onOriginChange={onGridOriginChange}
            originalState="STATE_EDIT_PATH"
            originalExtra={{
              // 複合パス選択中はUI専用の擬似タイプではなく、Unityが解釈できる
              // 代表バリアント(先頭コンポーネント)のタイプ/バリアントを渡す
              path_type: pc.unityPathType,
              path_variant: pc.unityPathVariant,
              path_id: editPathId ?? null,
              // 角度設定モードからの復帰でSTATE_EDIT_PATHを再送する際、軌道複線の
              // 平行プレビューが単線に戻らないようオフセット列・進行方向列も渡す
              // (無指定は空リセット扱い)
              lateral_offsets: pc.railComponents
                ? pc.railComponents.map((c) => c.lateral_offset ?? 0)
                : [],
              travel_directions: pc.railComponents
                ? pc.railComponents.map((c) => c.travel_direction ?? DIR_BOTH)
                : [],
            }}
            onStartSetAngleMode={onStartSetAngleMode}
            onEndSetAngleMode={onEndSetAngleMode}
          />
        </div>
      )}

      {/* 形状プレビュー（パネル最下部に配置） */}
      {pc.selectedVariant !== null && (
        <div className="pc-section">
          <div className="pc-section-title">
            {t("building_construction.shapePreview", "形状プレビュー")}
          </div>
          {pc.nodes.length === 0 || !apiBaseUrl ? (
            <div className="pc-no-nodes-message">
              {t("path_construction.noNodesYet")}
            </div>
          ) : (
            <div className="pc-canvas-section">
              <PathShapeMapPreview
                apiBaseUrl={apiBaseUrl}
                nodes={pc.nodes}
                curveMode={pc.curveMode}
                startControl={pc.startControl}
                endControl={pc.endControl}
                waypointControls={pc.waypointControls}
                lateralOffsets={
                  pc.railComponents
                    ? pc.railComponents.map((c) => c.lateral_offset ?? 0)
                    : undefined
                }
              />
            </div>
          )}

          {/* 推定建設コスト(詳細パネルと共通のカード。標準コスト倍率はバッジで表示) */}
          {pc.nodes.length >= 2 && (
            <div style={{ marginTop: 10 }}>
              <ConstructionInfoCard
                estimatedCost={pc.estimatedCost}
                standardCost={pc.estimatedStandardCost}
                costRatioHelp={t("path_construction.costRatioHelp")}
                estimatedDurationDays={pc.estimatedDurationDays}
                isCostLoading={pc.isCostLoading}
                nextEpochConstructionBudget={pc.nextEpochConstructionBudget}
              />
            </div>
          )}
        </div>
      )}

      {/*
        アクションボタン（キャンセル/確定）。
        パネル下部に固定表示し、内容がスクロールしても常に見えるようにする
        (position: sticky; bottom: 0)。バリアント選択後のみ表示する。
      */}
      {pc.selectedVariant !== null && (
        <div className="pc-action-bar">
          <div className="pc-actions">
            <button className="pc-btn" type="button" onClick={onCancel}>
              {t("path_construction.cancel")}
            </button>
            <button
              className="pc-btn pc-btn-confirm"
              disabled={pc.nodes.length < 2 || isConfirming}
              type="button"
              onClick={onConfirm}
            >
              {isConfirming ? t("path_construction.confirming") : t("path_construction.confirm")}
            </button>
          </div>
        </div>
      )}
    </>
  );
}
