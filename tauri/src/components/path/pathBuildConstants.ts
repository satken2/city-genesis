/**
 * Local constants for road types, colors, and variant tiles.
 *
 * Define mappings from road type numbers (path_type) to display names/colors.
 * Colors are used as variant tile icon backgrounds.
 */

/** Road type number → display name mapping. */
export const PATH_TYPE_LABELS: Record<number, string> = {
  0: "仮想道路",
  1: "幹線道路",
  2: "生活道路",
  3: "歩行者道路",
  4: "線路",
  5: "公共交通",
};

/** Tile icon background colors by road type. */
export const PATH_TYPE_COLORS: Record<number, string> = {
  0: "#585b70",
  1: "#fab387",
  2: "#89b4fa",
  3: "#a6e3a1",
  4: "#f9e2af",
  5: "#cba6f7",
};

/** Point role. */
export type PointRole = "start" | "waypoint" | "end";

/** Construction mode. */
export type BuildMode = "IDLE" | "PLACING" | "EDITING";

/** Drawing mode. */
export type CurveMode = "straight" | "curved";

/** Point information during construction. */
export type BuildPoint = {
  x: number;
  z: number;
  role: PointRole;
};

/** Current drag target information. */
export type DragTarget =
  | { type: "point"; index: number }
  | { type: "control"; which: "start" | "end" };

/** Control point information. */
export type ControlPoint = {
  x: number;
  /** Height relative to ground directly below the owning anchor. Fall back to anchor height only before receiving this value. */
  y?: number;
  z: number;
} | null;

/** Layer settings. */
export type LayerConfig = {
  id: string;
  url: string;
  key: string;
  color: string;
  type: "polygon" | "path" | "node";
  fill?: boolean;
  lineWidth?: number;
};

/** Placement editor phase (phase in edit_path:update_shape). Same three stages as Unity's RoadPlanManager. */
export type PathEditorPhase = "placing" | "editing" | "inactive";

/**
 * alignment in edit_path:update_shape. Thinned display samples of full-path alignment validation metrics,
 * or Unity's local estimates before those arrive (shared with 3D red lines and section labels).
 * Omitted for types excluded from alignment evaluation.
 */
export type PathAlignmentSyncPayload = {
  authoritative?: boolean;
  curve_violated?: boolean;
  grade_violated?: boolean;
  /** Polylines of sections rejected by full-path validation. Each point is [x, absolute elevation, z]. */
  warnings?: number[][][] | null;
  /** Minimum curve radius for the standard (m). 0 = unrestricted. */
  radius_limit: number;
  /** Maximum grade for the standard (%). 0 = unrestricted. */
  grade_limit: number;
  /** Minimum curve radius over the entire path (m). null if entirely straight. */
  min_curve_radius: number | null;
  /** Maximum grade over the entire path (%). */
  max_grade: number;
  /** Centerline samples [x, z, curve radius (m; null if straight), grade (%), distance from start (m), absolute road elevation, absolute ground elevation]. */
  samples: Array<[number, number, number | null, number, number, number, number]>;
  /** Worst values for contiguous violation sections by cause (same as 3D section labels). */
  labels: Array<{ curve: boolean; x: number; z: number; value: number; limit: number }>;
  /** [distance from start (m), ground elevation beneath anchor] in anchor order. Horizontal positions and reference heights for the longitudinal profile strip. */
  nodes?: Array<[number, number]>;
};

export type PathMaxSpanViolation = {
  /** Zero-based start-node index. Distance is straight-line distance in XZ (m), not curve length. */
  segment_index: number;
  distance: number;
  limit: number;
};

/**
 * edit_path:update_shape payload. Coordinates are [x, height relative to ground, z].
 * Control heights are relative to ground directly below their owning anchor (Unity ControlToRelativeList).
 */
export type PathShapeUpdatePayload = {
  /** Unity edit generation, updated when shapes/settings change. Full-path validation results return with this generation (apply_alignment). */
  revision?: string;
  /** Existing path ID being edited (0 = new). The name is historical. */
  unsent_paths_count: number;
  path_type: number;
  is_curved: boolean;
  follow_terrain?: number;
  /** True only for the notification that right-click confirmed the shape (Placing → Editing). */
  is_finalized?: boolean;
  /** Whether Unity finalized the shape and passed local checks (grade separation, water crossings, node distances, etc.). Confirmation also requires successful /paths/validate for the same shape. */
  can_confirm?: boolean;
  max_span_violations?: PathMaxSpanViolation[];
  selected_points?: number[][];
  start_control?: number[];
  end_control?: number[];
  embankment_heights?: number[];
  waypoint_controls?: Array<{ control_in?: number[] | null; control_out?: number[] | null }>;
  phase?: PathEditorPhase;
  /** Node index selected in 3D. -1 = no selection. */
  selected_index?: number;
  /** Lateral offset (m) for each parallel track. Empty for a single track. */
  lateral_offsets?: number[];
  /** Whether the variant permits elevation editing (elevated/underground). Controls longitudinal profile strip visibility. */
  height_editable?: boolean;
  /** Minimum relative height (m; master min_height; negative permits underground construction). */
  min_height?: number;
  alignment?: PathAlignmentSyncPayload;
  route_proposal?: RouteProposalShape;
};

export type RouteProposalShape = {
  revision: string;
  points: number[][];
  controls_in: number[][];
  controls_out: number[][];
  embankments: number[];
  generated: number[];
  can_undo: boolean;
  enabled: boolean;
  height_editable: boolean;
  path_variant: number;
  follow_terrain: number;
  components: Array<{
    path_variant: number;
    lateral_offset: number;
    height_offset: number;
    travel_direction: number;
  }>;
  platform_side: number;
  align_station_tracks: boolean;
  exclude_path_id: number | null;
};
