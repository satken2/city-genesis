/**
 * 軌道建設パネル
 *
 * window_type="track_build" のポップアップウィンドウで表示する専用パネル。
 * 道路建設(PathConstructionPanel)から軌道関連を分離し、上から
 * 「鉄道種別プルダウン → タブ → 規格プルダウン」の3段で対象を絞り込む。
 *
 * 鉄道種別プルダウン(鉄道/モノレール/ロープウェイ)はタブより上位の選択で、タブを切り替えても保持する。
 * タブは3つ:
 *   - 軌道タブ:        通常(TRACK_NORMAL)・橋梁(TRACK_BRIDGE)の線路を敷設する。
 *   - プラットフォームタブ: 駅(TRACK_STATION)の線路を敷設する(ホーム設置側の指定あり)。
 *   - 駅舎タブ:        駅舎パス(type=PATH_STATION_HOUSE のバリアント)を敷設する。
 *
 * 軌道・プラットフォームタブは usePathConstruction をカテゴリ="rail"に固定して駆動し、
 * 許可する track_type だけが異なる。構造(通常/橋梁)・電化/非電化は独立した設定ではなく
 * 「規格」プルダウンの選択肢(=バリアント)として並べ、選んだバリアントが唯一の正となる。
 * 規格選択より下の共通ボディ(形状・グリッド・プレビュー・コスト・確定/キャンセル)は
 * 道路建設パネルと共有する PathBuildSections に集約している。
 *
 * 駅舎タブも通常のパス敷設フロー(STATE_EDIT_PATH → confirmPath)に乗る。駅舎は
 * ウェイポイント付き折れ線のパスで、線と交差したプラットフォームがその駅舎の駅に
 * 所属する。鉄道種別に依存しないため種別プルダウンは表示せず、複線展開・ホーム設置側も
 * 無い(カテゴリ=null のまま単一パスとして確定する)。専有エリア(建屋)の位置は
 * バックエンドとUnityが線形・周辺線路から同一ロジックで導出するため、パネル側の
 * 追加入力は無い。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useMasterData } from "../../contexts/MasterDataContext";

import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { ApiError } from "../../utils/api";
import { usePathConstruction } from "./usePathConstruction";
import {
  sendEditPathFinish,
  sendSetGrid,
  sendChangeState,
  sendFocusGraphic,
  IPC_EVENT_VIEW_GRID_TOGGLED,
} from "../../utils/ipc";
import type { GridDisplayMode } from "./GridSettingsPanel";
import type { PathMasterEntry } from "../../types";
import { RailVariantSelector } from "./RailVariantSelector";
import { RailTrackLayoutSettings } from "./RailTrackLayoutSettings";
import { PathVariantDescription } from "./PathVariantDescription";
import { PathBuildSections } from "./PathBuildSections";
import { availableRailTypes, railVariantsForTrackTypes } from "./railBuild";
import {
  TRACK_NORMAL,
  TRACK_BRIDGE,
  TRACK_STATION,
  PATH_STATION_HOUSE,
} from "../../constants";
import "./PathConstructionPanel.css";
import "./TrackConstructionPanel.css";

type TrackConstructionPanelProps = {
  apiBaseUrl?: string;
  // App からウィンドウ閉鎖ハンドラを受け取るが、軌道建設は連続設置のためこのパネル自身から
  // 閉じることはない(ウィンドウ右上の×で閉じる)。API整合のため型には残す。
  onClose: () => void;
};

/** タブ種別。軌道 / プラットフォーム / 駅舎。 */
type TrackTab = "track" | "platform" | "house";

export function TrackConstructionPanel({ apiBaseUrl }: TrackConstructionPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  // 軌道は常に新規敷設のため editPathId は null 固定。
  const pc = usePathConstruction(masterData, apiBaseUrl, null);

  const [tab, setTab] = useState<TrackTab>("track");
  // 鉄道種別(鉄道/モノレール/ロープウェイ)はタブより上位の絞り込みなので、
  // タブ切り替えで選択が失われないようパネル側で保持する。
  const [railType, setRailType] = useState<number | null>(null);

  /* ---- グリッド設定状態（デフォルト非表示。道路建設パネルと同じ挙動） ---- */
  const [gridMode, setGridMode] = useState<GridDisplayMode>("off");
  const [gridSize, setGridSize] = useState(20);
  const [gridAngle, setGridAngle] = useState(0);
  const [gridOrigin, setGridOrigin] = useState<{ x: number; z: number } | null>(null);

  const [isConfirming, setIsConfirming] = useState(false);

  /* ---- 角度設定モード中のグリッド表示状態の退避・復元(道路建設パネルと同一) ---- */
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

  /**
   * パネル全体のクリックを捕捉し、ボタン操作直後にUnityへフォーカスを移す
   * (道路建設パネルと同一。数値入力欄はフォーカス維持のため対象外)。
   */
  const handlePanelClick = useCallback((e: React.MouseEvent<HTMLDivElement>) => {
    const target = e.target as HTMLElement;
    const button = target.closest("button, [role='button']");
    if (button) {
      sendFocusGraphic().catch(() => {});
    }
  }, []);

  /* ---- 鉄道種別・タブから規格プルダウンの候補を絞り込む ---- */
  // 軌道タブ=通常/橋梁、プラットフォームタブ=駅。構造・電化の違いも候補の一部として並べる。
  const allowedTrackTypes = useMemo(
    () => (tab === "platform" ? [TRACK_STATION] : [TRACK_NORMAL, TRACK_BRIDGE]),
    [tab],
  );
  const railTypeOptions = useMemo(() => availableRailTypes(masterData), [masterData]);
  const variantOptions = useMemo(
    () => (railType == null ? [] : railVariantsForTrackTypes(masterData, railType, allowedTrackTypes)),
    [masterData, railType, allowedTrackTypes],
  );

  // 駅舎タブの候補(path_master の type=PATH_STATION_HOUSE)。鉄道種別に依存しないため
  // 種別プルダウンでは絞り込まず、ID昇順で全駅舎バリアントを並べる。
  const houseVariantOptions = useMemo(() => {
    if (!masterData?.paths) return [];
    const matches: { id: number; data: PathMasterEntry }[] = [];
    for (const key in masterData.paths) {
      const entry = masterData.paths[key];
      if (entry.type === PATH_STATION_HOUSE) {
        matches.push({ id: parseInt(key), data: entry });
      }
    }
    matches.sort((a, b) => a.id - b.id);
    return matches;
  }, [masterData]);

  /* ---- 初回マウント時にカテゴリを軌道へ固定する ---- */
  useEffect(() => {
    pc.onCategoryChange("rail");
    // マウント時に一度だけ実行する(pc.onCategoryChange は安定)。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  /* ---- 鉄道種別の既定値(マスターに存在する先頭の種別=鉄道) ---- */
  useEffect(() => {
    if (railType == null && railTypeOptions.length > 0) {
      setRailType(railTypeOptions[0]);
    }
  }, [railType, railTypeOptions]);

  /* ---- 選択中の規格が現在の候補から外れたら、候補の先頭を選び直す ---- */
  // 鉄道種別の変更・タブ切り替え(=許可する構造の変更)で候補が入れ替わったときに、
  // 敷設対象を必ず候補内のバリアントへ揃える。駅舎タブは駅舎バリアントの候補で同様に行う。
  useEffect(() => {
    const options = tab === "house" ? houseVariantOptions : variantOptions;
    if (options.length === 0) return;
    if (pc.selectedVariant != null && options.some((v) => v.id === pc.selectedVariant)) return;
    pc.selectVariant(options[0].id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab, variantOptions, houseVariantOptions, pc.selectedVariant]);

  /* ---- タブ切り替え ---- */
  const handleTabChange = useCallback(
    (next: TrackTab) => {
      if (next === tab) return;
      if (next === "house") {
        // 駅舎は鉄道種別に依存せず複線展開もしないため、カテゴリを外して
        // 単一パス敷設として扱う(選択リセット後、上の useEffect が候補の先頭を選ぶ)。
        pc.onCategoryChange(null);
      } else {
        // 軌道タブ(通常/橋梁)⇔プラットフォームタブ(駅)の切替。
        // 規格の候補集合が変わるため選択をリセットし、上の useEffect が新しい候補の先頭を選び直す。
        pc.onCategoryChange("rail");
      }
      setTab(next);
    },
    [tab, pc],
  );

  /* ---- 軌道パスの確定(常に新規敷設=連続設置モード) ---- */
  const handleConfirmPath = useCallback(async () => {
    setIsConfirming(true);
    try {
      await pc.confirmPath();
      await sendEditPathFinish();
      // 連続設置: 選択中バリアント・軌道設定を維持したまま次のパスを配置できる状態へ戻す。
      pc.continuePlacement(true);
      setIsConfirming(false);
      void showNotification({
        message: t("path_construction.placedContinue"),
        durationMs: 3000,
      });
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
  }, [pc, t]);

  /** 軌道パスのキャンセル(配置中の仮形状だけをクリアして同じバリアントで引き直す)。 */
  const handleCancelPath = useCallback(() => {
    pc.continuePlacement();
  }, [pc]);

  /* ---- インフラと違い軌道はグリッド自動オフ不要。破棄時にグリッドと編集モードを片付ける ---- */
  useEffect(() => {
    return () => {
      sendSetGrid("off").catch(() => {});
      // 敷設モードのまま閉じても Unity を確実に通常へ戻す(ウィンドウ閉鎖時のRust側復帰の保険)。
      sendChangeState("STATE_NORMAL", {}, "track_build").catch(() => {});
    };
  }, []);

  /* ---- Unity側ラジアルメニューのグリッドトグルと表示状態を同期(道路建設パネルと同一) ---- */
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
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
    };
  }, []);

  /* ---- グリッド設定変更時に Unity に通知(道路建設パネルと同一) ---- */
  useEffect(() => {
    if (pc.selectedVariant !== null) {
      sendSetGrid(gridMode, gridSize, gridAngle, gridOrigin?.x, gridOrigin?.z).catch(() => {});
    } else {
      sendSetGrid("off").catch(() => {});
    }
  }, [gridMode, gridSize, gridAngle, gridOrigin, pc.selectedVariant]);

  /* ---- ヒントバー表示 ---- */
  useEffect(() => {
    let cancelled = false;
    (async () => {
      const hintKey =
        tab === "house"
          ? "track_construction.house.hint"
          : pc.heightEditable
            ? "hint.pathConstructionElevated"
            : "hint.pathConstruction";
      await setHintbarText(t(hintKey));
    })().catch((err) => {
      if (!cancelled) {
        console.error("[TrackConstruction] setHintbarText failed:", err);
      }
    });
    return () => {
      cancelled = true;
      void setHintbarText("");
    };
  }, [t, tab, pc.heightEditable]);

  if (!masterData) {
    return <p className="pc-muted">{t("common.loading")}</p>;
  }

  return (
    <div className="pc-layout" onClick={handlePanelClick}>
      {/* 鉄道種別(鉄道 / モノレール / ロープウェイ)。タブより上位の絞り込み。
          駅舎は鉄道種別に依存しないため、駅舎タブでは表示しない。 */}
      {tab !== "house" && (
        <div className="tc-railtype-bar">
          <select
            className="pc-select tc-railtype-select"
            value={railType ?? ""}
            onChange={(e) => {
              const val = e.target.value;
              if (val) setRailType(parseInt(val));
            }}
          >
            {railTypeOptions.map((type) => (
              <option key={type} value={type}>
                {masterData.path_types[type]?.display_name ?? `Type ${type}`}
              </option>
            ))}
          </select>
        </div>
      )}

      {/* タブバー(軌道 / プラットフォーム / 駅舎) */}
      <div className="tc-tabbar">
        {([
          { key: "track", label: t("track_construction.trackTab", "軌道") },
          { key: "platform", label: t("track_construction.platformTab", "プラットフォーム") },
          { key: "house", label: t("track_construction.houseTab", "駅舎") },
        ] as { key: TrackTab; label: string }[]).map((tabDef) => (
          <button
            key={tabDef.key}
            type="button"
            className={`tc-tab ${tab === tabDef.key ? "active" : ""}`}
            onClick={() => handleTabChange(tabDef.key)}
          >
            {tabDef.label}
          </button>
        ))}
      </div>

      {/* 規格プルダウン(駅舎タブは駅舎バリアント、それ以外は鉄道種別×構造で絞った軌道バリアント) */}
      <RailVariantSelector
        variantOptions={tab === "house" ? houseVariantOptions : variantOptions}
        selectedVariant={pc.selectedVariant}
        onSelectVariant={pc.selectVariant}
      />

      <PathVariantDescription entry={pc.selectedVariantData} />

      {/* 線路数・進行方向・線路間隔は軌道の複線構成専用(駅舎は常に単一の折れ線) */}
      {tab !== "house" && (
        <RailTrackLayoutSettings
          railType={railType}
          trackCount={pc.trackCount}
          trackSpacing={pc.trackSpacing}
          canWidenSpacing={pc.canWidenTrackSpacing}
          canShrinkSpacing={pc.canShrinkTrackSpacing}
          trackDirections={pc.trackDirections}
          onChangeTrackCount={pc.changeTrackCount}
          onAdjustSpacing={pc.adjustTrackSpacing}
          onCycleDirection={pc.cycleTrackDirection}
        />
      )}

      <PathBuildSections
        pc={pc}
        apiBaseUrl={apiBaseUrl}
        editPathId={null}
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
        onConfirm={() => void handleConfirmPath()}
        onCancel={handleCancelPath}
        showPlatformSide={tab === "platform"}
      />
    </div>
  );
}
