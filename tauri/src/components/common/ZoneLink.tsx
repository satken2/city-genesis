/**
 * District link component
 *
 * Displays the district name and opens its detail window (window_type="zone_detail") on click.
 */

import { invoke } from "@tauri-apps/api/core";

type ZoneLinkProps = {
  zoneId: number;
  zoneName: string;
};

export function ZoneLink({ zoneId, zoneName }: ZoneLinkProps) {
  async function handleClick() {
    try {
      await invoke("open_registered_window", {
        windowType: "zone_detail",
        entityId: String(zoneId),
      });
    } catch (error) {
      console.error("failed to open zone detail", error);
    }
  }

  return (
    <button className="entity-link" onClick={handleClick} type="button">
      {zoneName}
    </button>
  );
}
