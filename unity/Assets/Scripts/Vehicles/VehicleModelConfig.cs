using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Rendering definitions for public transport vehicles: vehicle/wagon types per line type, and running sounds per appearance.
/// ModelWeights stores appearance options and weights by vehicle type.
/// </summary>
public static class VehicleModelConfig
{
    private static Dictionary<int, TransitDefinition> transitTypes;
    private static Dictionary<int, Dictionary<int, VehicleAmbience.TrainSound>> transitSounds;

    private sealed class TransitDefinition
    {
        public int vehicle_type;
        public string wagon;
    }

    /// <summary>Actual model within a train. Wagon type is independent of locomotive appearance; pools are shared per actual model.</summary>
    public readonly struct TransitCarModel
    {
        public readonly string Part;
        public readonly int LiveryVariant;
        public readonly bool ReverseBody;
        public TransitCarModel(string part, int liveryVariant, bool reverseBody)
        { Part = part; LiveryVariant = liveryVariant; ReverseBody = reverseBody; }
    }

    private static TransitDefinition GetTransitDefinition(int pathVariant)
    {
        if (transitTypes == null)
        {
            var asset = Resources.Load<TextAsset>("Config/transit_vehicle_models");
            transitTypes = asset != null ? JsonConvert.DeserializeObject<Dictionary<int, TransitDefinition>>(asset.text) : new();
        }
        return transitTypes.TryGetValue(pathVariant, out var definition) ? definition : null;
    }

    public static int GetTransitVehicleType(int pathVariant) => GetTransitDefinition(pathVariant)?.vehicle_type ?? 0;

    /// <summary>
    /// Running sounds by public transport appearance (vehicle type and option index), defined by Resources/Config/transit_vehicle_sounds.json.
    /// Unregistered appearances return null for silence. Sounds are rendering-only; changing appearance has no economic effect.
    /// </summary>
    public static VehicleAmbience.TrainSound GetTransitSound(int vehicleType, int variant)
    {
        if (transitSounds == null)
        {
            var asset = Resources.Load<TextAsset>("Config/transit_vehicle_sounds");
            transitSounds = asset != null ? JsonConvert.DeserializeObject<Dictionary<int, Dictionary<int, VehicleAmbience.TrainSound>>>(asset.text) : new();
        }
        return transitSounds.TryGetValue(vehicleType, out var variants) && variants.TryGetValue(variant, out var sound) ? sound : null;
    }

    /// <summary>identity is a car index unchanged by reversal. For freight, 0 is the locomotive; others use the line type's wagon.</summary>
    public static TransitCarModel GetTransitCarModel(int pathVariant, int variant, int identity, int cars)
    {
        var definition = GetTransitDefinition(pathVariant);
        if (!string.IsNullOrEmpty(definition?.wagon))
            return identity == 0 ? new TransitCarModel(variant.ToString(), variant, false)
                : new TransitCarModel(definition.wagon, -1, false);
        bool middle = identity > 0 && identity < cars - 1;
        string part = middle ? (variant == 0 ? "middle" : $"middle_{variant}") : variant.ToString();
        return new TransitCarModel(part, variant, identity == cars - 1 && cars > 1 && definition?.vehicle_type != Constants.VEHICLE_BUS);
    }
}
