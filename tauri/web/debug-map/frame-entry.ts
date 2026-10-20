/**
 * 開発用の地図ページで、地図と上部バーのウィンドウ(iframe)の中身。各ウィンドウ共通の UI(`src/main.tsx`)を読み込む前に、
 * App が種類を知らないこの2つの中身を登録する。ページ(`debug-map-frame.html`)は、ページから読むファイルの相対パスが
 * 他のウィンドウと揃うよう、`app.html` と同じ階層に置く。
 */
import { registerExtraWindow } from "../../src/extraWindows";
import { DebugBar } from "./DebugBar";
import { DebugMap } from "./DebugMap";
import { LABEL_DEBUG_BAR, LABEL_DEBUG_MAP } from "./labels";

registerExtraWindow(LABEL_DEBUG_MAP, DebugMap);
registerExtraWindow(LABEL_DEBUG_BAR, DebugBar);
void import("../../src/main.tsx");
