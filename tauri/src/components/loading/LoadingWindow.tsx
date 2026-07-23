/**
 * ゲーム開始/ロード中に表示されるローディングウィンドウ。
 *
 * 現在は「読み込み中…」のテキスト表示のみ。
 * 将来的にプログレスバーや背景画像が追加される予定。
 */

import "./LoadingWindow.css";

export function LoadingWindow() {
  return (
    <div className="loading-window" data-tauri-drag-region>
      <p className="loading-text">読み込み中…</p>
    </div>
  );
}
