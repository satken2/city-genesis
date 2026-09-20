import { useState, useEffect } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { NewGameWindow } from "./NewGameWindow";
import { TerrainMapThumbnail } from "../common/TerrainMapThumbnail";
import { fetchApi } from "../../utils/api";
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

export function EditorWindow({ apiBaseUrl, onClose }: EditorWindowProps) {
  const { t } = useTranslation();
  const [activeTab, setActiveTab] = useState<"terrain" | "scenario">("terrain");
  const [terrainMaps, setTerrainMaps] = useState<TerrainMapEntry[]>([]);
  const [originalMaps, setOriginalMaps] = useState<TerrainMapEntry[]>([]);
  const [loading, setLoading] = useState(true);
  /** 地形コピー中のマップID。ボタンを無効化するために使う。 */
  const [copyingId, setCopyingId] = useState<string | null>(null);
  const [copyError, setCopyError] = useState<string | null>(null);
  /** 新規地形作成中かどうか。ボタンを無効化するために使う。 */
  const [creatingNew, setCreatingNew] = useState(false);
  const [createNewError, setCreateNewError] = useState<string | null>(null);
  // 地形マップ一覧の取得。地形エディタタブで表示するために必要。
  useEffect(() => {
    if (!apiBaseUrl) return;
    const base = apiBaseUrl.replace(/\/+$/, "");

    async function fetchTerrains() {
      setLoading(true);
      try {
        const [terrainData, originals] = await Promise.all([
          fetchApi<{ maps: TerrainMapEntry[] }>(`${base}/api/v1/terrain/maps`),
          fetchApi<{ maps: TerrainMapEntry[] }>(`${base}/api/v1/terrain/maps?original=true`),
        ]);
        setTerrainMaps(terrainData.maps);
        setOriginalMaps(originals.maps);
      } catch {
        /* エラー時は空表示にする */
      } finally {
        setLoading(false);
      }
    }

    void fetchTerrains();
  }, [apiBaseUrl]);

  /**
   * 地形タイルクリック時のハンドラ。
   *
   * バックエンドから選択した地形の近景標高マップを取得してローカル(AppData)に作業コピーを保存し、
   * その後、地形エディタウィンドウを開く。
   * 地形編集は保存されたローカルコピーに対して行われ、バックエンドの状態には影響しない。
   */
  async function handleSelectTerrain(map: TerrainMapEntry, original = false) {
    if (copyingId) return;
    setCopyError(null);
    setCopyingId(`${original ? "original:" : ""}${map.id}`);
    try {
      // バックエンドから地形データを取得し、Tauri管理領域に作業コピーを保存する。
      const copyId = await invoke<string>("copy_terrain_for_editor", { mapId: map.id, original });
      // 地形エディタウィンドウを開く。map_id は entity_id 経由で渡す
      // (single_per_entity ポリシーにより地形ごとに1つのウィンドウ)。
      await invoke("open_registered_window", {
        windowType: "terrain_editor",
        entityId: copyId,
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

            {loading ? (
              <div className="editor-loading">{t("newGame.loading")}</div>
            ) : (
              <>
                <h3>{t("editor.gameTerrains")}</h3>
                <p>{t("editor.gameTerrainsHelp")}</p>
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
                <h3>{t("editor.originalTerrains")}</h3>
                <p>{t("editor.originalTerrainsHelp")}</p>
                <div className="new-game-tiles">
                  {originalMaps.map((map) => (
                    <button
                      key={map.id}
                      className="new-game-tile"
                      disabled={copyingId !== null}
                      type="button"
                      onClick={() => void handleSelectTerrain(map, true)}
                    >
                      <span className="new-game-tile-name">
                        {copyingId === `original:${map.id}` ? t("editor.copying") : map.name}
                      </span>
                    </button>
                  ))}
                </div>
              </>
            )}
            {copyError && (
              <div className="terrain-editor-error">{copyError}</div>
            )}
            {createNewError && (
              <div className="terrain-editor-error">{createNewError}</div>
            )}
          </div>
        ) : (
          <NewGameWindow apiBaseUrl={apiBaseUrl} onClose={onClose} isScenarioEditor={true} />
        )}
      </div>
    </div>
  );
}
