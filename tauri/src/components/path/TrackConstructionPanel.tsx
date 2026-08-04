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
 *   - 駅舎タブ:        駅(近接するプラットフォーム群)に駅舎モデルを設置する。
 *
 * 軌道・プラットフォームタブは usePathConstruction をカテゴリ="rail"に固定して駆動し、
 * 許可する track_type だけが異なる。構造(通常/橋梁)・電化/非電化は独立した設定ではなく
 * 「規格」プルダウンの選択肢(=バリアント)として並べ、選んだバリアントが唯一の正となる。
 * 規格選択より下の共通ボディ(形状・グリッド・プレビュー・コスト・確定/キャンセル)は
 * 道路建設パネルと共有する PathBuildSections に集約している。
 *
 * 駅舎タブは2段階(Unityプレビュー → Tauri確定)で設置する:
 *   1. モデルを選ぶと即座に STATE_EDIT_STATION へ入る(house_variant を渡す)。
 *      選択をプレースホルダへ戻すと設置モードを抜ける。
 *   2. Unityがカーソル追従ゴーストを最寄りのプラットフォームにスナップして描画し、
 *      クリックで候補位置(edit_station:update_candidate)を通知してくる。
 *   3. 候補の path_id から所属駅を解決して表示し、[確定]で
 *      PATCH /api/v1/stations/{id} に駅舎の設置情報を送る。
 *   4. 成功後は edit_station:finish で後始末し、STATE_NORMAL に戻す。用地確保中(house_pending)の
 *      ときは、次エポック後に駅舎が現れる旨を通知する。
 *
 * このパネル自身の STATE_NORMAL / STATE_EDIT_STATION 送信は windowType="track_build" で行う
 * (usePathConstruction の内部 STATE_EDIT_PATH 送信は Unity の敷設機構をそのまま再利用するため
 * "path_build" のままで問題ない —— アクション名で Unity が解釈し、送信元ウィンドウ種別には依存しない)。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useMasterData } from "../../contexts/MasterDataContext";

import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { usePathConstruction } from "./usePathConstruction";
import {
  sendEditPathFinish,
  sendSetGrid,
  sendChangeState,
  sendFocusGraphic,
  sendEditStationChangeVariant,
  sendEditStationFinish,
  IPC_EVENT_VIEW_GRID_TOGGLED,
  IPC_EVENT_EDIT_STATION_UPDATE_CANDIDATE,
} from "../../utils/ipc";
import type { GridDisplayMode } from "./GridSettingsPanel";
import { RailVariantSelector } from "./RailVariantSelector";
import { RailTrackLayoutSettings } from "./RailTrackLayoutSettings";
import { PathVariantDescription } from "./PathVariantDescription";
import { PathBuildSections } from "./PathBuildSections";
import { availableRailTypes, railVariantsForTrackTypes } from "./railBuild";
import { TRACK_NORMAL, TRACK_BRIDGE, TRACK_STATION } from "../../constants";
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

/** Unityから受信する駅舎の候補位置。 */
type StationHouseCandidate = {
  path_id: number;
  position: [number, number];
  rotation: number;
  house_variant: number;
};

/** GET /api/v1/stations の要素(このパネルは path_id から所属駅を引くためだけに使う)。 */
type StationSummary = {
  station_id: number;
  name: string;
  paths: { path_id: number }[];
};

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

  /* ---- 駅舎設置の状態 ---- */
  const [houseVariant, setHouseVariant] = useState<number | null>(null);
  const [placing, setPlacing] = useState(false);
  const [candidate, setCandidate] = useState<StationHouseCandidate | null>(null);
  const [resolvedStation, setResolvedStation] = useState<{ station_id: number; name: string } | null>(null);
  const [isConfirmingHouse, setIsConfirmingHouse] = useState(false);

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
  // 敷設対象を必ず候補内のバリアントへ揃える。
  useEffect(() => {
    if (tab === "house" || variantOptions.length === 0) return;
    if (pc.selectedVariant != null && variantOptions.some((v) => v.id === pc.selectedVariant)) return;
    pc.selectVariant(variantOptions[0].id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab, variantOptions, pc.selectedVariant]);

  /* ---- 駅舎設置を終了して STATE_NORMAL に戻す ---- */
  // モデル選択が設置モードの入口を兼ねるため、終了時はプルダウンもプレースホルダへ戻す
  // (同じモデルを選び直しても onChange が発火して再入場できるようにするため)。
  const endHousePlacement = useCallback(() => {
    setPlacing(false);
    setCandidate(null);
    setResolvedStation(null);
    setHouseVariant(null);
    void sendChangeState("STATE_NORMAL", {}, "track_build");
  }, []);

  /* ---- タブ切り替え ---- */
  const handleTabChange = useCallback(
    (next: TrackTab) => {
      if (next === tab) return;
      // 駅舎タブから離れるときは駅舎設置モードを終える。
      if (tab === "house") {
        endHousePlacement();
      }
      if (next === "house") {
        // 軌道の敷設モードを抜けて選択をクリアする(STATE_NORMAL)。
        pc.onCategoryChange(null);
      } else {
        // 軌道タブ(通常/橋梁)⇔プラットフォームタブ(駅)の切替。
        // 規格の候補集合が変わるため選択をリセットし、上の useEffect が新しい候補の先頭を選び直す。
        pc.onCategoryChange("rail");
      }
      setTab(next);
    },
    [tab, pc, endHousePlacement],
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

  /* ---- 駅舎設置: 候補位置の受信と所属駅の解決 ---- */
  // path_id から所属駅を引く。GET /api/v1/stations を都度取得し、path_id を含む駅を探す。
  const resolveStation = useCallback(
    async (pathId: number) => {
      if (!apiBaseUrl) {
        setResolvedStation(null);
        return;
      }
      try {
        const data = await fetchApi<{ stations: StationSummary[] }>(
          buildApiUrl(apiBaseUrl, "/api/v1/stations"),
        );
        const st = data.stations.find((s) => s.paths.some((p) => p.path_id === pathId));
        setResolvedStation(st ? { station_id: st.station_id, name: st.name } : null);
      } catch {
        setResolvedStation(null);
      }
    },
    [apiBaseUrl],
  );

  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlisten: (() => void) | undefined;
    appWindow
      .listen<StationHouseCandidate>(IPC_EVENT_EDIT_STATION_UPDATE_CANDIDATE, (event) => {
        setCandidate(event.payload);
        void resolveStation(event.payload.path_id);
      })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
    };
  }, [resolveStation]);

  /* ---- 駅舎モデルの選択(=設置モードの開始・終了) ---- */
  // モデルの選択が設置モードの開始操作を兼ね、プレースホルダへ戻す操作が終了を兼ねる。
  const handleHouseVariantChange = useCallback(
    (v: number | null) => {
      if (v == null) {
        endHousePlacement();
        return;
      }
      setHouseVariant(v);
      if (placing) {
        // 設置中のモデル変更は、Unityのゴーストを新しいモデルへ作り直させるだけにする。
        void sendEditStationChangeVariant(v);
        return;
      }
      setCandidate(null);
      setResolvedStation(null);
      void sendChangeState("STATE_EDIT_STATION", { house_variant: v }, "track_build");
      setPlacing(true);
    },
    [placing, endHousePlacement],
  );

  /* ---- 駅舎の確定(PATCH → 後始末) ---- */
  const confirmHouse = useCallback(async () => {
    if (!candidate || !apiBaseUrl || !resolvedStation) return;
    setIsConfirmingHouse(true);
    try {
      const res = await fetchApi<{ updated?: Record<string, unknown> }>(
        buildApiUrl(apiBaseUrl, "/api/v1/stations/{id}", String(resolvedStation.station_id)),
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            house_position: candidate.position,
            house_rotation: candidate.rotation,
            house_variant: candidate.house_variant,
          }),
        },
      );
      // Unityのゴーストを片付けてデータをリロードさせてから通常状態へ戻す。
      await sendEditStationFinish();
      await sendChangeState("STATE_NORMAL", {}, "track_build");
      setPlacing(false);
      setCandidate(null);
      setResolvedStation(null);
      setHouseVariant(null);
      const pending = res?.updated?.house_pending === true;
      void showNotification({
        message: pending
          ? t("station.housePendingNotice")
          : t("track_construction.house.placed"),
        durationMs: 5000,
      });
    } catch (err) {
      let displayMessage: string;
      if (err instanceof ApiError) {
        const i18nKey = `errors.${err.code}`;
        displayMessage =
          i18nKey !== "errors.UNKNOWN_ERROR"
            ? t(i18nKey, { defaultValue: err.message })
            : err.message;
      } else {
        displayMessage = t("track_construction.house.confirmFailedUnknown");
      }
      void showNotification({ message: displayMessage, durationMs: 8000 });
    } finally {
      setIsConfirmingHouse(false);
    }
  }, [candidate, apiBaseUrl, resolvedStation, t]);

  /* ---- インフラと違い軌道はグリッド自動オフ不要。破棄時にグリッドと編集モードを片付ける ---- */
  useEffect(() => {
    return () => {
      sendSetGrid("off").catch(() => {});
      // 駅舎設置モードのまま閉じても Unity を確実に通常へ戻す(ウィンドウ閉鎖時のRust側復帰の保険)。
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

  const houseEntries = Object.entries(masterData.station_houses);

  return (
    <div className="pc-layout" onClick={handlePanelClick}>
      {/* 鉄道種別(鉄道 / モノレール / ロープウェイ)。タブより上位の絞り込み。 */}
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

      {tab === "house" ? (
        /* ---- 駅舎タブ ---- */
        <>
          <div className="pc-section">
            <div className="pc-section-title">{t("track_construction.house.selectModel", "駅舎モデル")}</div>
            {houseEntries.length === 0 ? (
              <div className="pc-empty-variants">{t("track_construction.house.noModels", "駅舎モデルがありません")}</div>
            ) : (
              <select
                className="pc-select"
                value={houseVariant ?? ""}
                onChange={(e) => {
                  const val = e.target.value;
                  handleHouseVariantChange(val ? parseInt(val) : null);
                }}
              >
                <option value="">{t("track_construction.house.selectModelPlaceholder", "-- モデルを選択 --")}</option>
                {houseEntries.map(([id, entry]) => (
                  <option key={id} value={id}>
                    {entry.display_name}
                  </option>
                ))}
              </select>
            )}
          </div>

          {/* 設置状態・候補表示 */}
          {placing && (
            <div className="pc-section">
              {candidate && resolvedStation ? (
                <div className="tc-house-candidate">
                  <div className="tc-house-candidate-title">
                    {t("track_construction.house.plannedAt", "設置予定")}
                  </div>
                  <div className="tc-house-candidate-station">{resolvedStation.name}</div>
                  <div className="tc-house-candidate-coord">
                    ({Math.round(candidate.position[0])}, {Math.round(candidate.position[1])})
                  </div>
                </div>
              ) : (
                <div className="pc-no-nodes-message">
                  {t("track_construction.house.placePrompt", "プラットフォーム付近をクリックして設置位置を指定してください")}
                </div>
              )}
            </div>
          )}

          {/* アクション(設置モード中のみ。開始はモデル選択が兼ねる) */}
          {placing && (
            <div className="pc-action-bar">
              <div className="pc-actions">
                <button className="pc-btn" type="button" onClick={endHousePlacement}>
                  {t("track_construction.house.cancel", "キャンセル")}
                </button>
                <button
                  className="pc-btn pc-btn-confirm"
                  type="button"
                  disabled={!candidate || !resolvedStation || isConfirmingHouse}
                  onClick={() => void confirmHouse()}
                >
                  {isConfirmingHouse
                    ? t("track_construction.house.confirming", "確定中...")
                    : t("track_construction.house.confirm", "確定")}
                </button>
              </div>
            </div>
          )}
        </>
      ) : (
        /* ---- 軌道タブ / プラットフォームタブ ---- */
        <>
          <RailVariantSelector
            variantOptions={variantOptions}
            selectedVariant={pc.selectedVariant}
            onSelectVariant={pc.selectVariant}
          />

          <PathVariantDescription entry={pc.selectedVariantData} />

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
        </>
      )}
    </div>
  );
}
