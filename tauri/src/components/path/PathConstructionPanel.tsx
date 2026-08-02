/**
 * 道路建設パネル
 *
 * window_type="path_build" のポップアップウィンドウで表示する専用パネル。
 * BuildingConstructionPanel と同じ単列レイアウト。
 *
 * 操作フロー:
 *   1. 道路種別プルダウンでカテゴリを選択 → バリアントタイルが表示される
 *   2. バリアントタイルをクリックして選択 → IPCでグラフィックプログラムに道路敷設モードを通知
 *   3. グラフィックプログラム側でノードを配置 → IPCでノード情報がパネルに通知される
 *   4. プレビューキャンバスにパス形状が表示される
 *   5. [確定] でバックエンドに送信
 *
 * 曲線/直線・吸着の設定はUnity側の操作系(Ctrlラジアルメニュー・カーソルHUD)が担い、
 * このパネルには置かない。標高・盛土は既存ノードを選んで数値で微調整する用途があるため
 * パネルにもステッパーを残す(Unityのキー操作と双方向同期)。
 * パネルが持つのはバリアント選択・標高/盛土・ホーム設置側・グリッド設定(サイズ/角度)・
 * 形状プレビュー・コスト・確定/キャンセル。
 */

import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useMasterData } from "../../contexts/MasterDataContext";
import { NumberStepper } from "../common/NumberStepper";
import { ConstructionInfoCard } from "../common/ConstructionInfoCard";

import { setHintbarText } from "../../utils/hintbar";
import { resolvePathIcon } from "../../utils/pathIcon";
import { showNotification } from "../../utils/notification";
import { ApiError } from "../../utils/api";
import { usePathConstruction, FOLLOW_TERRAIN_UNSET } from "./usePathConstruction";
import { sendEditPathFinish, sendSetGrid, sendChangeDisplay, sendChangeState, sendFocusGraphic, IPC_EVENT_VIEW_GRID_TOGGLED } from "../../utils/ipc";
import { PathShapeMapPreview } from "./PathShapeMapPreview";
import { GridSettingsPanel } from "./GridSettingsPanel";
import type { GridDisplayMode } from "./GridSettingsPanel";
import { isInfraPathType } from "../../local-constants";
import {
  PLATFORM_SIDE_OUTSIDE,
  PLATFORM_SIDE_INSIDE,
  PLATFORM_SIDE_BOTH,
} from "../../constants";
import { RoadVariantSelector } from "./RoadVariantSelector";
import { InfraVariantSelector } from "./InfraVariantSelector";
import { RailVariantSelector } from "./RailVariantSelector";
import type { PathCategory } from "./usePathConstruction";
import "./PathConstructionPanel.css";

type PathConstructionPanelProps = {
  apiBaseUrl?: string;
  onClose: () => void;
  editPathId?: number | null;
};

export function PathConstructionPanel({ apiBaseUrl, onClose, editPathId }: PathConstructionPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const pathConstruction = usePathConstruction(masterData, apiBaseUrl, editPathId ?? null);

  /* ---- 既存パス編集モード: データ取得 ---- */
  useEffect(() => {
    if (editPathId == null || !apiBaseUrl) return;
    const controller = new AbortController();

    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/${editPathId}`, {
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<{ path_type: number; path_variant: number }>;
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        pathConstruction.onTypeChange(data.path_type);
        pathConstruction.selectVariant(data.path_variant);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          console.error("[PathConstructionPanel] Failed to fetch existing path:", err);
        }
      });

    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [editPathId, apiBaseUrl]);

  /* ---- グリッド設定状態（デフォルト非表示） ---- */
  const [gridMode, setGridMode] = useState<GridDisplayMode>("off");
  const [gridSize, setGridSize] = useState(20);
  const [gridAngle, setGridAngle] = useState(0);
  const [gridOrigin, setGridOrigin] = useState<{ x: number; z: number } | null>(null);

  /* ---- 確定処理中状態 ---- */
  const [isConfirming, setIsConfirming] = useState(false);

  /* ---- 角度設定モード中のグリッド表示状態の退避・復元 ---- */
  // 吸着フラグの退避・復元はUnity側(GridSetManager)が行うため、パネルはグリッド表示だけ面倒を見る。
  const prevGridModeRef = useRef<GridDisplayMode | null>(null);
  const prevGridOriginRef = useRef<{ x: number; z: number } | null>(null);

  const handleStartSetAngleMode = useCallback(() => {
    prevGridModeRef.current = gridMode;
    prevGridOriginRef.current = gridOrigin;

    if (gridMode === "off") {
      setGridMode("line");
    }
  }, [gridMode, gridOrigin]);

  const handleEndSetAngleMode = useCallback(() => {
    if (prevGridModeRef.current !== null) {
      setGridMode(prevGridModeRef.current);
      prevGridModeRef.current = null;
    }
    if (prevGridOriginRef.current !== null) {
      setGridOrigin(prevGridOriginRef.current);
      prevGridOriginRef.current = null;
    }
  }, []);

  /* ---- ハンドラ ---- */

  const handleCategoryChange = useCallback(
    (next: PathCategory) => {
      // 同じカテゴリを再クリックしたら解除(選択なし)に戻す
      pathConstruction.onCategoryChange(pathConstruction.category === next ? null : next);
    },
    [pathConstruction]
  );

  /**
   * 標高ステッパー変更ハンドラ。
   * 入力欄で1文字入力するごとにフォーカスが外れる問題を防ぐため、ここでは
   * sendFocusGraphic() による Unity へのフォーカス強制は行わない。
   */
  const handleHeightChange = useCallback(
    (value: number) => {
      pathConstruction.updateHeight(value);
    },
    [pathConstruction]
  );

  /**
   * 盛土ステッパー変更ハンドラ。標高と同様に sendFocusGraphic() は呼ばない。
   */
  const handleEmbankmentHeightChange = useCallback(
    (value: number) => {
      pathConstruction.updateEmbankmentHeight(value);
    },
    [pathConstruction]
  );

  /**
   * パネル全体のクリックイベントを捕捉する共通のハンドラ。
   * ボタン要素やバリアントタイルなど、ユーザーが明示的に対話する要素をクリックした際に、
   * 直後にUnity側での3Dビュー操作をシームレスに行えるよう、Unityにフォーカスを強制する。
   * 数値入力欄（NumberStepperのインプット）など、入力フォーカスを維持すべき要素をクリックした場合は
   * Unityへのフォーカス移動を行わないよう、対象を制限している。
   */
  const handlePanelClick = useCallback((e: React.MouseEvent<HTMLDivElement>) => {
    const target = e.target as HTMLElement;
    const button = target.closest("button, [role='button']");
    if (button) {
      sendFocusGraphic().catch(() => {});
    }
  }, []);

  /**
   * 確定ボタンを押した際のハンドラ。
   * Tauri側が直接バックエンドにパスデータを送信する。
   *
   * 新規敷設(editPathId == null)の場合は連続設置モードとして動作し、
   * 確定後もパネルを閉じずに選択中バリアントを維持したまま次のパスを
   * 配置できる状態へ再遷移する。
   * 既存パス編集(editPathId != null)の場合は確定後ただちにパネルを閉じる。
   * 失敗時はエラーを通知してユーザーに再試行を許可する。
   */
  const handleConfirm = useCallback(async () => {
    setIsConfirming(true);
    try {
      await pathConstruction.confirmPath();
      // 成功時: Unityに編集終了を通知してデータをリロードさせる
      await sendEditPathFinish();
      if (editPathId != null) {
        // 既存パス編集: 確定したらパネルを閉じる
        // 先に STATE_NORMAL に戻してから表示モードを切り替える。
        // STATE_NORMAL でないと view:change_view の変更が _normalStateMemory に記憶されず、
        // 後から Rust 側が STATE_NORMAL を送った際に default に戻ってしまうため。
        await sendChangeState("STATE_NORMAL");
        await sendSetGrid("off");
        // 道路を作成したので、ユーザーが建設結果を確認できるよう計画表示に切り替える。
        await sendChangeDisplay("road", "plan");
        onClose();
      } else {
        // 新規敷設: 連続設置モード。バリアント選択を維持して次のパスを配置できるようにする。
        // 線路間隔の調整は確定したパス限りの設定なので、ここで既定へ戻す。
        pathConstruction.continuePlacement(true);
        setIsConfirming(false);
        void showNotification({
          message: t("path_construction.placedContinue"),
          durationMs: 3000,
        });
      }
    } catch (err) {
      setIsConfirming(false);
      let displayMessage: string;
      if (err instanceof ApiError) {
        const i18nKey = `errors.${err.code}`;
        displayMessage =
          i18nKey !== "errors.UNKNOWN_ERROR"
            ? t(i18nKey, { defaultValue: err.message })
            : err.message;
      } else {
        displayMessage = t("path_construction.confirmFailedUnknown");
      }
      void showNotification({ message: displayMessage, durationMs: 8000 });
    }
  }, [editPathId, onClose, pathConstruction, t]);

  /**
   * キャンセルボタンを押した際のハンドラ。
   * バリアント選択・カテゴリ選択は解除せず、Unity側に配置中の仮形状(まだ確定していない
   * ノード列)だけをクリアして、同じバリアントで再び配置できる状態へ戻す
   * (同じパスの引き直しを想定するため、線路間隔の調整も維持する)。
   * continuePlacement は配置状態をリセットしつつ STATE_EDIT_PATH を再送するため、
   * Unity側の仮形状が消え、敷設モードが維持される。
   */
  const handleCancel = useCallback(() => {
    pathConstruction.continuePlacement();
  }, [pathConstruction]);

  /* ---- インフラパス選択時にグリッドを自動オフ ---- */
  useEffect(() => {
    if (isInfraPathType(pathConstruction.selectedType)) { // 電気・水（PATH_TYPES_INFRA）
      setGridMode("off");
    }
  }, [pathConstruction.selectedType]);

  /* ---- コンポーネント破棄時にグリッドを確実に OFF にする ---- */
  useEffect(() => {
    return () => {
      sendSetGrid("off").catch(() => {});
    };
  }, []);

  /* ---- Unity側ラジアルメニューのグリッドトグルと表示状態を同期 ---- */
  // グリッド表示⇔グリッド吸着の連動はUnity側(GridOverlay)が担う。
  // ラジアルメニューでトグルされたら view:grid_toggled が届くので、
  // パネルのグリッド設定UIの表示状態(gridMode)だけを追従させる。
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlisten: (() => void) | undefined;
    appWindow
      .listen<{ visible: boolean }>(IPC_EVENT_VIEW_GRID_TOGGLED, (event) => {
        setGridMode((prev) => {
          if (event.payload.visible) {
            return prev === "off" ? "line" : prev;
          }
          return "off";
        });
      })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);
    return () => { unlisten?.(); };
  }, []);

  /* ---- グリッド設定変更時に Unity に通知 ---- */
  useEffect(() => {
    if (pathConstruction.selectedVariant !== null) {
      sendSetGrid(gridMode, gridSize, gridAngle, gridOrigin?.x, gridOrigin?.z).catch(() => {});
    } else {
      // バリアント未選択(＝建設のキャンセル・種別変更・タイル再クリックによる解除を含む)
      // になったら必ずグリッドを消す。handleCancel を経由しない解除パスでも
      // グリッドが残らないよう、ここで一元的に OFF を送る。
      sendSetGrid("off").catch(() => {});
    }
  }, [gridMode, gridSize, gridAngle, gridOrigin, pathConstruction.selectedVariant]);

  /* ---- ヒントバー表示 ---- */

  /**
   * パネルが開いたとき、および選択中バリアントの height_editable が変化したときに、
   * ヒントバーに道路建設の操作説明を表示する。
   * height_editable が true の場合は高さ変更のショートカットキー案内を含む。
   * パネルが閉じられたときにヒントをクリアする。
   */
  useEffect(() => {
    let cancelled = false;
    (async () => {
      const hintKey = pathConstruction.heightEditable
        ? "hint.pathConstructionElevated"
        : "hint.pathConstruction";
      await setHintbarText(t(hintKey));
    })().catch((err) => {
      if (!cancelled) {
        console.error("[PathConstruction] setHintbarText failed:", err);
      }
    });
    return () => {
      cancelled = true;
      void setHintbarText("");
    };
  }, [t, pathConstruction.heightEditable]);

  /* ---- マスターデータ未取得時 ---- */

  if (!masterData) {
    return <p className="pc-muted">{t("common.loading")}</p>;
  }

  /* ---- レンダリング ---- */

  return (
    <div className="pc-layout" onClick={handlePanelClick}>
      {/* カテゴリ選択(道路/軌道/インフラ) */}
      <div className="pc-section">
        <div className="pc-category-group">
          {([
            { key: "road", label: t("path_construction.category.road", "道路") },
            { key: "rail", label: t("path_construction.category.rail", "軌道") },
            { key: "infra", label: t("path_construction.category.infra", "インフラ") },
          ] as { key: PathCategory; label: string }[]).map((c) => (
            <button
              key={c.key}
              type="button"
              className={`pc-category-btn ${pathConstruction.category === c.key ? "active" : ""}`}
              disabled={editPathId != null}
              onClick={() => handleCategoryChange(c.key)}
            >
              {c.label}
            </button>
          ))}
        </div>
      </div>

      {/* カテゴリ別バリアント選択 */}
      {pathConstruction.category === "road" && (
        <RoadVariantSelector
          masterData={masterData}
          selectedType={pathConstruction.selectedType}
          selectedVariant={pathConstruction.selectedVariant}
          variantsForType={pathConstruction.variantsForType}
          onSelectType={pathConstruction.onTypeChange}
          onSelectVariant={pathConstruction.selectVariant}
          disabled={editPathId != null}
        />
      )}
      {pathConstruction.category === "infra" && (
        <InfraVariantSelector
          masterData={masterData}
          selectedType={pathConstruction.selectedType}
          selectedVariant={pathConstruction.selectedVariant}
          variantsForType={pathConstruction.variantsForType}
          onSelectType={pathConstruction.onTypeChange}
          onSelectVariant={pathConstruction.selectVariant}
          disabled={editPathId != null}
        />
      )}
      {pathConstruction.category === "rail" && (
        <RailVariantSelector
          masterData={masterData}
          railType={pathConstruction.railType}
          trackType={pathConstruction.trackType}
          electrified={pathConstruction.electrified}
          trackCount={pathConstruction.trackCount}
          trackSpacing={pathConstruction.trackSpacing}
          canWidenSpacing={pathConstruction.canWidenTrackSpacing}
          canShrinkSpacing={pathConstruction.canShrinkTrackSpacing}
          trackDirections={pathConstruction.trackDirections}
          trackTypeOptions={pathConstruction.railTrackTypeOptions}
          electrificationOptions={pathConstruction.railElectrificationOptions}
          railVariantOptions={pathConstruction.railVariantOptions}
          selectedVariant={pathConstruction.selectedVariant}
          onSelectRailType={pathConstruction.selectRailType}
          onSelectTrackType={pathConstruction.selectTrackType}
          onToggleElectrified={pathConstruction.setElectrified}
          onSelectVariant={pathConstruction.selectVariant}
          onChangeTrackCount={pathConstruction.changeTrackCount}
          onAdjustSpacing={pathConstruction.adjustTrackSpacing}
          onCycleDirection={pathConstruction.cycleTrackDirection}
          disabled={editPathId != null}
        />
      )}

      {/* 説明文 */}
      {pathConstruction.selectedVariantData && (
        <div className="pc-section">
          <div className="pc-description-frame">
            <img
              src={resolvePathIcon(pathConstruction.selectedVariantData.type)}
              alt={pathConstruction.selectedVariantData.display_name}
              className="pc-description-icon"
              draggable={false}
            />
            <div className="pc-description-body">
              <div className="pc-description-name">{pathConstruction.selectedVariantData.display_name}</div>
              <div className="pc-description-text">
                {pathConstruction.selectedVariantData.description || "—"}
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
      {pathConstruction.selectedVariant !== null &&
        (pathConstruction.heightEditable || !pathConstruction.isInfrastructure || pathConstruction.hasPlatform) && (
        <div className="pc-section">
          <div className="pc-section-title">{t("path_construction.shape", "形状")}</div>

          {/*
            標高（相対高さ）。height_editable=true のバリアント(高架道路・地下鉄用線路など)のみ表示。
            下限はマスターの min_height。負の値を持つバリアント(地下)だけが高さ<0に建設できる。
          */}
          {pathConstruction.heightEditable && (
            <div className="pc-settings-row">
              <span className="pc-settings-label">{t("path_construction.elevation", "標高 (Y)")}</span>
              <div className="pc-stepper-row">
                <NumberStepper
                  value={pathConstruction.height}
                  onChange={handleHeightChange}
                  min={pathConstruction.selectedVariantData?.min_height ?? 0}
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
          {pathConstruction.heightEditable && (
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
                    className={`pc-platform-side-btn ${pathConstruction.followTerrain === option.value ? "active" : ""}`}
                    onClick={() => pathConstruction.setFollowTerrain(option.value)}
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
          {!pathConstruction.isInfrastructure && pathConstruction.followTerrain !== 0 && (
            <div className="pc-settings-row">
              <span className="pc-settings-label">{t("path_construction.embankmentHeight", "盛土高さ")}</span>
              <div className="pc-stepper-row">
                <NumberStepper
                  value={pathConstruction.embankmentHeight}
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
          {pathConstruction.hasPlatform && (
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
                    className={`pc-platform-side-btn ${pathConstruction.platformSide === option.value ? "active" : ""}`}
                    onClick={() => pathConstruction.setPlatformSide(option.value)}
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
      {pathConstruction.selectedVariant !== null && (
        <div className="pc-section">
          <GridSettingsPanel
            mode={gridMode}
            size={gridSize}
            angle={gridAngle}
            onModeChange={setGridMode}
            onSizeChange={setGridSize}
            onAngleChange={setGridAngle}
            onOriginChange={setGridOrigin}
            originalState="STATE_EDIT_PATH"
            originalExtra={{
              // 複合パス選択中はUI専用の擬似タイプではなく、Unityが解釈できる
              // 代表バリアント(先頭コンポーネント)のタイプ/バリアントを渡す
              path_type: pathConstruction.unityPathType,
              path_variant: pathConstruction.unityPathVariant,
              path_id: editPathId ?? null,
              // 角度設定モードからの復帰でSTATE_EDIT_PATHを再送する際、軌道複線の
              // 平行プレビューが単線に戻らないようオフセット列も渡す(無指定は空リセット扱い)
              lateral_offsets: pathConstruction.railComponents
                ? pathConstruction.railComponents.map((c) => c.lateral_offset ?? 0)
                : [],
            }}
            onStartSetAngleMode={handleStartSetAngleMode}
            onEndSetAngleMode={handleEndSetAngleMode}
          />
        </div>
      )}

      {/* 形状プレビュー（パネル最下部に配置） */}
      {pathConstruction.selectedVariant !== null && (
        <div className="pc-section">
          <div className="pc-section-title">
            {t("building_construction.shapePreview", "形状プレビュー")}
          </div>
          {pathConstruction.nodes.length === 0 || !apiBaseUrl ? (
            <div className="pc-no-nodes-message">
              {t("path_construction.noNodesYet")}
            </div>
          ) : (
            <div className="pc-canvas-section">
              <PathShapeMapPreview
                apiBaseUrl={apiBaseUrl}
                nodes={pathConstruction.nodes}
                curveMode={pathConstruction.curveMode}
                startControl={pathConstruction.startControl}
                endControl={pathConstruction.endControl}
                waypointControls={pathConstruction.waypointControls}
                lateralOffsets={
                  pathConstruction.railComponents
                    ? pathConstruction.railComponents.map((c) => c.lateral_offset ?? 0)
                    : undefined
                }
              />
            </div>
          )}

          {/* 推定建設コスト(詳細パネルと共通のカード。標準コスト倍率はバッジで表示) */}
          {pathConstruction.nodes.length >= 2 && (
            <div style={{ marginTop: 10 }}>
              <ConstructionInfoCard
                estimatedCost={pathConstruction.estimatedCost}
                standardCost={pathConstruction.estimatedStandardCost}
                costRatioHelp={t("path_construction.costRatioHelp")}
                estimatedDurationDays={pathConstruction.estimatedDurationDays}
                isCostLoading={pathConstruction.isCostLoading}
                nextEpochConstructionBudget={pathConstruction.nextEpochConstructionBudget}
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
      {pathConstruction.selectedVariant !== null && (
        <div className="pc-action-bar">
          <div className="pc-actions">
            <button className="pc-btn" type="button" onClick={handleCancel}>
              {t("path_construction.cancel")}
            </button>
            <button
              className="pc-btn pc-btn-confirm"
              disabled={pathConstruction.nodes.length < 2 || isConfirming}
              type="button"
              onClick={handleConfirm}
            >
              {isConfirming ? t("path_construction.confirming") : t("path_construction.confirm")}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
