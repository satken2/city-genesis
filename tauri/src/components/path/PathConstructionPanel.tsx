/**
 * パス建設パネル
 *
 * window_type="path_build" のポップアップ。道路・鉄道・インフラなど、線を引いて敷くもの(パス)の新規敷設と、
 * 既存パスの形状編集(パス詳細の「編集」が params.extra.editPathId 付きで開く)を1つのパネルで扱う。
 *
 * 選択は建物建設パネルと同じ3段で、左上の戻るで1段ずつ戻る。
 *   1. カテゴリのタイル: カテゴリは path_type そのもの(local-constants の PATH_BUILD_CATEGORIES)
 *   2. バリアントのカード一覧: 並び順は pathBuildCategories が決める(鉄道・モノレールは末尾に駅舎)
 *   3. 建設画面: 選んだバリアントのカードを概要として先頭に出し、その下に敷設の設定と確定/キャンセルを並べる
 * 既存パスの形状編集は種別・バリアントを変えないので、建設画面から始まり戻れない。
 *
 * 建設画面の操作フロー:
 *   1. カードを選ぶ → IPCでグラフィックプログラムに敷設モード(STATE_EDIT_PATH)を通知
 *   2. グラフィックプログラム側でノードを配置 → IPCでノード情報がパネルに通知される
 *   3. [確定] でバックエンドに送信(新規敷設は連続設置で、同じバリアントのまま次を敷ける)
 *
 * 建設画面の設定は選んだバリアントの種別で決まる。軌道(線路・モノレール)は線路数・進行方向・線路間隔と
 * 駅のホーム設置側、駅舎は建屋の配置端と通路方式、街路・高速道路は所属道路を持つ。これらと建設プロジェクトの
 * タグは新規敷設でだけ選ぶ(建設後は変えられないか、パス詳細で変える)。
 * 曲線/直線・吸着の設定はUnity側の操作系(Ctrlラジアルメニュー・カーソルHUD)が担い、このパネルには置かない。
 * 駅舎の敷設中はUnityが既存の道路・線路への吸着を止める(グリッド吸着だけは有効)。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { invoke } from "@tauri-apps/api/core";
import { emit } from "@tauri-apps/api/event";
import { useMasterData } from "../../contexts/MasterDataContext";

import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { ApiError, fetchApi } from "../../utils/api";
import { resolvePathCategoryIcon } from "../../utils/pathCategoryIcon";
import { usePathConstruction } from "./usePathConstruction";
import { useVariantAffordability } from "../../hooks/useVariantAffordability";
import { sendEditPathFinish, sendSetGrid, sendChangeDisplay, sendChangeState, sendFocusGraphic, IPC_EVENT_VIEW_GRID_TOGGLED } from "../../utils/ipc";
import type { GridDisplayMode } from "./GridSettingsPanel";
import { isInfraPathType } from "../../local-constants";
import { PATH_STATION_HOUSE } from "../../constants";
import { tutorialVariantAllowed } from "../../tutorial/TutorialLayer";
import { availableBuildCategories, buildCategoryVariants } from "./pathBuildCategories";
import { isRailType } from "./railBuild";
import { PathVariantCard } from "./PathVariantCard";
import { RailTrackLayoutSettings } from "./RailTrackLayoutSettings";
import { StationHouseEndsSelect } from "./StationHouseEndsSelect";
import { StationPassageModeSelect } from "./StationPassageModeSelect";
import { PathBuildSections } from "./PathBuildSections";
import { PathRoadAssignmentSection } from "./PathRoadAssignmentSection";
import { PathTagSection } from "./PathTagSection";
import { TAGS_CHANGED_EVENT } from "../../utils/constructionTags";
import { usePathRoadAssignment } from "./usePathRoadAssignment";
import { isNamedRoadPathType } from "../road/roadApi";
import turnBackIcon from "../../assets/images/ui/turn-back.png";
import "./PathConstructionPanel.css";
import "../building/BuildingConstructionPanel.css";

type PathConstructionPanelProps = {
  apiBaseUrl?: string;
  onClose: () => void;
  editPathId?: number | null;
};

export function PathConstructionPanel({ apiBaseUrl, onClose, editPathId }: PathConstructionPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const isNewBuild = editPathId == null;
  const pathConstruction = usePathConstruction(masterData, apiBaseUrl, editPathId ?? null);
  // バリアント単位の資金目安(今の資金では平地の基準延長すら敷けない規格を選ばせない)
  const { affordability: variantAffordability, refresh: refreshAffordability } =
    useVariantAffordability("paths", apiBaseUrl);
  const isVariantUnaffordable = (variantId: number) =>
    variantAffordability != null
    && !variantAffordability.unlimited
    && variantAffordability.variants[variantId]?.affordable === false;

  // カード一覧を表示しているカテゴリ(path_type)。null のときはカテゴリのタイルを表示する。
  const [selectedCategory, setSelectedCategory] = useState<number | null>(null);

  const selectedVariantData = pathConstruction.selectedVariantData;
  const selectedPathType = selectedVariantData?.type ?? null;

  /* ---- 新規敷設: 開いた時点でUnityの編集状態を通常へ戻す ---- */
  // バリアントを選ぶまでは敷設モードに入らないので、直前のモード(ブルドーザなど)を残さない。
  useEffect(() => {
    if (isNewBuild) pathConstruction.deselectVariant();
    // マウント時に一度だけ実行する(deselectVariant は安定)。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isNewBuild]);

  /* ---- 既存パス編集モード: データ取得 ---- */
  // バリアントの種別はマスターから引いて敷設モードへ渡すので、マスターの取得を待ってから選ぶ。
  const masterDataLoaded = masterData != null;
  useEffect(() => {
    if (editPathId == null || !apiBaseUrl || !masterDataLoaded) return;
    const controller = new AbortController();

    fetchApi<{ path_variant: number }>(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/${editPathId}`, {
      signal: controller.signal,
    })
      .then((data) => {
        if (controller.signal.aborted) return;
        pathConstruction.selectVariant(data.path_variant);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          console.error("[PathConstructionPanel] Failed to fetch existing path:", err);
        }
      });

    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [editPathId, apiBaseUrl, masterDataLoaded]);

  /* ---- グリッド設定状態（デフォルト非表示） ---- */
  const [gridMode, setGridMode] = useState<GridDisplayMode>("off");
  const [gridSize, setGridSize] = useState(20);
  const [gridAngle, setGridAngle] = useState(0);
  const [gridOrigin, setGridOrigin] = useState<{ x: number; z: number } | null>(null);

  /* ---- 確定処理中状態 ---- */
  const [isConfirming, setIsConfirming] = useState(false);

  /* ---- 敷くパスに付ける建設プロジェクトのタグ(空文字=付けない)。連続設置の間は保持する ---- */
  const [projectTag, setProjectTag] = useState("");

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

  /* ---- カテゴリとバリアントの候補 ---- */
  // タイル・カードの選択可否はチュートリアルの案内状態にも依存する。案内状態が変わると
  // usePathConstruction(内部で useGuideSnapshot を購読)がパネルを描き直すので、描画のたびに判定すればよい。
  const availableCategories = useMemo(() => availableBuildCategories(masterData), [masterData]);
  const variantsForCategory = useMemo(
    () => (selectedCategory === null ? [] : buildCategoryVariants(masterData, selectedCategory)),
    [masterData, selectedCategory],
  );

  /**
   * 所属道路の選択は、街路・高速道路の新規敷設のときだけ扱う(既存パスの形状編集では所属を変えない。
   * 所属の変更はパス詳細・道路詳細で行う)。
   */
  const roadAssignmentEnabled = isNewBuild && pathConstruction.selectedVariant !== null && isNamedRoadPathType(selectedPathType);
  const roadAssignment = usePathRoadAssignment(apiBaseUrl, roadAssignmentEnabled, pathConstruction.rawPoints);

  /* ---- ハンドラ ---- */

  /**
   * パネル全体のクリックイベントを捕捉する共通のハンドラ。
   * ボタン要素やカードなど、ユーザーが明示的に対話する要素をクリックした際に、
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

  /** バリアントのカードを選ぶ。チュートリアルが敷くバリアントを指定している間は、それ以外を選ばせない。 */
  const handleVariantSelect = useCallback((variantId: number) => {
    if (!tutorialVariantAllowed(variantId)) return;
    pathConstruction.selectVariant(variantId);
  }, [pathConstruction]);

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
      const createdPathIds = await pathConstruction.confirmPath({
        ...roadAssignment.postFields,
        ...(projectTag ? { tag: projectTag } : {}),
      });
      if (projectTag && createdPathIds.length > 0) {
        void emit(TAGS_CHANGED_EVENT, { tag: projectTag }).catch(console.error);
      }
      // 道路の作成は敷設とは別の要求なので、失敗しても敷設の成功は取り消さずに知らせるだけにする。
      await roadAssignment.finalize(createdPathIds).catch((err) => {
        console.error("[PathConstructionPanel] road assignment failed:", err);
        void showNotification({ message: t("path_construction.road.createFailed"), durationMs: 8000 });
      });
      // 成功時: Unityに編集終了を通知してデータをリロードさせる
      await sendEditPathFinish();
      // 確定した形状の2D編集パネルは役目を終えるので閉じる(開いていなければ何もしない)
      void invoke("close_registered_window", { windowType: "path_shape_editor" }).catch(console.error);
      // 発注で拘束額が増えたので、規格ごとの資金目安を取り直す
      refreshAffordability();
      if (editPathId != null) {
        // 既存パス編集: 確定したらパネルを閉じる
        // 先に STATE_NORMAL に戻してから表示モードを切り替える。
        // STATE_NORMAL でないと view:change_view の変更が _normalStateMemory に記憶されず、
        // 後から Rust 側が STATE_NORMAL を送った際に default に戻ってしまうため。
        await sendChangeState("STATE_NORMAL");
        await sendSetGrid("off");
        // 道路を作成したので、ユーザーが建設結果を確認できるよう計画中表示をONにする。
        await sendChangeDisplay("road", "default", null, ["plan"]);
        onClose();
      } else {
        // 新規敷設: 連続設置モード。バリアント選択を維持して次のパスを配置できるようにする。
        pathConstruction.continuePlacement();
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
  }, [editPathId, onClose, pathConstruction, projectTag, refreshAffordability, roadAssignment, t]);

  /**
   * キャンセルボタンを押した際のハンドラ。
   * バリアント選択は解除せず、Unity側に配置中の仮形状(まだ確定していない
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
    if (isInfraPathType(selectedPathType)) { // 送電・導水・パイプライン（PATH_TYPES_INFRA）
      setGridMode("off");
    }
  }, [selectedPathType]);

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
      // バリアント未選択(＝戻るボタンによる選択解除を含む)になったら必ずグリッドを消す。
      // handleCancel を経由しない解除パスでもグリッドが残らないよう、ここで一元的に OFF を送る。
      sendSetGrid("off").catch(() => {});
    }
  }, [gridMode, gridSize, gridAngle, gridOrigin, pathConstruction.selectedVariant]);

  /* ---- ヒントバー表示 ---- */

  /**
   * パネルが開いたとき、および選択中バリアントが変わったときに、ヒントバーに敷設の操作説明を表示する。
   * 駅舎は線とプラットフォームの関係の説明、height_editable のバリアントは高さ変更のショートカットキー案内を含む。
   * パネルが閉じられたときにヒントをクリアする。
   */
  const hintKey = selectedPathType === PATH_STATION_HOUSE
    ? "path_construction.stationHouseHint"
    : pathConstruction.heightEditable
      ? "hint.pathConstructionElevated"
      : "hint.pathConstruction";
  useEffect(() => {
    let cancelled = false;
    setHintbarText(t(hintKey)).catch((err) => {
      if (!cancelled) {
        console.error("[PathConstruction] setHintbarText failed:", err);
      }
    });
    return () => {
      cancelled = true;
      void setHintbarText("");
    };
  }, [t, hintKey]);

  /* ---- マスターデータ未取得時 ---- */

  if (!masterData) {
    return <p className="pc-muted">{t("common.loading")}</p>;
  }

  /* ---- レンダリング ---- */

  if (selectedVariantData === null) {
    // 既存パスの編集はバリアントを読み込んだら建設画面から始まる
    if (!isNewBuild) {
      return <p className="pc-muted">{t("common.loading")}</p>;
    }
    return (
      <div className="pc-layout" onClick={handlePanelClick}>
        <div className="pc-section">
          {selectedCategory === null ? (
            <div className="bc-tile-grid">
              {availableCategories.map((category) => (
                <CategoryTile
                  key={category}
                  icon={resolvePathCategoryIcon(category)}
                  name={t(`path_construction.category.${category}`)}
                  disabled={!buildCategoryVariants(masterData, category).some((v) => tutorialVariantAllowed(v.id))}
                  onClick={() => setSelectedCategory(category)}
                />
              ))}
            </div>
          ) : (
            <>
              <div className="bc-selection-header">
                <BackButton onClick={() => setSelectedCategory(null)} />
              </div>
              <div className="bc-variant-card-list">
                {variantsForCategory.map(({ id, data }) => {
                  const unaffordable = isVariantUnaffordable(id);
                  return (
                    <PathVariantCard
                      key={id}
                      entry={data}
                      disabled={unaffordable || !tutorialVariantAllowed(id)}
                      titleSuffix={unaffordable ? t("construction.variantUnaffordableSuffix") : undefined}
                      onClick={() => handleVariantSelect(id)}
                    />
                  );
                })}
              </div>
            </>
          )}
        </div>
      </div>
    );
  }

  return (
    <div className="pc-layout" onClick={handlePanelClick}>
      {/* 選択中のバリアントの概要。戻るとカテゴリのカード一覧へ戻る */}
      <div className="pc-section">
        {isNewBuild && <BackButton onClick={pathConstruction.deselectVariant} />}
        <PathVariantCard entry={selectedVariantData} />
      </div>

      {/* 駅舎の建屋の配置端と通路方式は建設時に決める(配置端は着工後に変えられない) */}
      {isNewBuild && selectedPathType === PATH_STATION_HOUSE && (
        <div className="pc-section">
          <StationHouseEndsSelect value={pathConstruction.houseEnds} onChange={pathConstruction.setHouseEnds} />
          <StationPassageModeSelect value={pathConstruction.passageMode} onChange={pathConstruction.setPassageMode} />
        </div>
      )}

      {/* 線路数・進行方向・線路間隔は軌道の複線構成専用(軌道以外・人員搬送では何も描かない) */}
      {isNewBuild && (
        <RailTrackLayoutSettings
          variant={selectedVariantData}
          trackCount={pathConstruction.trackCount}
          trackSpacing={pathConstruction.trackSpacing}
          canWidenSpacing={pathConstruction.canWidenTrackSpacing}
          canShrinkSpacing={pathConstruction.canShrinkTrackSpacing}
          trackDirections={pathConstruction.trackDirections}
          onChangeTrackCount={pathConstruction.changeTrackCount}
          onAdjustSpacing={pathConstruction.adjustTrackSpacing}
          onCycleDirection={pathConstruction.cycleTrackDirection}
        />
      )}

      {roadAssignmentEnabled && (
        <PathRoadAssignmentSection assignment={roadAssignment} disabled={isConfirming} />
      )}

      {/* 既存パスの形状編集ではタグを変えない(タグの付け替えはパス詳細で行う)。 */}
      {isNewBuild && (
        <PathTagSection
          apiBaseUrl={apiBaseUrl ?? ""}
          value={projectTag}
          onChange={setProjectTag}
          disabled={isConfirming}
        />
      )}

      {/*
        説明カードより下の共通ボディ(形状・グリッド・コスト・確定/キャンセル)。
        ホーム設置側は建設時にしか指定できないので、軌道の新規敷設でだけ出す(駅のバリアントに限られる)。
      */}
      <PathBuildSections
        pc={pathConstruction}
        editPathId={editPathId ?? null}
        gridMode={gridMode}
        gridSize={gridSize}
        gridAngle={gridAngle}
        onGridModeChange={setGridMode}
        onGridSizeChange={setGridSize}
        onGridAngleChange={setGridAngle}
        onGridOriginChange={setGridOrigin}
        onStartSetAngleMode={handleStartSetAngleMode}
        onEndSetAngleMode={handleEndSetAngleMode}
        isConfirming={isConfirming}
        onConfirm={handleConfirm}
        onCancel={handleCancel}
        showPlatformSide={isNewBuild && isRailType(selectedPathType)}
      />
    </div>
  );
}

/** カテゴリのタイル。アイコン画像が無いものは枠だけ空けておく(意匠は建物建設パネルの分類タイルと共通)。 */
function CategoryTile({ icon, name, disabled, onClick }: {
  icon: string;
  name: string;
  disabled: boolean;
  onClick: () => void;
}) {
  return (
    <button className="bc-tile" type="button" data-guide="path.type" disabled={disabled} onClick={onClick}>
      <span className="bc-tile-icon">{icon && <img src={icon} alt="" draggable={false} />}</span>
      <span className="bc-tile-name">{name}</span>
    </button>
  );
}

/** 1段前の選択へ戻るボタン(意匠は建物建設パネルと共通)。 */
function BackButton({ onClick }: { onClick: () => void }) {
  const { t } = useTranslation();
  return (
    <button className="bc-header-btn bc-back-btn" type="button" onClick={onClick}>
      <img src={turnBackIcon} alt="" className="bc-header-btn-icon" draggable={false} />
      <span>{t("path_construction.back")}</span>
    </button>
  );
}
