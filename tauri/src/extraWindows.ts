import type { ComponentType } from "react";

/**
 * シェルが直接作るウィンドウのうち、App が種類を知らないものの中身。ページの入口が App を描く前に登録する
 * (ブラウザ版の開発用の地図ページ `web/debug-map/` が、Unity の代わりの地図などを足すのに使う)。
 * 製品のページは何も登録しないので、登録する部品は製品のビルドに入らない。
 */
export type ExtraWindowComponent = ComponentType<{ apiBaseUrl: string }>;

const extraWindows = new Map<string, ExtraWindowComponent>();

export function registerExtraWindow(label: string, component: ExtraWindowComponent): void {
  extraWindows.set(label, component);
}

export function extraWindow(label: string): ExtraWindowComponent | undefined {
  return extraWindows.get(label);
}
