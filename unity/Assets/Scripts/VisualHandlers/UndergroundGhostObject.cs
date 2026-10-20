using System.Collections.Generic;
using UnityEngine;

/// <summary>Replace only above-ground model materials for underground view, without changing visibility.</summary>
public sealed class UndergroundGhostObject : MonoBehaviour
{
    private readonly HashSet<Renderer> fadedRenderers = new HashSet<Renderer>();
    private void OnEnable() { UndergroundView.Changed += Refresh; Refresh(); }
    private void OnDisable() { UndergroundView.Changed -= Refresh; RestoreMaterials(); }

    public void RestoreMaterials()
    {
        foreach (var renderer in fadedRenderers)
        {
            if (renderer == null) continue;
            // Retain materials replaced by mesh updates and remove only the underground-view overlay.
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++) materials[i] = UndergroundMaterials.Source(materials[i]);
            renderer.sharedMaterials = materials;
        }
        fadedRenderers.Clear();
    }

    public void Refresh()
    {
        RestoreMaterials();
        if (!UndergroundView.IsActive) { FarBuildingMember.RefreshObject(gameObject); return; }
        foreach (var renderer in GetComponentsInChildren<MeshRenderer>(true))
        {
            if (renderer.GetComponentInParent<PathSection>()?.IsUnderground == true) continue;
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++) materials[i] = UndergroundMaterials.Source(materials[i]);
            fadedRenderers.Add(renderer);
            var faded = new Material[materials.Length];
            for (int i = 0; i < materials.Length; i++) faded[i] = UndergroundMaterials.Ghost(materials[i]);
            renderer.sharedMaterials = faded;
        }
        FarBuildingMember.RefreshObject(gameObject);
    }
}
