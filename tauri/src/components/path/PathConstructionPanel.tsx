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

import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { ApiError } from "../../utils/api";
import { usePathConstruction } from "./usePathConstruction";
import { sendEditPathFinish, sendSetGrid, sendChangeDisplay, sendChangeState, sendFocusGraphic, IPC_EVENT_VIEW_GRID_TOGGLED } from "../../utils/ipc";
import type { GridDisplayMode } from "./GridSettingsPanel";
import { isInfraPathType } from "../../local-constants";
import { RoadVariantSelector } from "./RoadVariantSelector";
import { InfraVariantSelector } from "./InfraVariantSelector";
import { PathVariantDescription } from "./PathVariantDescription";
import { PathBuildSections } from "./PathBuildSections";
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
      {/* カテゴリ選択(道路/インフラ)。軌道は専用の軌道建設パネル(track_build)へ分離した。 */}
      <div className="pc-section">
        <div className="pc-category-group">
          {([
            { key: "road", label: t("path_construction.category.road", "道路") },
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
