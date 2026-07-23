/**
 * 市民リンクコンポーネント
 *
 * 世帯構成員の名前を表示し、クリックで市民詳細ウィンドウ(window_type="citizen_detail")を開く。
 */

import { invoke } from "@tauri-apps/api/core";

type CitizenLinkProps = {
  citizenId: number;
  citizenName: string;
};

export function CitizenLink({ citizenId, citizenName }: CitizenLinkProps) {
  async function handleClick() {
    try {
      await invoke("open_registered_window", {
        windowType: "citizen_detail",
        entityId: String(citizenId),
      });
    } catch (error) {
      console.error("failed to open citizen detail", error);
    }
  }

  return (
    <button className="entity-link" onClick={handleClick} type="button">
      {citizenName}
    </button>
  );
}
