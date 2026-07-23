/**
 * 経路リンクコンポーネント
 *
 * 経路(パス)名を表示し、クリックで経路詳細ウィンドウ(window_type="path_detail")を開く。
 */

import { invoke } from "@tauri-apps/api/core";

type PathLinkProps = {
  pathId: number;
  pathName: string;
};

export function PathLink({ pathId, pathName }: PathLinkProps) {
  async function handleClick() {
    try {
      await invoke("open_registered_window", {
        windowType: "path_detail",
        entityId: String(pathId),
      });
    } catch (error) {
      console.error("failed to open path detail", error);
    }
  }

  return (
    <button className="entity-link" onClick={handleClick} type="button">
      {pathName}
    </button>
  );
}
