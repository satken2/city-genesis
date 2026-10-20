using UnityEngine;

/// <summary>
/// Category-specific visual-mode constants.
/// VisualModeHandler stores modes separately per object type,
/// so duplicate values are acceptable.
/// </summary>
public static class VisualModePath
{
    public const int UTILIZATION = 100;   // Utilization 0..1: white -> red.
    public const int SPEED = 101;         // Traversal speed (PathSummary.base_speed, including curves/grades but excluding intersection slowing/stops) 0..100km/h: red -> green.
    public const int PEDESTRIAN = 102;    // Pedestrian flow 0..100: gray -> blue.
    public const int DURABILITY = 103;    // Durability 0..1: red -> green.
    public const int ZONE_COLOR = 104;    // District colors for roads, using backend zone.color.
}

public static class VisualModeParcel
{
    public const int LAND_PRICE = 200;      // Land price 0..10000: white -> red.
    public const int FILTER_HOUSE = 201;    // Detached houses.
    public const int FILTER_APARTMENT = 202;// Apartments.
    public const int FILTER_COMMERCIAL = 203;// Commercial.
    public const int FILTER_OFFICE = 204;   // Offices.
    public const int FILTER_FACTORY = 205;  // Factories.
    public const int FILTER_OTHER = 206;    // Other.
    public const int FILTER_FARM = 207;     // Farmland.
    public const int FILTER_MINE = 208;     // Mines.
    public const int FILTER_PORT = 209;     // Ports.
    public const int ZONE_COLOR = 210;      // District colors for parcels.
    public const int NOISE = 211;           // Noise 0..1: pale yellow -> dark orange.
    public const int AIR_POLLUTION = 212;   // Air pollution 0..1: pale green-gray -> dark purple.
    public const int CRIME = 213;           // Poor public safety 0..1: pale red -> dark red.
    public const int SUNSHINE = 214;        // Poor sunshine (fraction of time in shadow) 0..1: pale yellow -> dark blue-gray.
}

public static class VisualModeBuilding
{
    public const int POPULATION = 300;   // Population 0..100: white -> green.
    public const int RENT = 301;         // Rent 0..10000: green -> red.
    public const int DURABILITY = 302;   // Durability 0..1: red -> green.
    public const int SPACE_OTHER = 303;     // Other space.
    public const int SPACE_RESIDENCE = 304; // Residential space.
    public const int SPACE_RETAIL = 305;    // Retail space.
    public const int SPACE_FACTORY = 306;   // Factory space.
    public const int SPACE_OFFICE = 307;    // Office space.
    public const int SPACE_FARM = 308;      // Farm space.
    public const int ZONE_COLOR = 309;      // District colors for buildings.
}

public static class VisualModeTerrain
{
    public const int TRANSPARENT = 400;  // Transparent display.
    public const int ELEVATION = 401;    // Elevation colors.
}

