/**
 * template_mode の現在値を取得するフック
 *
 * GET /api/v1/sessions でバックエンドから取得した
 * template_mode が True のとき、God edit ボタンを表示するために使用する。
 * コンポーネントマウント時に1回だけAPIを呼び出して状態を取得する。
 */
import { useEffect, useState } from "react";

export function useTemplateMode(apiBaseUrl: string): boolean {
  const [templateMode, setTemplateMode] = useState(false);

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    const base = apiBaseUrl.replace(/\/+$/, "");
    fetch(`${base}/api/v1/sessions`, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) return;
        const data = await res.json() as { template_mode?: boolean };
        if (!controller.signal.aborted) {
          setTemplateMode(data.template_mode === true);
        }
      })
      .catch(() => {
        // 取得失敗時はデフォルトの false のまま
      });
    return () => controller.abort();
  }, [apiBaseUrl]);

  return templateMode;
}
