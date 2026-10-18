/**
 * パス建設パネルの敷設方法
 *
 * 方法ごとに Unity のクリック手順とコアの展開(POST /paths/layout)が決まる。単一はすべての種別で選べ、
 * ほかの方法は共有定数の種別一覧(碁盤状は PATH_TYPES_GRID_BUILD)に入っている種別だけで選べる。
 */

import { PATH_BUILD_METHOD_GRID, PATH_BUILD_METHOD_SINGLE, PATH_TYPES_GRID_BUILD } from "../../constants";

const METHOD_PATH_TYPES: Record<number, number[]> = {
  [PATH_BUILD_METHOD_GRID]: PATH_TYPES_GRID_BUILD,
};

/** 翻訳キー path_construction.buildMethod.* の名前 */
export const BUILD_METHOD_KEYS: Record<number, string> = {
  [PATH_BUILD_METHOD_SINGLE]: "single",
  [PATH_BUILD_METHOD_GRID]: "grid",
};

/** 碁盤状の分割数の初期値([1辺目, 2辺目]) */
export const DEFAULT_GRID_DIVISIONS: [number, number] = [3, 3];

/** 種別で選べる敷設方法を、パネルに並べる順で返す。 */
export function buildMethodsFor(pathType: number | null | undefined): number[] {
  const methods = [PATH_BUILD_METHOD_SINGLE];
  for (const [method, types] of Object.entries(METHOD_PATH_TYPES)) {
    if (pathType != null && types.includes(pathType)) methods.push(Number(method));
  }
  return methods;
}
