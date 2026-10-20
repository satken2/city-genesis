import { useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { playSound } from "../../utils/playSound";
import closeIcon from "../../assets/images/ui/close.png";
import defaultIcon from "../../assets/images/ui/info.png";
import notifySound from "../../assets/sounds/notify.mp3";
import "./NotificationWindow.css";

/**
 * Notification payload sent from Rust.
 */
interface NotificationPayload {
  message: string;
  icon_path: string;
  duration_ms?: number;
}

/**
 * Notification popup component.
 *
 * Uses a dedicated frame with no title bar: only a top-right close button, icon, and message.
 * Play the notification sound on display and slide in from below via CSS animation.
 *
 * Behavior:
 * - Update content and show on `notification-show`.
 * - Measure actual content height, then call `resize_notification_window` to fit the window.
 * - The close button calls `dismiss_notification` to show the next queued notification.
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

    // Measure the notification body including margins, rather than the outer frame filling the window.
    const targetHeight = Math.ceil(contentRef.current.getBoundingClientRect().height);

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
    >
      <div className="notification-inner" ref={contentRef}>
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
