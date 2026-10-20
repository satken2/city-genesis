// Assets/Scripts/MeshGenerators/Buildings/ConstructionSiteProfile.cs

/// <summary>
/// Lightweight profile collecting state differences for construction-site meshes (<see cref="ConstructingBuildingMeshBuilder"/>).
///
/// Construction (STATUS_CONSTRUCTING) and demolition (STATUS_DEMOLISHING) share dirt mound, scaffolding rings, top, and machinery
/// composition/stacking logic; only parts, machinery types, and whether scaffolding grows or shrinks with progress differ.
/// Keep only these differences here so both states share the builder (without code duplication).
///
/// Part names resolve through <see cref="BuildingMeshUtils.GetPartWithCommonFallback"/>, like other construction parts,
/// in a{variant}→b{bd_type}→common order; missing parts are skipped (optional design).
/// </summary>
public enum SiteMachineKind
{
    /// <summary>Tower crane. Stack crane-base / crane-mid / crane-top vertically for builtFloors+α levels.</summary>
    StackedCrane,

    /// <summary>Rooftop machinery (e.g. demolition excavator). Place one actual-size machine on the site top.</summary>
    RoofVehicle,
}

/// <summary>Construction-site part/machinery profile with two presets: <see cref="Construction"/> / <see cref="Demolition"/>.</summary>
public class ConstructionSiteProfile
{
    /// <summary>Scaffolding wall part name (one part shared by all edges).</summary>
    public readonly string ScaffoldWallPart;
    /// <summary>Scaffolding corner part name (one part shared by all vertices). Walls only if absent.</summary>
    public readonly string ScaffoldCornerPart;
    /// <summary>Top part name for closing constructed floors (material reference only).</summary>
    public readonly string CeilPart;

    /// <summary>Machinery type (vertical crane or rooftop machinery).</summary>
    public readonly SiteMachineKind MachineKind;

    // --- StackedCrane parts ---
    /// <summary>Crane bottom part name (Y-only floor-height scaling).</summary>
    public readonly string CraneBasePart;
    /// <summary>Crane repeated middle part name (Y-only floor-height scaling).</summary>
    public readonly string CraneMidPart;
    /// <summary>Crane top part name (actual size).</summary>
    public readonly string CraneTopPart;

    // --- RoofVehicle parts ---
    /// <summary>Rooftop machinery part name (one actual-size instance).</summary>
    public readonly string RoofMachinePart;

    /// <summary>Minimum logical floor count for machinery. Omit machinery below this. RoofVehicle uses 0 (always placed).</summary>
    public readonly int MachineMinFloors;

    /// <summary>If true, scaffolding loses floors with progress (demolition proceeds from the top); false stacks upward with progress.</summary>
    public readonly bool ShrinksWithProgress;

    private ConstructionSiteProfile(
        string scaffoldWallPart, string scaffoldCornerPart, string ceilPart,
        SiteMachineKind machineKind,
        string craneBasePart, string craneMidPart, string craneTopPart,
        string roofMachinePart, int machineMinFloors, bool shrinksWithProgress)
    {
        ScaffoldWallPart = scaffoldWallPart;
        ScaffoldCornerPart = scaffoldCornerPart;
        CeilPart = ceilPart;
        MachineKind = machineKind;
        CraneBasePart = craneBasePart;
        CraneMidPart = craneMidPart;
        CraneTopPart = craneTopPart;
        RoofMachinePart = roofMachinePart;
        MachineMinFloors = machineMinFloors;
        ShrinksWithProgress = shrinksWithProgress;
    }

    /// <summary>Construction (STATUS_CONSTRUCTING): standard scaffolding + tower crane.</summary>
    public static readonly ConstructionSiteProfile Construction = new ConstructionSiteProfile(
        scaffoldWallPart: "scaffold-wall",
        scaffoldCornerPart: "scaffold-corner",
        ceilPart: "ceil",
        machineKind: SiteMachineKind.StackedCrane,
        craneBasePart: "crane-base",
        craneMidPart: "crane-mid",
        craneTopPart: "crane-top",
        roofMachinePart: null,
        machineMinFloors: LocalConstants.CONSTRUCTION_CRANE_MIN_FLOORS,
        shrinksWithProgress: false);

    /// <summary>Demolition (STATUS_DEMOLISHING): dedicated scaffolding with sound barriers + rooftop excavator (top-down demolition).</summary>
    public static readonly ConstructionSiteProfile Demolition = new ConstructionSiteProfile(
        scaffoldWallPart: "demo-scaffold-wall",
        scaffoldCornerPart: "demo-scaffold-corner",
        ceilPart: "demo-ceil",
        machineKind: SiteMachineKind.RoofVehicle,
        craneBasePart: null,
        craneMidPart: null,
        craneTopPart: null,
        roofMachinePart: "excavator",
        machineMinFloors: 0,
        shrinksWithProgress: true);
}
