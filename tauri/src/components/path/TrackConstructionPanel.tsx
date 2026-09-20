/**
 * 軌道建設パネル
 *
 * window_type="track_build" のポップアップウィンドウで表示する専用パネル。
 * 道路建設(PathConstructionPanel)から軌道関連を分離し、上から
 * 「モード切替 → 鉄道種別プルダウン → タブ → 規格プルダウン」の順で対象を絞り込む。
 *
 * パネル全体に簡易モードと詳細モードがあり、タブの構成が変わる。
 *   - 簡易モード(既定): 軌道タブ + 駅タブの2枚。駅タブは「相対式(対面ホーム)の複線 +
 *     駅舎」を1クリックでまとめて置く固定パッケージで、指定できるのはホーム長(両数)・
 *     高さ・角度・駅舎の種類・建屋の配置端・通路方式。プラットフォームを曲げたり本数を変えたりはできない。
 *   - 詳細モード: 軌道 / プラットフォーム / 駅舎 の3枚。従来どおり1本ずつ自由に敷設する。
 *
 * 鉄道種別プルダウン(鉄道/モノレール/人員搬送)はタブより上位の選択で、タブを切り替えても保持する。
 * タブの役割:
 *   - 軌道タブ:        通常(TRACK_NORMAL)・橋梁(TRACK_BRIDGE)の線路を敷設する。
 *   - プラットフォームタブ: 駅(TRACK_STATION)の線路を敷設する(ホーム設置側の指定あり)。
 *   - 駅舎タブ:        駅舎パス(type=PATH_STATION_HOUSE のバリアント)を敷設する。
 *   - 駅タブ(簡易):    上記2つをまとめたパッケージ。形状の組み立てとゴーストはUnityの
 *     StationPlanManager(STATE_EDIT_STATION)が持ち、確定送信は useStationConstruction が行う。
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
 *
 * 駅舎バリアントの敷設中は、Unity側が既存の地物への吸着(ノード/パス/直線延長/直角/
 * 並行・終端揃え)を一切適用しない(RoadPlanManager.SnappingSuppressed)。駅舎の線は
 * 線路・道路と交差してもノードを作らず接続もしないため、吸着すると端点が線路上に
 * 乗って建屋が線路を避けきれなくなる。グリッド吸着だけはプレイヤーが明示的に出した
 * グリッドへの整列なので有効なまま(このパネルのグリッド設定も駅舎タブで使える)。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { StationHouseEndsSelect } from "./StationHouseEndsSelect";
import { StationPassageModeSelect } from "./StationPassageModeSelect";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
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
import { useVariantAffordability } from "../../hooks/useVariantAffordability";
import { RailTrackLayoutSettings } from "./RailTrackLayoutSettings";
import { PathVariantDescription } from "./PathVariantDescription";
import { PathBuildSections } from "./PathBuildSections";
import { StationBuildSections } from "./StationBuildSections";
import { useStationConstruction } from "./useStationConstruction";
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

/** タブ種別。軌道 / プラットフォーム / 駅舎(詳細モード) / 駅(簡易モードのパッケージ)。 */
type TrackTab = "track" | "platform" | "house" | "station";

/** パネル全体のモード。簡易=軌道+駅の2タブ、詳細=軌道+プラットフォーム+駅舎の3タブ。 */
type PanelMode = "simple" | "detail";

export function TrackConstructionPanel({ apiBaseUrl }: TrackConstructionPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  // 軌道は常に新規敷設のため editPathId は null 固定。
  const pc = usePathConstruction(masterData, apiBaseUrl, null);
  // バリアント単位の資金目安(今の資金では平地の基準延長すら敷けない規格を選ばせない)
  const { affordability: variantAffordability, refresh: refreshAffordability } =
    useVariantAffordability("paths", apiBaseUrl);

  const [mode, setMode] = useState<PanelMode>("simple");
  const [tab, setTab] = useState<TrackTab>("track");
  const isStationTab = tab === "station";
  // 鉄道種別(鉄道/モノレール/人員搬送)はタブより上位の絞り込みなので、
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
  // 軌道タブ=通常/橋梁、プラットフォームタブと簡易モードの駅タブ=駅。
  // 構造・電化の違いも候補の一部として並べる。
  const allowedTrackTypes = useMemo(
    () => (tab === "platform" || tab === "station" ? [TRACK_STATION] : [TRACK_NORMAL, TRACK_BRIDGE]),
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

  /* ---- 駅タブ(簡易モード)の状態管理。タブを離れている間は Unity へ何も送らない ---- */
  const sc = useStationConstruction(masterData, apiBaseUrl, isStationTab);

  /* ---- 初回マウント時にカテゴリを軌道へ固定する ---- */
  useEffect(() => {
    pc.onCategoryChange("rail");
    // マウント時に一度だけ実行する(pc.onCategoryChange は安定)。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  /* ---- 駅タブの規格の既定値(プラットフォーム・駅舎とも候補の先頭) ---- */
  useEffect(() => {
    // 他のタブでは variantOptions が通常/橋梁の候補になるため、駅タブ表示中だけ選び直す。
    if (!isStationTab) return;
    if (variantOptions.length > 0
        && (sc.platformVariant == null || !variantOptions.some((v) => v.id === sc.platformVariant))) {
      sc.selectPlatformVariant(variantOptions[0].id);
    }
    if (houseVariantOptions.length > 0
        && (sc.houseVariant == null || !houseVariantOptions.some((v) => v.id === sc.houseVariant))) {
      sc.selectHouseVariant(houseVariantOptions[0].id);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isStationTab, variantOptions, houseVariantOptions, sc.platformVariant, sc.houseVariant]);

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
    // 駅タブは usePathConstruction を使わない(敷設セッションの持ち主が StationPlanManager のため)。
    if (isStationTab) return;
    const options = tab === "house" ? houseVariantOptions : variantOptions;
    if (options.length === 0) return;
    if (pc.selectedVariant != null && options.some((v) => v.id === pc.selectedVariant)) return;
    pc.selectVariant(options[0].id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isStationTab, tab, variantOptions, houseVariantOptions, pc.selectedVariant]);

  /* ---- タブ切り替え ---- */
  const handleTabChange = useCallback(
    (next: TrackTab) => {
      if (next === tab) return;
      if (next === "station") {
        // 駅タブへは pc 側の状態を触らずに移る。useStationConstruction の effect が送る
        // STATE_EDIT_STATION が、Unity 側で進行中のパス敷設セッションごと引き取る。
        setTab(next);
        return;
      }
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

  /* ---- モード切り替え(簡易 ⇔ 詳細) ---- */
  // タブ構成が変わるため、切り替え先に存在しないタブを開いていたら軌道タブへ退避する。
  const handleModeChange = useCallback(
    (next: PanelMode) => {
      if (next === mode) return;
      setMode(next);
      if (next === "simple" && (tab === "platform" || tab === "house")) {
        handleTabChange("track");
      } else if (next === "detail" && tab === "station") {
        handleTabChange("track");
      }
    },
    [mode, tab, handleTabChange],
  );

  /* ---- 軌道パスの確定(常に新規敷設=連続設置モード) ---- */
  const handleConfirmPath = useCallback(async () => {
    setIsConfirming(true);
    try {
      await pc.confirmPath();
      await sendEditPathFinish();
      // 確定した形状の2D編集パネルは役目を終えるので閉じる(開いていなければ何もしない)
      void invoke("close_registered_window", { windowType: "path_shape_editor" }).catch(console.error);
      // 発注で拘束額が増えたので、規格ごとの資金目安を取り直す
      refreshAffordability();
      // 連続設置: 選択中バリアント・軌道設定を維持したまま次のパスを配置できる状態へ戻す。
      pc.continuePlacement();
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
  }, [pc, refreshAffordability, t]);

  /** 軌道パスのキャンセル(配置中の仮形状だけをクリアして同じバリアントで引き直す)。 */
  const handleCancelPath = useCallback(() => {
    pc.continuePlacement();
  }, [pc]);

  /* ---- 駅パッケージの確定(プラットフォーム2本+駅舎1本を一括発注し、連続設置へ戻す) ---- */
  const handleConfirmStation = useCallback(async () => {
    setIsConfirming(true);
    try {
      await sc.confirmStation();
      // Unity 側の配置状態をリセットしてデータを再取得させる(道路敷設と同じ後始末)。
      await sendEditPathFinish();
      refreshAffordability();
      // 設定を保ったまま駅配置モードへ入り直し、続けて次の駅を置けるようにする。
      sc.continuePlacement();
      setIsConfirming(false);
      void showNotification({ message: t("path_construction.placedContinue"), durationMs: 3000 });
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
        displayMessage = err instanceof Error ? err.message : t("path_construction.confirmFailedUnknown");
      }
      void showNotification({ message: displayMessage, durationMs: 8000 });
    }
  }, [sc, refreshAffordability, t]);

  /** 駅パッケージのキャンセル(固定した地点だけを捨てて置き直す。寸法・角度は維持)。 */
  const handleCancelStation = useCallback(() => {
    sc.resetPlacement();
  }, [sc]);

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
        tab === "station"
          ? "track_construction.station.hint"
          : tab === "house"
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

  // タブ構成はモードで変わる。簡易=軌道+駅、詳細=軌道+プラットフォーム+駅舎。
  const tabDefs: { key: TrackTab; label: string }[] = mode === "simple"
    ? [
        { key: "track", label: t("track_construction.trackTab") },
        { key: "station", label: t("track_construction.stationTab") },
      ]
    : [
        { key: "track", label: t("track_construction.trackTab") },
        { key: "platform", label: t("track_construction.platformTab") },
        { key: "house", label: t("track_construction.houseTab") },
      ];

  return (
    <div className="pc-layout" onClick={handlePanelClick}>
      {/* パネル全体のモード切替。タブより上位の選択なので最上段に置く。 */}
      <div className="tc-modebar">
        {([
          { key: "simple", label: t("track_construction.simpleMode") },
          { key: "detail", label: t("track_construction.detailMode") },
        ] as { key: PanelMode; label: string }[]).map((modeDef) => (
          <button
            key={modeDef.key}
            type="button"
            className={`tc-mode-btn ${mode === modeDef.key ? "active" : ""}`}
            onClick={() => handleModeChange(modeDef.key)}
          >
            {modeDef.label}
          </button>
        ))}
      </div>

      {/* 鉄道種別(鉄道 / モノレール / 人員搬送)。タブより上位の絞り込み。
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

      {/* タブバー(モードごとのタブ構成)。意匠は道路建設パネルと共通の pc-tabbar / pc-tab。 */}
      <div className="pc-tabbar" role="tablist">
        {tabDefs.map((tabDef) => (
          <button
            key={tabDef.key}
            type="button"
            role="tab"
            aria-selected={tab === tabDef.key}
            className={`pc-tab ${tab === tabDef.key ? "active" : ""}`}
            onClick={() => handleTabChange(tabDef.key)}
          >
            {tabDef.label}
          </button>
        ))}
      </div>

      {/* 駅タブは「規格 → 駅舎 → 寸法・向き → 費用 → 確定」を専用ボディに持つ。
          形状は固定なので、グリッド設定・複線構成・2D形状編集は出さない。 */}
      {isStationTab ? (
        <>
        <div className="pc-section">
          <StationHouseEndsSelect value={sc.houseEnds} onChange={sc.setHouseEnds} />
          <StationPassageModeSelect value={sc.passageMode} onChange={sc.setPassageMode} />
        </div>
        <StationBuildSections
          sc={sc}
          platformVariantOptions={variantOptions}
          houseVariantOptions={houseVariantOptions}
          affordability={variantAffordability}
          isConfirming={isConfirming}
          onConfirm={() => void handleConfirmStation()}
          onCancel={handleCancelStation}
        />
        </>
      ) : (
        <>
          {/* 規格プルダウン(駅舎タブは駅舎バリアント、それ以外は鉄道種別×構造で絞った軌道バリアント) */}
          <RailVariantSelector
            variantOptions={tab === "house" ? houseVariantOptions : variantOptions}
            selectedVariant={pc.selectedVariant}
            onSelectVariant={pc.selectVariant}
            affordability={variantAffordability}
          />

          <PathVariantDescription entry={pc.selectedVariantData} />
          {tab === "house" && pc.selectedVariant !== null && (
            <div className="pc-section">
              <StationHouseEndsSelect value={pc.houseEnds} onChange={pc.setHouseEnds} />
              <StationPassageModeSelect value={pc.passageMode} onChange={pc.setPassageMode} />
            </div>
          )}

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
        </>
      )}
    </div>
  );
}
