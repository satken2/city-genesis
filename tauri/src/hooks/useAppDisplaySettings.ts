import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";
import { isSupportedLanguage } from "../utils/locale";
import { setCurrencyUnit } from "../utils/money";
import { loadAppSettings, type AppSettings } from "../utils/settings";

/** 各 WebView で言語と通貨単位を復元・同期する。再取得中に受けた更新を古い設定で戻さない。 */
export function useAppDisplaySettings(): void {
  const { i18n } = useTranslation();
  const currentLanguage = useRef(i18n.language);
  currentLanguage.current = i18n.language;
  // 言語ごとに作り直される i18n ラッパーではなく、安定した関数を使って購読と復元を維持する。
  const changeLanguage = i18n.changeLanguage;
  useEffect(() => {
    let disposed = false;
    let receivedUpdate = false;
    const apply = (settings: AppSettings) => {
      if (disposed) return;
      setCurrencyUnit(settings.currency_unit);
      const language = settings.language;
      if (isSupportedLanguage(language) && language !== currentLanguage.current) {
        currentLanguage.current = language;
        void changeLanguage(language);
      }
    };
    const unlisten = listen<AppSettings>("settings-updated", (event) => {
      receivedUpdate = true;
      apply(event.payload);
    });
    void unlisten.then(() => loadAppSettings()).then((settings) => {
      if (!receivedUpdate) apply(settings);
    }).catch(console.error);

    return () => {
      disposed = true;
      void unlisten.then((fn) => fn());
    };
  }, [changeLanguage]);
}
