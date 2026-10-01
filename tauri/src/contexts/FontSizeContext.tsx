/**
 * フォントサイズをアプリケーション全体で管理するReact Context
 *
 * 設定値は本文の文字サイズ(px)で、ドキュメントルートのCSS変数(--font-size-base)を通じて 1rem になる。
 * 各コンポーネントは文字サイズを rem で書くので、本文(1rem)を基準に全体が同じ比率で拡縮する。
 * 値は設定ファイル(settings.json)から読み込み、保存は設定画面(SettingsWindow)が行う。
 * 全コンポーネントで useFontSize() フックから現在のフォントサイズと
 * 変更関数を利用できる。
 *
 * Tauriのマルチウィンドウ間で同期するため、フォントサイズの変更時に
 * Tauriイベント(font-size-changed)を全ウィンドウにブロードキャストする。
 * 各ウィンドウのFontSizeProviderはこのイベントをリッスンし、
 * リアルタイムにフォントサイズを更新する。
 * 後から開いたウィンドウは起動時に設定ファイルから読み込む。
 */

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useLayoutEffect,
  useState,
  type ReactNode,
} from "react";
import { emit, listen } from "@tauri-apps/api/event";
import { loadAppSettings } from "../utils/settings";

/** ウィンドウ間同期に使用するTauriイベント名 */
const EVENT_FONT_SIZE_CHANGED = "font-size-changed";

/**
 * プリセットフォントサイズの定義(本文の文字サイズ px)
 * keyはi18nの翻訳キーとしても使用する。中はWindows標準の本文(Segoe UI 9pt)に相当する。
 * 中は設定ファイルが無いときの既定値でもあり、Rust側 settings.rs の既定値と揃える。
 */
export const FONT_SIZE_PRESETS: Record<string, number> = {
  small: 10,
  medium: 12,
  large: 14,
};

type FontSizeContextValue = {
  /** 現在のフォントサイズ（px） */
  fontSize: number;
  /** フォントサイズを変更する */
  setFontSize: (size: number) => void;
  /** 現在のフォントサイズに対応するプリセットキー（該当なしならnull） */
  presetKey: string | null;
};

const FontSizeContext = createContext<FontSizeContextValue>({
  fontSize: FONT_SIZE_PRESETS.medium,
  setFontSize: () => {},
  presetKey: "medium",
});

/**
 * フォントサイズ値からプリセットキーを逆引きする
 */
function findPresetKey(size: number): string | null {
  for (const [key, value] of Object.entries(FONT_SIZE_PRESETS)) {
    if (value === size) {
      return key;
    }
  }
  return null;
}

/**
 * フォントサイズをContextで提供するプロバイダ
 *
 * マウント時に設定ファイル（settings.json）からフォントサイズを復元し、
 * ドキュメントルート要素の--font-size-base CSS変数を更新する。
 * フォントサイズが変更されるたびにCSS変数・Tauriイベントを同期する。
 *
 * Tauriのマルチウィンドウ間で同期するため:
 * - setFontSize呼び出し時にemitで全ウィンドウに変更を通知する
 * - 他ウィンドウからのTauriイベントをlistenで受信してstateを更新する
 * - 設定ファイルは SettingsWindow 側で保存するため、
 *   本プロバイダでは読み込みとイベント通知のみを行う。
 */
export function FontSizeProvider({ children }: { children: ReactNode }) {
  const [fontSize, setFontSizeState] = useState(FONT_SIZE_PRESETS.medium);

  /**
   * マウント時に設定ファイルからフォントサイズを読み込む。
   * ファイルが存在しないか不正な値の場合はデフォルト（medium）を使用する。
   */
  useEffect(() => {
    let cancelled = false;
    loadAppSettings()
      .then((settings) => {
        if (cancelled) return;
        const loaded = settings.font_size;
        if (Number.isFinite(loaded) && loaded >= 8 && loaded <= 32) {
          setFontSizeState(loaded);
        }
      })
      .catch((err) => {
        console.error("failed to load font size from settings", err);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const setFontSize = useCallback((size: number) => {
    const clamped = Math.max(8, Math.min(32, Math.round(size)));
    setFontSizeState(clamped);
    // 全ウィンドウにフォントサイズ変更を通知する
    void emit(EVENT_FONT_SIZE_CHANGED, clamped);
  }, []);

  /**
   * フォントサイズの変更をCSS変数に反映する。
   * 子の useEffect より先に反映するため layout effect にする(内容の高さを測ってウィンドウを合わせるパネルが、変更前の文字サイズで測らないように)。
   */
  useLayoutEffect(() => {
    document.documentElement.style.setProperty("--font-size-base", `${fontSize}px`);
  }, [fontSize]);

  /**
   * 他ウィンドウからのフォントサイズ変更イベントをリッスンする
   *
   * Tauriのemitは全ウィンドウにイベントを配送するため、
   * 設定ウィンドウで変更したフォントサイズがポップアップにも即座に反映される。
   */
  useEffect(() => {
    const unlisten = listen<number>(EVENT_FONT_SIZE_CHANGED, (event) => {
      const value = event.payload;
      if (Number.isFinite(value) && value >= 8 && value <= 32) {
        setFontSizeState(value);
      }
    });

    return () => {
      void unlisten.then((fn) => fn());
    };
  }, []);

  const presetKey = findPresetKey(fontSize);

  return (
    <FontSizeContext.Provider value={{ fontSize, setFontSize, presetKey }}>
      {children}
    </FontSizeContext.Provider>
  );
}

/**
 * フォントサイズを参照・変更するためのフック
 * FontSizeProvider 配下のコンポーネントからのみ使用可能
 */
export function useFontSize() {
  return useContext(FontSizeContext);
}
