using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Recolor chosen material slots of vehicle bodies across close, normal, and distant LODs.
/// Materials are embedded per FBX, so identically named materials on other models are unaffected.
/// Share cloned original-material/color pairs rather than using MaterialPropertyBlock, keeping SRP Batcher compatibility.
/// Each owner keeps its own instance so that resetting one kind of vehicle leaves the others painted.
/// </summary>
public sealed class VehicleRepaint
{
    private readonly Dictionary<GameObject, RendererSlots[]> slotsByCar = new();
    private readonly Dictionary<GameObject, Color32?> appliedByCar = new();
    private readonly Dictionary<(Material, Color32), Material> tinted = new();
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    private sealed class RendererSlots
    {
        public Renderer renderer;
        public int[] indices;
        public Material[] originals;
    }

    public bool IsRegistered(GameObject car) => slotsByCar.ContainsKey(car);

    /// <summary>Record the slots whose material name satisfies painted (null selects none). Call once before recoloring a newly created body.</summary>
    public void Register(GameObject car, Predicate<string> painted)
    {
        var list = new List<RendererSlots>();
        if (painted != null)
        {
            foreach (var renderer in car.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                var indices = new List<int>();
                for (int i = 0; i < materials.Length; i++)
                    if (materials[i] != null && painted(BaseName(materials[i].name))) indices.Add(i);
                if (indices.Count == 0) continue;
                var index = indices.ToArray();
                list.Add(new RendererSlots { renderer = renderer, indices = index, originals = Array.ConvertAll(index, i => materials[i]) });
            }
        }
        slotsByCar[car] = list.ToArray();
        appliedByCar.Remove(car);
    }

    /// <summary>Paint a body, or restore original paint for null. Pooled bodies change only when the color differs from the previous one.</summary>
    public void Apply(GameObject car, Color32? color)
    {
        if (!slotsByCar.TryGetValue(car, out var slots) || slots.Length == 0) return;
        if (appliedByCar.TryGetValue(car, out var current) && Same(current, color)) return;
        appliedByCar[car] = color;
        foreach (var slot in slots)
        {
            if (slot.renderer == null) continue;
            var materials = slot.renderer.sharedMaterials;
            for (int i = 0; i < slot.indices.Length; i++)
                materials[slot.indices[i]] = color.HasValue ? Tinted(slot.originals[i], color.Value) : slot.originals[i];
            slot.renderer.sharedMaterials = materials;
        }
    }

    /// <summary>Restore all original body paint and destroy cloned materials.</summary>
    public void Reset()
    {
        foreach (var car in new List<GameObject>(slotsByCar.Keys))
        {
            if (car == null) { slotsByCar.Remove(car); continue; }
            Apply(car, null);
        }
        appliedByCar.Clear();
        foreach (var material in tinted.Values) if (material != null) UnityEngine.Object.Destroy(material);
        tinted.Clear();
    }

    /// <summary>Clone a material in the given color. The caller owns the clone.</summary>
    public static Material Tint(Material source, Color32 color)
    {
        var material = new Material(source) { name = source.name + "_paint" };
        Color value = color;
        if (material.HasProperty(BaseColorId)) material.SetColor(BaseColorId, value);
        if (material.HasProperty(ColorId)) material.SetColor(ColorId, value);
        // For emissive stripes, retain original emission brightness and change only hue.
        if (material.IsKeywordEnabled("_EMISSION") && material.HasProperty(EmissionColorId))
            material.SetColor(EmissionColorId, value * Mathf.Max(1f, material.GetColor(EmissionColorId).maxColorComponent));
        return material;
    }

    /// <summary>Material name without the suffix Unity appends to runtime copies.</summary>
    public static string BaseName(string name)
    {
        int suffix = name.IndexOf(" (", StringComparison.Ordinal);
        return suffix >= 0 ? name.Substring(0, suffix) : name;
    }

    private static bool Same(Color32? a, Color32? b)
        => a.HasValue == b.HasValue && (!a.HasValue || (a.Value.r == b.Value.r && a.Value.g == b.Value.g && a.Value.b == b.Value.b));

    private Material Tinted(Material source, Color32 color)
    {
        if (tinted.TryGetValue((source, color), out var material) && material != null) return material;
        return tinted[(source, color)] = Tint(source, color);
    }
}
