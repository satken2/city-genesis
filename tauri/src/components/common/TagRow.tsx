/**
 * 建設プロジェクトのタグ行(建物・道路の詳細パネル共通)
 *
 * 「建設プロジェクト(タグ)の選択」と「この1件を3Dビューで表示/非表示」を1行にまとめる。
 * タグはバックエンドに保存されるが、表示・非表示はUnityがセッション内で持つだけで保存されない
 * (詳しくは `hooks/useObjectVisibility`)。
 */

import { useTranslation } from "react-i18next";

import { useObjectVisibility } from "../../hooks/useObjectVisibility";
import { TagSelect } from "./TagSelect";
import "./TagSelect.css";

type TagRowProps = {
  apiBaseUrl: string;
  kind: "agent" | "path";
  entityId: number;
};

export function TagRow({ apiBaseUrl, kind, entityId }: TagRowProps) {
  const { t } = useTranslation();
  const visibility = useObjectVisibility(kind === "path" ? "path_detail" : "agent_detail");
  const hidden = visibility.isHidden(kind, entityId);

  return (
    <div className="tag-row">
      <span className="tag-row__label">{t("tag.label")}</span>
      <TagSelect apiBaseUrl={apiBaseUrl} kind={kind} entityId={entityId} />
      <button
        type="button"
        className={`tag-row__eye${hidden ? " tag-row__eye--hidden" : ""}`}
        title={hidden ? t("tag.showObject") : t("tag.hideObject")}
        onClick={() => visibility.toggleObject(kind, entityId)}
      >
        {hidden ? "🚫" : "👁"}
      </button>
    </div>
  );
}
