import { useState, useEffect } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { NewGameWindow } from "./NewGameWindow";
import { TerrainMapThumbnail } from "../common/TerrainMapThumbnail";
import "./EditorWindow.css";

type EditorWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

type TerrainMapEntry = {
  id: string;
  name: string;
  settings: Record<string, unknown>;
};

/** AppData の作業コピー一覧(list_terrain_editor_maps の戻り値)1件分 */
type TerrainEditorMapSummary = {
  map_id: string;
  map_width: number;
  map_height: number;
  grid_rows: number;
  grid_cols: number;
  /** thumbnail.png のbase64(160x120のPNG)。未生成(一度も保存していない)なら null */
  thumbnail_base64: string | null;
};

export function EditorWindow({ apiBaseUrl, onClose }: EditorWindowProps) {
  const { t } = useTranslation();
  const [activeTab, setActiveTab] = useState<"terrain" | "scenario">("terrain");
  const [terrainMaps, setTerrainMaps] = useState<TerrainMapEntry[]>([]);
  const [loading, setLoading] = useState(true);
  /** 地形コピー中のマップID。ボタンを無効化するために使う。 */
  const [copyingId, setCopyingId] = useState<string | null>(null);
  const [copyError, setCopyError] = useState<string | null>(null);
  /** 新規地形作成中かどうか。ボタンを無効化するために使う。 */
  const [creatingNew, setCreatingNew] = useState(false);
  const [createNewError, setCreateNewError] = useState<string | null>(null);
  /** AppDataに残っている地形エディタの作業コピー一覧(編集途中のデータ)。 */
  const [localMaps, setLocalMaps] = useState<TerrainEditorMapSummary[]>([]);
  const [openingLocalId, setOpeningLocalId] = useState<string | null>(null);
  const [openLocalError, setOpenLocalError] = useState<string | null>(null);
  /** 削除確認中の作業コピーのマップID。確認オーバーレイの表示に使う。 */
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [deleteLocalError, setDeleteLocalError] = useState<string | null>(null);

  // 地形マップ一覧の取得。地形エディタタブで表示するために必要。
  useEffect(() => {
    if (!apiBaseUrl) return;
    const base = apiBaseUrl.replace(/\/+$/, "");

    async function fetchTerrains() {
      setLoading(true);
      try {
        const terrainRes = await fetch(`${base}/api/v1/terrain/maps`).catch(() => null);
        if (terrainRes?.ok) {
          const terrainData: { maps: TerrainMapEntry[] } = await terrainRes.json();
          if (terrainData.maps) {
            setTerrainMaps(terrainData.maps);
          }
        }
      } catch {
        /* エラー時は空表示にする */
      } finally {
        setLoading(false);
      }
    }

    void fetchTerrains();
  }, [apiBaseUrl]);

  // AppData に残っている地形エディタの作業コピー一覧を取得する。
  // 「編集途中のデータを開く」セクションで表示するために必要。
  useEffect(() => {
    async function fetchLocalMaps() {
      try {
        const maps = await invoke<TerrainEditorMapSummary[]>("list_terrain_editor_maps");
        setLocalMaps(maps);
      } catch {
        /* エラー時は空表示にする */
      }
    }
    void fetchLocalMaps();
  }, []);

  /**
   * 「編集途中のデータを開く」タイルクリック時のハンドラ。
   *
   * copy_terrain_for_editor を経由せず、既存の作業コピー(AppData)をそのまま
   * 地形エディタウィンドウで開く。バックエンドから再取得すると編集内容が
   * 上書きされてしまうため、ここでは呼ばない。
   */
  async function handleOpenLocalMap(mapId: string) {
    if (openingLocalId) return;
    setOpenLocalError(null);
    setOpeningLocalId(mapId);
    try {
      await invoke("open_registered_window", {
        windowType: "terrain_editor",
        entityId: mapId,
      });
    } catch (error) {
      setOpenLocalError(
        error instanceof Error
          ? error.message
          : t("editor.openLocalFailed", { defaultValue: "作業コピーを開けませんでした" })
      );
    } finally {
      setOpeningLocalId(null);
    }
  }

  /**
   * 「編集途中のデータを開く」タイルの×ボタン押下時のハンドラ。
   *
   * delete_terrain_editor_map で AppData 上の作業コピーを削除する。
   * バックエンド管理の地形マップ本体には影響しない。
   */
  async function handleDeleteLocalMap(mapId: string) {
    if (deletingId) return;
    setDeleteLocalError(null);
    setDeletingId(mapId);
    try {
      await invoke("delete_terrain_editor_map", { mapId });
      setLocalMaps((prev) => prev.filter((m) => m.map_id !== mapId));
    } catch (error) {
      setDeleteLocalError(
        error instanceof Error
          ? error.message
          : t("editor.deleteLocalFailed", { defaultValue: "作業コピーの削除に失敗しました" })
      );
    } finally {
      setDeletingId(null);
      setConfirmDeleteId(null);
    }
  }

  /**
   * 地形タイルクリック時のハンドラ。
   *
   * バックエンドから選択した地形の近景標高マップを取得してローカル(AppData)に作業コピーを保存し、
   * その後、地形エディタウィンドウを開く。
   * 地形編集は保存されたローカルコピーに対して行われ、バックエンドの状態には影響しない。
   */
  async function handleSelectTerrain(map: TerrainMapEntry) {
    if (copyingId) return;
    setCopyError(null);
    setCopyingId(map.id);
    try {
      // バックエンドから地形データを取得し、Tauri管理領域に作業コピーを保存する。
      await invoke("copy_terrain_for_editor", { mapId: map.id });
      // 地形エディタウィンドウを開く。map_id は entity_id 経由で渡す
      // (single_per_entity ポリシーにより地形ごとに1つのウィンドウ)。
      await invoke("open_registered_window", {
        windowType: "terrain_editor",
        entityId: map.id,
      });
    } catch (error) {
      setCopyError(
        error instanceof Error
          ? error.message
          : t("editor.copyFailed", { defaultValue: "地形データの取得に失敗しました" })
      );
    } finally {
      setCopyingId(null);
    }
  }

  /**
   * 新しい地形作成ボタンのハンドラ。
   *
   * 標高-10m(`local-constants.ts` の `NEW_TERRAIN_INITIAL_ELEVATION_M` と同じ値。
   * Rust側 `create_new_terrain_for_editor` にハードコードされている)で初期化した
   * 1x1セルの標高マップの作業コピーをローカルに生成し、
   * 地形エディタウィンドウを開く。既存の地形マップ一覧には影響しない。
   */
  async function handleCreateNewTerrain() {
    if (creatingNew) return;
    setCreateNewError(null);
    setCreatingNew(true);
    try {
      const mapId: string = await invoke("create_new_terrain_for_editor");
      await invoke("open_registered_window", {
        windowType: "terrain_editor",
        entityId: mapId,
      });
    } catch (error) {
      setCreateNewError(
        error instanceof Error
          ? error.message
          : t("editor.createFailed", { defaultValue: "新しい地形の作成に失敗しました" })
      );
    } finally {
      setCreatingNew(false);
    }
  }

  return (
    <div className="editor-window">
      {/* タブ切り替えバー */}
      <div className="editor-tabs">
        <button
          className={`editor-tab-btn ${activeTab === "terrain" ? "active" : ""}`}
          onClick={() => setActiveTab("terrain")}
          type="button"
        >
          {t("editor.terrainTab", { defaultValue: "地形エディタ" })}
        </button>
        <button
          className={`editor-tab-btn ${activeTab === "scenario" ? "active" : ""}`}
          onClick={() => setActiveTab("scenario")}
          type="button"
        >
          {t("editor.scenarioTab", { defaultValue: "シナリオエディタ" })}
        </button>
      </div>

      {/* タブコンテンツ領域 */}
      <div className="editor-content">
        {activeTab === "terrain" ? (
          <div className="terrain-editor-body" style={{ position: "relative" }}>
            <div className="terrain-editor-header">
              <button
                className="terrain-add-btn"
                disabled={creatingNew}
                onClick={() => void handleCreateNewTerrain()}
                type="button"
              >
                {creatingNew
                  ? t("editor.creating", { defaultValue: "作成中..." })
                  : t("editor.addNewTerrain", { defaultValue: "新しい地形を追加" })}
              </button>
            </div>

            {/* 編集途中のデータを開く(AppDataの作業コピー一覧)。該当なしなら非表示。 */}
            {localMaps.length > 0 && (
              <div className="terrain-editor-local-section">
                <div className="terrain-editor-header">
                  <span>{t("editor.localMapsTitle", { defaultValue: "編集途中のデータを開く" })}</span>
                </div>
                <div className="new-game-tiles">
                  {localMaps.map((map) => (
                    <div key={map.map_id} className="new-game-tile-wrapper">
                      <button
                        className="new-game-tile-delete-btn"
                        disabled={deletingId !== null}
                        type="button"
                        title={t("editor.deleteLocalMap", { defaultValue: "この作業コピーを削除" })}
                        onClick={(e) => {
                          e.stopPropagation();
                          setConfirmDeleteId(map.map_id);
                        }}
                      >
                        ×
                      </button>
                      <button
                        className="new-game-tile"
                        disabled={openingLocalId !== null}
                        type="button"
                        title={map.map_id}
                        onClick={() => void handleOpenLocalMap(map.map_id)}
                      >
                        {map.thumbnail_base64 ? (
                          <img
                            className="new-game-tile-image"
                            src={`data:image/png;base64,${map.thumbnail_base64}`}
                            alt=""
                          />
                        ) : (
                          <div className="new-game-tile-image new-game-tile-image--terrain" />
                        )}
                        <span className="new-game-tile-name">
                          {openingLocalId === map.map_id
                            ? t("editor.opening", { defaultValue: "開いています..." })
                            : map.map_id}
                        </span>
                      </button>
                    </div>
                  ))}
                </div>
                {openLocalError && (
                  <div className="terrain-editor-error">{openLocalError}</div>
                )}
                {deleteLocalError && (
                  <div className="terrain-editor-error">{deleteLocalError}</div>
                )}
              </div>
            )}

            {loading ? (
              <div className="editor-loading">{t("newGame.loading")}</div>
            ) : (
              <div className="new-game-tiles">
                {terrainMaps.map((map) => (
                  <button
                    key={map.id}
                    className="new-game-tile"
                    disabled={copyingId !== null}
                    type="button"
                    title={map.name}
                    onClick={() => void handleSelectTerrain(map)}
                  >
                    <TerrainMapThumbnail apiBaseUrl={apiBaseUrl} mapId={map.id} />
                    <span className="new-game-tile-name">
                      {copyingId === map.id
                        ? t("editor.copying", { defaultValue: "コピー中..." })
                        : map.name}
                    </span>
                  </button>
                ))}
              </div>
            )}
            {copyError && (
              <div className="terrain-editor-error">{copyError}</div>
            )}
            {createNewError && (
              <div className="terrain-editor-error">{createNewError}</div>
            )}

            {confirmDeleteId && (
              <div
                className="terrain-editor-confirm-overlay"
                onMouseDown={() => setConfirmDeleteId(null)}
              >
                <div
                  className="terrain-editor-confirm-box"
                  onMouseDown={(e) => e.stopPropagation()}
                >
                  <div className="terrain-editor-confirm-text">
                    {t("editor.confirmDeleteLocalMap", {
                      defaultValue: "この作業コピーを削除しますか？この操作は取り消せません。",
                    })}
                  </div>
                  <div className="terrain-editor-confirm-actions">
                    <button
                      className="terrain-editor-confirm-yes"
                      disabled={deletingId !== null}
                      onClick={() => void handleDeleteLocalMap(confirmDeleteId)}
                      type="button"
                    >
                      {deletingId === confirmDeleteId
                        ? t("editor.deleting", { defaultValue: "削除中..." })
                        : t("editor.confirmDelete", { defaultValue: "削除" })}
                    </button>
                    <button
                      disabled={deletingId !== null}
                      onClick={() => setConfirmDeleteId(null)}
                      type="button"
                    >
                      {t("editor.cancelDelete", { defaultValue: "キャンセル" })}
                    </button>
                  </div>
                </div>
              </div>
            )}
          </div>
        ) : (
          <NewGameWindow apiBaseUrl={apiBaseUrl} onClose={onClose} isScenarioEditor={true} />
        )}
      </div>
    </div>
  );
}
