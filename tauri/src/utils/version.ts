import definition from "../../../version.json";

// Frontend version shared by Tauri and Unity. Check get_frontend_status graphic_version for the version reported by the running Unity instance.
export const frontendVersion = definition;
export type CoreVersion = { core_version: string; build_kind: string };
