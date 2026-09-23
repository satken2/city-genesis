/**
 * 道路建設パネル / インフラ建設パネル
 *
 * window_type="path_build"(道路)と "infra_build"(インフラ=電気・水道)の2つのポップアップ
 * ウィンドウで共用する専用パネル。どちらのカテゴリを扱うかは `category` prop で固定し、
 * パネル内でカテゴリを切り替える手段は持たない(プレイヤーには道路とインフラは別の道具に
 * 見えるため入口=アイコンパネルのボタンを分けている。入力UIは同一なのでコードは共有する)。
 * 軌道は TrackConstructionPanel(track_build)が担う。
 * BuildingConstructionPanel と同じ単列レイアウト。
 *
 * 操作フロー:
 *   1. 種別タブ(道路: 街路/高速道路/歩道、インフラ: 電気/水道)を選ぶ → バリアントのプルダウンが出る
 *   2. バリアントを選択 → IPCでグラフィックプログラムに敷設モードを通知
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
import { invoke } from "@tauri-apps/api/core";
import { useMasterData } from "../../contexts/MasterDataContext";

import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { ApiError } from "../../utils/api";
import { usePathConstruction } from "./usePathConstruction";
import { useVariantAffordability } from "../../hooks/useVariantAffordability";
import { sendEditPathFinish, sendSetGrid, sendChangeDisplay, sendChangeState, sendFocusGraphic, IPC_EVENT_VIEW_GRID_TOGGLED } from "../../utils/ipc";
import type { GridDisplayMode } from "./GridSettingsPanel";
import { isInfraPathType } from "../../local-constants";
import { RoadVariantSelector } from "./RoadVariantSelector";
import { InfraVariantSelector } from "./InfraVariantSelector";
import { PathVariantDescription } from "./PathVariantDescription";
import { PathBuildSections } from "./PathBuildSections";
import type { PathCategory } from "./usePathConstruction";
import "./PathConstructionPanel.css";

/** このパネルが扱うカテゴリ。軌道("rail")は TrackConstructionPanel の担当なので受け付けない。 */
export type PathConstructionCategory = Exclude<PathCategory, "rail">;

type PathConstructionPanelProps = {
  apiBaseUrl?: string;
  onClose: () => void;
  editPathId?: number | null;
  /** 新規敷設時に固定するカテゴリ。既存パス編集時はパスの種別から決まるため参照しない。 */
  category: PathConstructionCategory;
};

export function PathConstructionPanel({ apiBaseUrl, onClose, editPathId, category }: PathConstructionPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const pathConstruction = usePathConstruction(masterData, apiBaseUrl, editPathId ?? null);
  // バリアント単位の資金目安(今の資金では平地の基準延長すら敷けない規格を選ばせない)
  const { affordability: variantAffordability, refresh: refreshAffordability } =
    useVariantAffordability("paths", apiBaseUrl);

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

  /* ---- カテゴリの固定 ---- */

  /**
   * 新規敷設ではウィンドウ種別で決まるカテゴリをフックに固定する(軌道建設パネルと同じ方式)。
   * 既存パス編集はパス取得時の onTypeChange が種別からカテゴリを補完するため、ここでは触らない。
   */
  useEffect(() => {
    if (editPathId == null) {
      pathConstruction.onCategoryChange(category);
    }
    // マウント時に一度だけ実行する(pathConstruction.onCategoryChange は安定)。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [category, editPathId]);

  /**
   * 表示に使うカテゴリ。既存パス編集ではフック側(種別から補完した値)が正、
   * 新規敷設では prop が正(フックの値は上の useEffect で追従するまで一瞬 null になりうる)。
   */
  const activeCategory: PathCategory | null =
    editPathId != null ? pathConstruction.category : category;

  /* ---- ハンドラ ---- */

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
  }, [editPathId, onClose, pathConstruction, refreshAffordability, t]);

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
    if (isInfraPathType(pathConstruction.selectedType)) { // 送電・導水・パイプライン（PATH_TYPES_INFRA）
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
      {/* カテゴリ別バリアント選択(カテゴリはウィンドウ種別で固定。軌道は track_build が担う) */}
      {activeCategory === "road" && (
        <RoadVariantSelector
          masterData={masterData}
          selectedType={pathConstruction.selectedType}
          selectedVariant={pathConstruction.selectedVariant}
          variantsForType={pathConstruction.variantsForType}
          onSelectType={pathConstruction.onTypeChange}
          onSelectVariant={pathConstruction.selectVariant}
          disabled={editPathId != null}
          affordability={variantAffordability}
        />
      )}
      {activeCategory === "infra" && (
        <InfraVariantSelector
          masterData={masterData}
          selectedType={pathConstruction.selectedType}
          selectedVariant={pathConstruction.selectedVariant}
          variantsForType={pathConstruction.variantsForType}
          onSelectType={pathConstruction.onTypeChange}
          onSelectVariant={pathConstruction.selectVariant}
          disabled={editPathId != null}
          affordability={variantAffordability}
        />
      )}

      {/* 選択中バリアントの説明カード */}
      <PathVariantDescription entry={pathConstruction.selectedVariantData} />

      {/*
        説明カードより下の共通ボディ(形状・グリッド・プレビュー・コスト・確定/キャンセル)は
        軌道建設パネルと共有する PathBuildSections に集約している。
        道路/インフラではホーム設置側トグルは出さない(showPlatformSide=false)。
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
        showPlatformSide={false}
      />
    </div>
  );
}
