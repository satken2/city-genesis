import { useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { playSound } from "../../utils/playSound";
import closeIcon from "../../assets/images/ui/close.png";
import defaultIcon from "../../assets/images/ui/info.png";
import notifySound from "../../assets/sounds/notify.mp3";
import "./NotificationWindow.css";

/**
 * Rust 側から送信される通知ペイロード。
 */
interface NotificationPayload {
  message: string;
  icon_path: string;
  duration_ms?: number;
}

/**
 * 通知ポップアップウィンドウのコンポーネント。
 *
 * タイトルバーがなく、右上に×ボタン、アイコン領域と本文領域のみを持つ専用ウィンドウ枠でレンダリングされる。
 * 表示開始時に通知音を再生し、CSS アニメーションで下からスライドインする。
 *
 * 仕様:
 * - `notification-show` イベントを受信すると内容を更新して表示する。
 * - コンテンツの実際の高さを測定後、`resize_notification_window` を呼び出してウィンドウサイズをフィットさせる。
 * - ×ボタン押下で `dismiss_notification` を呼び出し、キューの次の通知を表示させる。
 */
export function NotificationWindow() {
  const [payload, setPayload] = useState<NotificationPayload | null>(null);
  const [isVisible, setIsVisible] = useState(false);
  const contentRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let unlisten: (() => void) | undefined;

    listen<NotificationPayload>("notification-show", (event) => {
      setPayload(event.payload);
      setIsVisible(true);
      playSound(notifySound);
    }).then((fn) => {
      unlisten = fn;
    });

    return () => {
      unlisten?.();
    };
  }, []);

  useEffect(() => {
    if (!isVisible || !contentRef.current) {
      return;
    }

    // フロントエンドで実際にレンダリングされた高さを測定し、
    // Rust 側のウィンドウサイズをその高さにフィットさせる。
    // padding・margin を含めた高さを渡すことで、コンテンツが見切れないようにする。
    const measuredHeight = contentRef.current.scrollHeight;
    const padding = 24;
    const targetHeight = measuredHeight + padding;

    invoke("resize_notification_window", { height: targetHeight }).catch((err) => {
      console.error("resize_notification_window failed:", err);
    });
  }, [isVisible, payload]);

  const handleClose = () => {
    setIsVisible(false);
    invoke("dismiss_notification").catch((err) => {
      console.error("dismiss_notification failed:", err);
    });
  };

  if (!payload) {
    return null;
  }

  const iconSrc = payload.icon_path || defaultIcon;

  return (
    <div
      className={`notification-container ${isVisible ? "is-visible" : ""}`}
      ref={contentRef}
    >
      <div className="notification-inner">
        <img
          className="notification-icon"
          src={iconSrc}
          alt=""
        />
        <div className="notification-body">
          <p className="notification-message">{payload.message}</p>
        </div>
        <button
          className="notification-close"
          onClick={handleClose}
          type="button"
          aria-label="Close notification"
        >
          <img src={closeIcon} alt="Close" />
        </button>
      </div>
    </div>
  );
}
