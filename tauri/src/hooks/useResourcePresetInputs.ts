/**
 * 天然資源エリア作成フォームの「目安・埋蔵量上限・回復率」の入力状態。
 *
 * 値は目安(data/resourcePresets.ts)から導出し、面積比例の目安なら形状の面積が変わるたびに
 * 追従する。利用者が手で変えた値はそれを優先し、目安を選び直すか reset() すると目安由来の値へ戻る。
 * 提供物資(種別)が変わったら、選んだ目安も手入力もすべて捨てる(別の種別を経て同じ種別へ
 * 戻ったときも、古い手入力を復活させない)。
 * 地形エディタの資源編集タブと、テンプレート作成モードの資源編集パネルが共有する。
 */

import { useCallback, useState } from "react";
import {
  RESOURCE_FALLBACK_MAX_AMOUNT,
  presetMaxAmount,
  resourcePresetsFor,
  type ResourcePreset,
} from "../data/resourcePresets";

type Edits = {
  presetKey: string | null;
  maxAmount: number | null;
  recoveryRate: number | null;
};

const NO_EDITS: Edits = { presetKey: null, maxAmount: null, recoveryRate: null };

export type ResourcePresetInputs = {
  presets: readonly ResourcePreset[];
  preset: ResourcePreset | null;
  maxAmount: number;
  recoveryRate: number;
  /** 面積比例の目安で形状がまだ無く、埋蔵量上限が仮の値(RESOURCE_FALLBACK_MAX_AMOUNT)になっている */
  awaitingShape: boolean;
  selectPreset: (key: string) => void;
  setMaxAmount: (value: number) => void;
  setRecoveryRate: (value: number) => void;
  /** 手入力を捨てて目安由来の値へ戻す(選んだ目安は保つ) */
  reset: () => void;
};

export function useResourcePresetInputs(productId: number | null, areaM2: number): ResourcePresetInputs {
  const [edits, setEdits] = useState<Edits>(NO_EDITS);
  // 種別が変わったら描画中に編集内容を捨てる(React の「props の変化で state を調整する」定型)。
  // 捨てた結果が反映される再描画までの1回も、古い手入力を使わないよう current で読み替える。
  const [editsProductId, setEditsProductId] = useState(productId);
  if (editsProductId !== productId) {
    setEditsProductId(productId);
    setEdits(NO_EDITS);
  }
  const current = editsProductId === productId ? edits : NO_EDITS;

  const presets = resourcePresetsFor(productId);
  const preset = presets.find((p) => p.key === current.presetKey) ?? presets[0] ?? null;
  const presetAmount = preset ? presetMaxAmount(preset, areaM2) : null;

  const selectPreset = useCallback((key: string) => setEdits({ ...NO_EDITS, presetKey: key }), []);
  const setMaxAmount = useCallback((value: number) => setEdits((prev) => ({ ...prev, maxAmount: value })), []);
  const setRecoveryRate = useCallback(
    (value: number) => setEdits((prev) => ({ ...prev, recoveryRate: value })),
    [],
  );
  const reset = useCallback(() => setEdits((prev) => ({ ...NO_EDITS, presetKey: prev.presetKey })), []);

  return {
    presets,
    preset,
    maxAmount: current.maxAmount ?? presetAmount ?? RESOURCE_FALLBACK_MAX_AMOUNT,
    recoveryRate: current.recoveryRate ?? preset?.recoveryRate ?? 0,
    awaitingShape: current.maxAmount === null && preset !== null && presetAmount === null,
    selectPreset,
    setMaxAmount,
    setRecoveryRate,
    reset,
  };
}
