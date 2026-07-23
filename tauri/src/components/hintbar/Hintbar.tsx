/**
 * 画面上部に常駐するヒントバーコンポーネント
 *
 * タスクバーとは異なり、角丸の枠で囲まれたポップアップ風の表示を行う。
 * ユーザー向けのヒントや操作ガイドを軽く表示するためのもの。
 *
 * - 画面上部中央に固定され、左右 65% の幅を占める
 * - テキストは 2 行程度表示できる高さを確保する
 * - バツボタンはなく、ユーザーが閉じることはできない
 * - 表示/非表示は `session-state` イベントの `current_save_id` で制御される。
 *   セーブデータがロードされている間はテキストの有無に関わらず常に表示し続ける。
 *
 * テキストの更新は `setHintbarText()` ユーティリティを通じて、
 * Tauri のグローバルイベント `hintbar:set-text` を発行することで行う。
 */

import { useEffect, useMemo, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import "./Hintbar.css";

export type HintbarPayload = {
  text: string;
};

type SessionStatePayload = {
  current_save_id: string | null;
};

export function Hintbar() {
  const appWindow = useMemo(() => getCurrentWindow(), []);
  const [visible, setVisible] = useState(true);
  const [text, setText] = useState("");

  useEffect(() => {
    appWindow.setIgnoreCursorEvents(true).catch(console.error);

    let unlistenText: (() => void) | undefined;
    let unlistenSession: (() => void) | undefined;

    listen<HintbarPayload>("hintbar:set-text", (event) => {
      setText(event.payload.text);
    })
      .then((fn) => {
        unlistenText = fn;
      })
      .catch(console.error);

    appWindow
      .listen<SessionStatePayload>("session-state", (event) => {
        const shouldShow = event.payload.current_save_id != null;
        setVisible(shouldShow);
        if (shouldShow) {
          appWindow.show().catch(() => {});
        } else {
          appWindow.hide().catch(() => {});
        }
      })
      .then((fn) => {
        unlistenSession = fn;
      })
      .catch(console.error);

    return () => {
      unlistenText?.();
      unlistenSession?.();
    };
  }, [appWindow]);

  if (!visible) {
    return null;
  }

  return (
    <div className="hintbar-root">
      <div className="hintbar-frame">
        <span className="hintbar-text">{text}</span>
      </div>
    </div>
  );
}
