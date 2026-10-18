/**
 * パス建設パネル(PathConstructionPanel)の建設画面の共通ボディ
 *
 * バリアントの種別によらず共通のセクション ——
 * 形状(標高/地形追従/盛土/ホーム設置側)・グリッド設定・
 * 建設費用カード(パス長/推定コスト/工期)・確定/キャンセルのアクションバー —— を描く。
 * 種別ごとの設定(軌道の複線構成・駅舎の配置端・所属道路など)はパネル側がこの上に並べる。
 * `usePathConstruction` の結果とグリッド状態・確定/キャンセルハンドラを受け取って描画する。
 *
 * ここに置く設定の意図:
 *   - 曲線/直線・吸着の操作はUnity側(Ctrlラジアルメニュー)へ移管済みのため置かない。
 *   - 標高・盛土は既存ノードを選んで数値で微調整する用途があるためステッパーを残す
 *     (真実源はUnity。ステッパー変更を change_node/change_embankment で送り返す双方向同期)。
 *   - ホーム設置側は建設時にしか指定できないためここに置く(設置側を選ぶ単線の駅のみ表示。
 *     1本で上下線を持つ複線一体の線路の駅はホームの位置がバリアントで決まる(相対式・島式)ので出さない)。
 */

import { useCallback } from "react";
import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";
import { NumberStepper } from "../common/NumberStepper";
import { ConstructionInfoCard } from "../common/ConstructionInfoCard";
import { isConstructionAffordable } from "../../utils/constructionFunding";
import { GridSettingsPanel } from "./GridSettingsPanel";
import type { GridDisplayMode } from "./GridSettingsPanel";
import { FOLLOW_TERRAIN_UNSET, usePathConstruction } from "./usePathConstruction";
import {
  DIR_BOTH,
  PLATFORM_SIDE_OUTSIDE,
  PLATFORM_SIDE_INSIDE,
  PLATFORM_SIDE_BOTH,
} from "../../constants";

/** usePathConstruction フックの返り値型。パネルから丸ごと受け取る。 */
export type PathBuildState = ReturnType<typeof usePathConstruction>;

type PathBuildSectionsProps = {
  pc: PathBuildState;
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
   * 軌道の新規敷設でだけ true にする(ホーム設置側は建設後に変えられない)。
   * 実際の表示は pc.hasPlatformSide(設置側を選ぶ駅バリアント)との論理積で決まる。
   */
  showPlatformSide: boolean;
};

export function PathBuildSections({
  pc,
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

  /**
   * 2Dパス形状編集パネル(path_shape_editor)を開く。曲線モードで形状を確定したときは
   * usePathConstruction が自動で開くが、直線モードや配置中でも手動で開けるようにする。
   * 既に開いていれば前面化されるだけ(single ポリシー)。
   */
  const handleOpenShapeEditor = useCallback(() => {
    void invoke("open_registered_window", { windowType: "path_shape_editor" }).catch(console.error);
  }, []);

  return (
    <>
      {/*
        形状セクション。曲線/直線・吸着の操作はUnity側(Ctrlラジアルメニュー)へ移管したが、
        標高・盛土は既存ノードを選んで数値で微調整する用途があるためステッパーを残す。
        ホーム設置側は建設時にしか指定できないためここに置く。
        高さ編集不可(街路)かつインフラでない場合でも盛土は使えるので、いずれかを持つとき表示する。
        展開する敷設方法(碁盤状)は地表に敷くので出さない。
      */}
      {pc.selectedVariant !== null && !pc.isLayoutBuild &&
        (pc.heightEditable || !pc.isInfrastructure || pc.hasPlatformSide) && (
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
                  step={0.1}
                  integerOnly={false}
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
                    data-guide="path.followTerrain"
                    data-guide-value={option.value}
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
                  step={0.1}
                  integerOnly={false}
                />
                <span className="pc-stepper-unit">m</span>
              </div>
            </div>
          )}

          {/*
            駅パスのホーム設置側（platform_width を持つ単線の駅のみ表示）。
            「内側/外側」は都市の通行方向を基準に解決される（左側通行なら内側=進行方向右）。
            複線（単線2本）の上下線とも内側にすると島式ホーム、外側にすると相対式ホームになる。
            1本で上下線を持つ複線一体の線路の駅はホームの位置がバリアントで決まる（相対式・島式）ので出さない。
            建設時にのみ指定でき、建設後の変更はできない。
          */}
          {showPlatformSide && pc.hasPlatformSide && (
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
                    data-guide="path.platformSide"
                    data-guide-value={option.value}
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
              build_method: pc.buildMethod,
              grid_divisions: pc.gridDivisions,
            }}
            onStartSetAngleMode={onStartSetAngleMode}
            onEndSetAngleMode={onEndSetAngleMode}
          />
        </div>
      )}

      {/*
        建設費用セクション(パネル最下部)。ノードが2点そろうまではコストを算出できないため
        敷設を促すメッセージを出す。
      */}
      {pc.selectedVariant !== null && (
        <div className="pc-section">
          <div className="pc-section-title">
            {t("path_construction.costInfo", "建設費用")}
          </div>
          {pc.nodes.length < 2 ? (
            <div className="pc-no-nodes-message">
              {t("path_construction.noNodesYet")}
            </div>
          ) : (
            <ConstructionInfoCard
              pathLength={pc.pathLength}
              estimatedCost={pc.estimatedCost}
              standardCost={pc.estimatedStandardCost}
              costRatioHelp={t("path_construction.costRatioHelp")}
              estimatedDurationDays={pc.estimatedDurationDays}
              isCostLoading={pc.isCostLoading}
              maintenanceCost={pc.maintenanceCost}
              epochRevenue={pc.epochRevenue}
              availableFunds={pc.availableFunds}
              requiredTotal={pc.estimatedCost}
              showAvailableFunds={false}
              supplyRatio={pc.supplyRatio}
              expectedDurationDays={pc.expectedDurationDays}
            />
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
          {pc.nodes.length >= 2 && (!pc.canConfirm || !pc.alignmentOk) && (
            <div className="pc-validation-message" role="status">
              {(pc.canConfirm ? [t("path_construction.shapeError")] : pc.confirmMessages)
                .map((message, index) => <p key={index}>{message}</p>)}
              {pc.canRetryValidation && (
                <button className="pc-btn" type="button" onClick={pc.retryValidation}>
                  {t("path_construction.validationRetry")}
                </button>
              )}
            </div>
          )}
          <div className="pc-actions">
            {!pc.isLayoutBuild && (
              <button data-guide="path.shapeEditor" className="pc-btn" type="button" onClick={handleOpenShapeEditor}>
                {t("path_construction.openShapeEditor")}
              </button>
            )}
            <button data-guide="path.cancel" className="pc-btn" type="button" onClick={onCancel}>
              {t("path_construction.cancel")}
            </button>
            <button
              className="pc-btn pc-btn-confirm"
              data-guide="path.confirm"
              // 資金不足(推定総額 > 利用可能額)と線形制約違反(急カーブ・急勾配)は確定させない。
              // バックエンドも同じ基準で拒否する(INSUFFICIENT_FUNDS / CREATEPATH_CURVE_TOO_TIGHT / CREATEPATH_GRADE_TOO_STEEP)。
              disabled={
                !pc.canConfirm
                || isConfirming
                || !pc.alignmentOk
                || !isConstructionAffordable(pc.estimatedCost, pc.availableFunds)
              }
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
