/**
 * 経済主体リンクコンポーネント
 *
 * 経済主体名を表示し、クリックで詳細パネルの内容をその経済主体に切り替える。
 * 新しいウィンドウを開くのではなく、現在のパネル内でナビゲーション履歴に追加する方式。
 * onNavigate コールバック経由で親コンポーネントにナビゲーションを委譲する。
 */

type AgentLinkProps = {
  agentId: number;
  agentName: string;
  /** ナビゲーション発生時に呼ばれるコールバック。entityId は経済主体IDの文字列 */
  onNavigate: (entityId: string) => void;
};

export function AgentLink({ agentId, agentName, onNavigate }: AgentLinkProps) {
  function handleClick() {
    onNavigate(String(agentId));
  }

  return (
    <button className="entity-link" onClick={handleClick} type="button">
      {agentName}
    </button>
  );
}
