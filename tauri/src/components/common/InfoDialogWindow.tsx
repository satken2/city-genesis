/**
 * Generic information window
 *
 * Image above; heading, subheading, and explanation below. The opener passes the content in `params.extra`.
 * Text is passed as translation keys so it follows the display language. The button and the title bar's close
 * button both simply close the window; openers that wait for dismissal watch its hidden notification.
 */

import { useTranslation } from "react-i18next";
import "./InfoDialogWindow.css";

export type InfoDialogContent = {
  /** Title bar text. Defaults to the window type's name. */
  titleKey?: string;
  image?: string;
  headingKey: string;
  subheadingKey: string;
  textKey: string;
  buttonKey: string;
};

type InfoDialogWindowProps = {
  content: InfoDialogContent;
  onClose: () => void;
};

export function InfoDialogWindow({ content, onClose }: InfoDialogWindowProps) {
  const { t } = useTranslation();
  return (
    <div className="info-dialog">
      {content.image && (
        <div className="info-dialog-image">
          <img src={content.image} alt="" />
        </div>
      )}
      <div className="info-dialog-content">
        <h2 className="info-dialog-heading">{t(content.headingKey)}</h2>
        <h3 className="info-dialog-subheading">{t(content.subheadingKey)}</h3>
        <p className="info-dialog-text">{t(content.textKey)}</p>
      </div>
      <button className="info-dialog-button" type="button" onClick={onClose}>
        {t(content.buttonKey)}
      </button>
    </div>
  );
}
