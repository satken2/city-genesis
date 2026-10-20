/**
 * Agent link component
 *
 * Displays an agent name; clicking switches the detail panel to that agent.
 * Adds to navigation history within the current panel instead of opening a new window.
 * Delegates navigation to the parent through the onNavigate callback.
 */

type AgentLinkProps = {
  agentId: number;
  agentName: string;
  /** Callback on navigation. entityId is the agent ID as a string. */
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
