using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>路線色で塗る帯の材質が全外観の通常・接写モデルにあり、塗った色と元の塗装の復帰がそのスロットだけに効くことを確かめる。</summary>
public static partial class VehicleQA
{
    static void LiveryColors()
    {
        var resolver = game.GetType("VehicleModelResolver");
        var livery = game.GetType("TransitLivery");
        var apply = livery.GetMethod("Apply");
        var red = new Color32(229, 23, 31, 255);
        foreach (var (type, variants, hasMiddle) in new[]
        {
            (12, new[]{0,1}, false), (14, new[]{0}, true), (20, new[]{0,1,2}, true),
            (21, new[]{0,1,2}, true), (22, new[]{0,1,2}, false), (24, new[]{0}, true),
        })
        foreach (int variant in variants)
        foreach (bool middle in hasMiddle ? new[]{false,true} : new[]{false})
        {
            var names = (HashSet<string>)livery.GetMethod("GetMaterialNames").Invoke(null, new object[]{type, variant});
            Check(names != null && names.Count > 0, $"Livery material is not configured: {type}/{variant}");
            var car = (GameObject)resolver.GetMethod("CreateTransitCar").Invoke(null, new object[]{type, middle, null, variant});
            var originals = new Dictionary<Renderer, Material[]>();
            foreach (var renderer in car.GetComponentsInChildren<Renderer>(true)) originals[renderer] = renderer.sharedMaterials;
            var lod = car.GetComponent<LODGroup>();
            Check(lod != null, $"Livery LOD missing: {type}/{variant}");
            var levels = lod.GetLODs();
            // 遠景は帯を省いた簡略形なので、接写と通常にだけ帯の材質を要求する。
            for (int level = 0; level < levels.Length - 1; level++)
            {
                bool found = false;
                foreach (var renderer in levels[level].renderers)
                    foreach (var material in renderer.sharedMaterials) found |= material != null && names.Contains(material.name);
                Check(found, $"Livery material missing in LOD{level}: {type}/{variant} middle={middle}");
            }

            apply.Invoke(null, new object[]{car, (Color32?)red});
            int painted = 0;
            foreach (var pair in originals)
            {
                var current = pair.Key.sharedMaterials;
                for (int i = 0; i < current.Length; i++)
                {
                    if (pair.Value[i] == null) continue;
                    if (!names.Contains(pair.Value[i].name)) { Check(current[i] == pair.Value[i], $"Livery repaints an unrelated slot: {type}/{variant} {pair.Value[i].name}"); continue; }
                    Check(current[i] != pair.Value[i], $"Livery slot is not repainted: {type}/{variant}");
                    var color = BodyColor(current[i]);
                    Check(Mathf.Abs(color.r - red.r / 255f) < .01f && Mathf.Abs(color.g - red.g / 255f) < .01f && Mathf.Abs(color.b - red.b / 255f) < .01f,
                        $"Livery color differs: {type}/{variant} {color}");
                    Check(BodyColor(pair.Value[i]) != color, $"Livery changed the shared FBX material: {type}/{variant}");
                    painted++;
                }
            }
            Check(painted > 0, $"Nothing painted: {type}/{variant}");

            apply.Invoke(null, new object[]{car, null});
            foreach (var pair in originals)
            {
                var current = pair.Key.sharedMaterials;
                for (int i = 0; i < current.Length; i++) Check(current[i] == pair.Value[i], $"Livery reset leaves a painted slot: {type}/{variant}");
            }
            accessTimings.Add($"livery type={type} variant={variant} middle={middle} slots={painted}");
            UnityEngine.Object.DestroyImmediate(car);
        }
        livery.GetMethod("Reset").Invoke(null, null);
    }

    /// <summary>本編(URP)は _BaseColor、独立検証のプロジェクト(ビルトイン)は _Color に地の色を持つ。</summary>
    static Color BodyColor(Material material) => material.GetColor(material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color");
}
