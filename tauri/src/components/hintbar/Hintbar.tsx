/**
 * 画面上部に常駐するヒントバーコンポーネント
 *
 * 画面上部中央に、角丸の枠で囲まれたポップアップ風の表示を行う。
 * ユーザー向けのヒントや操作ガイドを軽く表示するためのもの。
 *
 * - 画面上部中央に固定され、左右 55% の幅を占める
 * - テキストは 2 行程度表示できる高さを確保する
 * - バツボタンはなく、ユーザーが閉じることはできない
 * - クリックは透過する(ウィンドウ生成時に Rust 側が `set_ignore_cursor_events` を設定する)
 * - 表示/非表示は次の2条件の AND で決まる。
 *   - `session-state` イベントの `current_save_id`(セーブデータがロードされている間は
 *     テキストの有無に関わらず常に表示し続ける)
 *   - 設定ウィンドウの `hintbar_visible`(設定変更は `settings-updated` イベントで即時反映)
 *
 * テキストの更新は `setHintbarText()` ユーティリティを通じて、
 * Tauri のグローバルイベント `hintbar:set-text` を発行することで行う。
 */

import { useEffect, useMemo, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { loadAppSettings, type AppSettings } from "../../utils/settings";
import "./Hintbar.css";

export type HintbarPayload = {
  text: string;
};

type SessionStatePayload = {
  current_save_id: string | null;
  isLoading?: boolean;
};

export function Hintbar() {
  const appWindow = useMemo(() => getCurrentWindow(), []);
  const [sessionActive, setSessionActive] = useState(false);
  const [enabled, setEnabled] = useState(true);
  const [text, setText] = useState("");

  useEffect(() => {
    let unlistenText: (() => void) | undefined;
    let unlistenSession: (() => void) | undefined;
    let unlistenSettings: (() => void) | undefined;
    let unlistenLoading: (() => void) | undefined;

    listen("initial-loading:finished", () => setSessionActive(true))
      .then((fn) => { unlistenLoading = fn; }).catch(console.error);

    listen<HintbarPayload>("hintbar:set-text", (event) => {
      setText(event.payload.text);
    })
      .then((fn) => {
        unlistenText = fn;
      })
      .catch(console.error);

    appWindow
      .listen<SessionStatePayload>("session-state", (event) => {
        setSessionActive(event.payload.current_save_id != null && !event.payload.isLoading);
      })
      .then((fn) => {
        unlistenSession = fn;
      })
      .catch(console.error);

    loadAppSettings()
      .then((settings) => {
        setEnabled(settings.hintbar_visible !== false);
      })
      .catch(console.error);

    listen<AppSettings>("settings-updated", (event) => {
      setEnabled(event.payload.hintbar_visible !== false);
    })
      .then((fn) => {
        unlistenSettings = fn;
      })
      .catch(console.error);

    return () => {
      unlistenText?.();
      unlistenSession?.();
      unlistenSettings?.();
      unlistenLoading?.();
    };
  }, [appWindow]);

  const visible = sessionActive && enabled;

  /**
   * React の描画を止めるだけでは常時最前面の透明ウィンドウが残るため、
   * 表示条件に合わせてネイティブウィンドウ自体も出し入れする。
   */
  useEffect(() => {
    if (visible) {
      appWindow.show().catch(() => {});
    } else {
      appWindow.hide().catch(() => {});
    }
  }, [appWindow, visible]);

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
