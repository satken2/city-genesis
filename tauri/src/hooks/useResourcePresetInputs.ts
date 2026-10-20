/**
 * Preset, maximum reserves, and recovery rate input state for natural resource creation forms. Derive values
 * from data/resourcePresets.ts; area-based presets follow shape area changes. Manual values take priority until
 * another preset is selected or reset() restores preset values. Changing supplied product (type) discards both
 * the preset and manual input, even when returning to a previous type. Used by the scenario authoring resource
 * panel.
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
  /** An area-based preset has no shape yet; maximum reserves uses the temporary RESOURCE_FALLBACK_MAX_AMOUNT. */
  awaitingShape: boolean;
  selectPreset: (key: string) => void;
  setMaxAmount: (value: number) => void;
  setRecoveryRate: (value: number) => void;
  /** Discard manual input and restore preset-derived values (keep the selected preset). */
  reset: () => void;
};

export function useResourcePresetInputs(productId: number | null, areaM2: number): ResourcePresetInputs {
  const [edits, setEdits] = useState<Edits>(NO_EDITS);
  // Discard edits during rendering when type changes (React's pattern for adjusting state on prop
  // changes). Use current to avoid stale manual values even for the render before the reset takes effect.
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
