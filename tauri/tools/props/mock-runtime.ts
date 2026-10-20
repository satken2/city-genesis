export { state, invoke, emit, listen, useTranslation } from "../scenarios/mock-runtime";
import { listen } from "../scenarios/mock-runtime";
import { PLAY_MODE_SCENARIO } from "../../src/constants";

export function getCurrentWindow() { return { listen }; }
export function usePlayMode() { return PLAY_MODE_SCENARIO; }
export function useMasterData() {
  return { prop_types: { 1: { display_name: "Tree" }, 2: { display_name: "Rock" } } };
}
