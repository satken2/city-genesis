import type { MasterData } from "../../src/types";

export const master = { data: null as MasterData | null };
export function useMasterData() { return master.data; }
