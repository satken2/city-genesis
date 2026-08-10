// Assets/Scripts/MeshGenerators/Buildings/ConstructionSiteProfile.cs

/// <summary>
/// 工事現場メッシュ(<see cref="ConstructingBuildingMeshBuilder"/>)の「状態差分」を集約する軽量プロファイル。
///
/// 建設中(STATUS_CONSTRUCTING)と解体中(STATUS_DEMOLISHING)は、土マウンド・足場リング・天面・建機
/// という構成も積み上げロジックも全く同じで、使用するパーツと建機の種別だけが違う。その差分だけを
/// ここに持たせ、ビルダー本体を両状態で共用する(コード重複を作らない)。
///
/// パーツ名は既存の工事系パーツと同じく <see cref="BuildingMeshUtils.GetPartWithCommonFallback"/> で
/// a{variant}→b{bd_type}→common の順に解決され、無ければスキップされる(オプショナル設計)。
/// </summary>
public enum SiteMachineKind
{
    /// <summary>タワークレーン。crane-base / crane-mid / crane-top を builtFloors+α 段、垂直に積む。</summary>
    StackedCrane,

    /// <summary>地上建機(ショベルカー等)。単体をマウンド上面に実寸で1台据える。</summary>
    GroundVehicle,
}

/// <summary>工事現場メッシュのパーツ・建機プロファイル。<see cref="Construction"/> / <see cref="Demolition"/> の2プリセットを持つ。</summary>
public class ConstructionSiteProfile
{
    /// <summary>足場の壁パーツ名(全辺共通の単一パーツ)。</summary>
    public readonly string ScaffoldWallPart;
    /// <summary>足場の角パーツ名(全頂点共通の単一パーツ)。無ければ壁のみ。</summary>
    public readonly string ScaffoldCornerPart;
    /// <summary>施工済み階を塞ぐ天面パーツ名(マテリアル参照のみ)。</summary>
    public readonly string CeilPart;

    /// <summary>建機の種別(垂直クレーン or 地上建機)。</summary>
    public readonly SiteMachineKind MachineKind;

    // --- StackedCrane 用パーツ ---
    /// <summary>クレーン最下段パーツ名(Y方向のみ階高スケール)。</summary>
    public readonly string CraneBasePart;
    /// <summary>クレーン中間反復パーツ名(Y方向のみ階高スケール)。</summary>
    public readonly string CraneMidPart;
    /// <summary>クレーン頂部パーツ名(実寸)。</summary>
    public readonly string CraneTopPart;

    // --- GroundVehicle 用パーツ ---
    /// <summary>地上建機パーツ名(実寸で1台のみ)。</summary>
    public readonly string GroundMachinePart;

    /// <summary>建機を立てる最小論理階数。これ未満の低層現場には建機を置かない。GroundVehicle は 0(常に据える)。</summary>
    public readonly int MachineMinFloors;

    private ConstructionSiteProfile(
        string scaffoldWallPart, string scaffoldCornerPart, string ceilPart,
        SiteMachineKind machineKind,
        string craneBasePart, string craneMidPart, string craneTopPart,
        string groundMachinePart, int machineMinFloors)
    {
        ScaffoldWallPart = scaffoldWallPart;
        ScaffoldCornerPart = scaffoldCornerPart;
        CeilPart = ceilPart;
        MachineKind = machineKind;
        CraneBasePart = craneBasePart;
        CraneMidPart = craneMidPart;
        CraneTopPart = craneTopPart;
        GroundMachinePart = groundMachinePart;
        MachineMinFloors = machineMinFloors;
    }

    /// <summary>建設中(STATUS_CONSTRUCTING): 標準の足場＋タワークレーン。</summary>
    public static readonly ConstructionSiteProfile Construction = new ConstructionSiteProfile(
        scaffoldWallPart: "scaffold-wall",
        scaffoldCornerPart: "scaffold-corner",
        ceilPart: "ceil",
        machineKind: SiteMachineKind.StackedCrane,
        craneBasePart: "crane-base",
        craneMidPart: "crane-mid",
        craneTopPart: "crane-top",
        groundMachinePart: null,
        machineMinFloors: LocalConstants.CONSTRUCTION_CRANE_MIN_FLOORS);

    /// <summary>解体中(STATUS_DEMOLISHING): 色違いの専用足場＋地上のショベルカー。ロジックは建設中と同一。</summary>
    public static readonly ConstructionSiteProfile Demolition = new ConstructionSiteProfile(
        scaffoldWallPart: "demo-scaffold-wall",
        scaffoldCornerPart: "demo-scaffold-corner",
        ceilPart: "demo-ceil",
        machineKind: SiteMachineKind.GroundVehicle,
        craneBasePart: null,
        craneMidPart: null,
        craneTopPart: null,
        groundMachinePart: "excavator",
        machineMinFloors: 0);
}
