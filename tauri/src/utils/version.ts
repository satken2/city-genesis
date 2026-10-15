import definition from "../../../version.json";

// Tauri と Unity で共通のフロントの版。実行中の Unity が名乗った版は get_frontend_status の graphic_version で確かめる。
export const frontendVersion = definition;
export type CoreVersion = { core_version: string; build_kind: string };
