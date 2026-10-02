/** シナリオは有限の操作・条件を組み合わせる。任意のコードやAPI URLは受け取らない。 */
export type Policy = "highlight" | "related" | "strict";
export type Resource = "paths" | "parcels" | "buildings" | "agents" | "zones" | "city" | "budgets" | "lines" | "tutorial";
export type Value = null | boolean | number | string | Value[] | { [key: string]: Value };
export type Ref = { kind: Resource; id: number };
export type Bindings = Record<string, Ref>;
export type Match = { field: string; equals?: Value; notEquals?: Value; oneOf?: Value[]; greaterThan?: number };
export type Condition =
  | { type: "ack" }
  | { type: "page"; id: string }
  | { type: "all" | "any"; conditions: Condition[] }
  | { type: "window"; window: string; target?: string }
  | { type: "pathVariant"; value: Value }
  | { type: "pathPoints"; count: number; phase?: string }
  | { type: "hudMode"; mode: "district" | "bulldozer" }
  | { type: "entity"; resource: Resource; where: Match[]; bind?: string; target?: string }
  | { type: "budgetRatio"; category: string; min: number }
  | { type: "connected"; from: string; to: string; terminal: string; where: Match[] };
export type Action =
  | { type: "create"; resource: "paths" | "parcels" | "zones" | "buildings"; bind: string; body: Record<string, Value> }
  | { type: "completeConstruction"; target: string }
  | { type: "scenarioAction"; scenario: string; action: string }
  | { type: "reload" };
/** 段階の開始時にカメラを向ける先。target は地面相対の [x, y, z]、yaw は北を0とする時計回り、pitch は見下ろし角(度)。 */
export type CameraGuide = { target: number[]; distance: number; yaw: number; pitch: number };
export type WorldGuide = {
  mode: "observe" | "blocked" | "path";
  markers?: number[][];
  path?: { points: number[][]; radius: number; variant: Value; bind?: string };
  camera?: CameraGuide;
};
export type Phase = {
  id: string;
  textKey: string;
  ui: { policy: Policy; allow: string[]; highlight: string[] };
  world: WorldGuide;
  until: Condition;
};
export type GuideStep = { id: string; legacyStep: number; phases: Phase[]; beforeCheck?: Action[]; complete?: Condition; afterCheck?: Action[] };
export type Definition = { version: 1; id: string; setup?: Action[]; steps: GuideStep[] };
export type Facts = {
  windows: Record<string, { window: string; visible: boolean; entity?: number }>;
  pathVariant?: number;
  pathShape?: { selected_points: number[][]; phase: string; is_curved?: boolean };
  /** HUDの常駐モード(地区編集・ブルドーザ)。通常状態は未設定。 */
  hudMode?: "district" | "bulldozer";
  /** 「次へ」で送り終えたページ(`page` 条件のid)。ステップを始め直すと空に戻る。 */
  pages?: Record<string, boolean>;
};
export type Snapshot = {
  active: boolean; runId: string; revision: number; saveId: string; scenario: string;
  stepId: string; phaseId: string; ui: Phase["ui"]; world: WorldGuide;
};
export interface Environment {
  bindings: Bindings;
  pendingCreates: Record<string, boolean>;
  facts: Facts;
  constants: Record<string, unknown>;
  read(resource: Resource, id?: number): Promise<Record<string, unknown>[]>;
  create(resource: string, body: unknown): Promise<number>;
  complete(ref: Ref): Promise<void>;
  scenarioAction?(scenario: string, action: string): Promise<void>;
  reload(): Promise<void>;
  checkpoint(): void;
  assertCurrent(): void;
}
