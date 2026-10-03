using System.Collections.Generic;
using UnityEngine;

/// <summary>表示可否を変えず、地上のモデルの材質だけを地下表示用に差し替える。</summary>
public sealed class UndergroundGhostObject : MonoBehaviour
{
    private readonly Dictionary<Renderer, Material[]> originals = new Dictionary<Renderer, Material[]>();
    private void OnEnable() { UndergroundView.Changed += Refresh; Refresh(); }
    private void OnDisable() { UndergroundView.Changed -= Refresh; RestoreMaterials(); }

    public void RestoreMaterials()
    {
        foreach (var pair in originals) if (pair.Key != null) pair.Key.sharedMaterials = pair.Value;
        originals.Clear();
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
            originals[renderer] = materials;
            var faded = new Material[materials.Length];
            for (int i = 0; i < materials.Length; i++) faded[i] = UndergroundMaterials.Ghost(materials[i]);
            renderer.sharedMaterials = faded;
        }
        FarBuildingMember.RefreshObject(gameObject);
    }
}
