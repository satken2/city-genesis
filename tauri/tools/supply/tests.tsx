import assert from "node:assert/strict";
import { Window } from "happy-dom";
import { act } from "react";
import { createRoot } from "react-dom/client";
import type { SupplyGraph, SupplyNode, SupplyResponse } from "../../src/components/supply/supplyGraph";
import { SupplyChainGraph } from "../../src/components/supply/SupplyChainGraph";
import { SupplyChainSection } from "../../src/components/supply/SupplyChainSection";
import { useSupplyGraph } from "../../src/hooks/useSupplyGraph";
import { runtime } from "./mock-runtime";

const window = new Window();
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement,
  IS_REACT_ACT_ENVIRONMENT: true });
const node = (id: string): SupplyNode => ({ id, kind: "facility", role: "producer", selected: id === "selected",
  state: "ok", product_id: 401, count: 2, active_count: 2, production: 100 });
const graph: SupplyGraph = { selected_id: "selected", has_results: true,
  nodes: [{ ...node("water"), state: "missing", kind: "resource", role: "resource" }, node("plant"),
    { ...node("selected"), name: "城西変電所", variant: 1062 }],
  edges: [{ source: "water", target: "plant", product_id: 5, transport: "resource", state: "missing",
    recipient_count: 2, reachable_count: 0, received: 0, demand: 10, path_ids: [] },
  { source: "plant", target: "selected", product_id: 401, transport: "infrastructure", state: "capacity_limited",
    recipient_count: 1, reachable_count: 1, received: 3, demand: 10, path_ids: [9] }] };
const container = window.document.createElement("div");
window.document.body.append(container);
const root = createRoot(container);
await act(async () => { root.render(<SupplyChainGraph graph={graph} />); });
assert.equal(container.querySelectorAll(".supply-node").length, 3);
assert.equal(container.querySelectorAll(".supply-edge").length, 2);
assert.equal(container.querySelectorAll(".supply-node--selected").length, 1);
assert.equal(container.querySelectorAll(".supply-node--missing").length, 1);
assert.equal(container.querySelector(".supply-edge--missing text")?.textContent, "×");
assert.equal(container.querySelector(".supply-edge--capacity_limited text")?.textContent, "!");
assert.equal(container.querySelectorAll("button").length, 0, "the graph has no refresh or drill-down buttons");
assert.ok(container.querySelector(".supply-node-amount[title]")?.getAttribute("title"));
assert.equal(container.querySelectorAll(".supply-node-name")[1]?.textContent, "発電所");
assert.equal(container.querySelector(".supply-node--selected .supply-node-name")?.textContent, "城西変電所");
assert.equal(container.querySelector(".supply-node--selected img")?.getAttribute("src"), "/variant-1062.png");
const supplierIcon = container.querySelectorAll(".supply-node img")[1]?.getAttribute("src");
const onePlant = { ...graph, nodes: graph.nodes.map(n => n.id === "plant" ? { ...n, count: 1, active_count: 1 } : n) };
await act(async () => { root.render(<SupplyChainGraph graph={onePlant} />); });
assert.equal(container.querySelectorAll(".supply-node-name")[1]?.textContent, "発電所", "supplier names do not change with the facility count");
assert.equal(container.querySelectorAll(".supply-node img")[1]?.getAttribute("src"), supplierIcon);

function Harness({ id, tab = "trade", epoch = 0, enabled = true }: { id: string; tab?: string; epoch?: number; enabled?: boolean }) {
  const state = useSupplyGraph("http://core/", id, enabled);
  return <div data-epoch={state.data?.epoch} data-current-epoch={epoch}>
    {tab === "trade" && <SupplyChainSection {...state} />}
  </div>;
}
const response = (agentId: number, epoch: number): SupplyResponse =>
  ({ agent_id: agentId, epoch, pending: false, alert: 0, graph });
await act(async () => { root.render(<Harness id="1" />); });
assert.equal(runtime.requests.length, 1);
assert.equal(runtime.requests[0].url, "http://core/api/v1/agents/1/diagnosis");
await act(async () => { root.render(<Harness id="2" />); });
assert.ok(runtime.requests[0].signal.aborted);
await act(async () => { runtime.requests[1].resolve(response(2, 2)); });
await act(async () => { runtime.requests[0].resolve(response(1, 1)); });
assert.equal(container.firstElementChild?.getAttribute("data-epoch"), "2", "an old response cannot replace the selected agent");
await act(async () => { root.render(<Harness id="2" tab="basic" epoch={3} />); });
assert.equal(container.querySelector(".supply-graph"), null);
await act(async () => { root.render(<Harness id="2" tab="trade" epoch={4} />); });
assert.equal(runtime.requests.length, 2, "tab changes and epoch completion do not fetch");
assert.equal(container.firstElementChild?.getAttribute("data-epoch"), "2");
assert.ok(container.querySelector(".supply-graph"));
assert.equal(container.querySelectorAll("button, .agent-detail-alert-card").length, 0, "the supply section only displays the graph");
await act(async () => { root.render(<Harness key="reopened" id="2" />); });
assert.equal(runtime.requests.length, 3, "reopening the window fetches once");
await act(async () => { root.unmount(); });
assert.ok(runtime.requests[2].signal.aborted);
console.log("PASS: linear supply graph, failure indicators and open/target-only fetching");
