/**
 * Path link component
 *
 * Displays a path name and opens the path detail window (window_type="path_detail") on click.
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
