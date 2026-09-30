import { useCallback, useEffect, useState } from "react";
import { fetchApi } from "../utils/api";

type ApiImageState = { url: string | null; src: string | null; failed: boolean };

/**
 * バックエンドAPIの画像を Blob URL にして返す。
 *
 * fetchApi で取得して Blob URL に変え、URLが変わったときとアンマウント時に古い Blob URL を解放する。
 * 取得中は `src` も `failed` も立たない。404 などの取得失敗と、`onError`(画像として読めない)で `failed` になる。
 */
export function useApiImageUrl(url: string | null): { src: string | null; failed: boolean; onError: () => void } {
  const [state, setState] = useState<ApiImageState>({ url: null, src: null, failed: false });

  useEffect(() => {
    if (!url) return;
    const controller = new AbortController();
    let objectUrl: string | null = null;
    fetchApi<Blob>(url, { responseType: "blob", signal: controller.signal })
      .then(blob => {
        if (controller.signal.aborted) return;
        objectUrl = URL.createObjectURL(blob);
        setState({ url, src: objectUrl, failed: false });
      })
      .catch(() => {
        if (!controller.signal.aborted) setState({ url, src: null, failed: true });
      });
    return () => {
      controller.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [url]);

  const onError = useCallback(() => {
    setState(current => current.url === url ? { ...current, failed: true } : current);
  }, [url]);

  const current = state.url === url ? state : { src: null, failed: false };
  return { src: current.failed ? null : current.src, failed: current.failed, onError };
}
