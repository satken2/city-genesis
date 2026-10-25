/**
 * IPC utilities for MemoryMappedFile communication with graphics (Unity). Pass through Tauri invoke to Rust's
 * send_window_action, then send through the pipe to Unity. Event names are colon-separated (e.g.
 * `view:change_view`). Sending function comments and received-event constant comments here define event
 * meanings/payloads. Public docs programs/IPC連携の整理.md define transport, Envelope, and synchronization
 * principles.
 */

import { invoke } from "@tauri-apps/api/core";

/** Replace tutorial guidance in full on changes or Unity reconnection. Do not send periodically. */
export async function sendTutorialGuide(state: import("../tutorial/model").Snapshot): Promise<void> {
  await invoke("send_window_action", { windowType: "tutorial", entity: null, action: "tutorial:apply", params: state });
}

export const IPC_EVENT_WEATHER_STATE = "weather:state";
/** Asset manager tabs: rows are variants for buildings/paths, vehicle types for vehicles, and types for props. */
export type AssetManagerCategory = "buildings" | "paths" | "vehicles" | "props";
/** Per-row source (standard or pack ID), unit (null without a model), and models. weight is the standard weight; null for paths/props. */
export type AssetManagerModels = {
  request_id: string;
  rows: { row: number; source: string; unit: string | null; models: { id: number; weight: number | null }[] }[];
};
/** file is a filename within the user folder's cache/asset_thumbnails/, or null if capture failed. */
export type AssetManagerThumbnail = { request_id: string; row: number; model: number; file: string | null };
export const IPC_EVENT_ASSET_MANAGER_MODELS = "asset_manager:models";
export const IPC_EVENT_ASSET_MANAGER_THUMBNAIL = "asset_manager:thumbnail";
/**
 * Asset manager rendering request. Only while no city is open, Unity opens packId, returns row models via
 * `asset_manager:models`, then writes thumbnails individually to the user folder and announces them through
 * `asset_manager:thumbnail`. Identify responses by requestId. A new request cancels the previous capture
 * sequence.
 */
export async function requestAssetManagerRender(requestId: string, packId: string, category: AssetManagerCategory, rows: number[]): Promise<void> {
  await invoke("send_window_action", {
    windowType: "asset_manager", entity: null, action: "asset_manager:render",
    params: { request_id: requestId, pack_id: packId, category, rows },
  });
}
/** The 3D drive button was pressed. The cockpit holds current-language wording and responds with `openDriveMode` to start. */
export const IPC_EVENT_DRIVE_REQUEST = "drive:request";
/** Unity displays the boarding selection screen. Supply all wording together in the UI's current language. */
export async function openDriveMode(labels: Record<string, string>): Promise<void> {
  await invoke("send_window_action", { windowType: "cockpit", entity: null, action: "drive:open", params: labels });
}
/**
 * Start following a citizen in Unity. Unity creates the designated vehicle/pedestrian and route from the core's
 * estimated itinerary and follows by camera. Start/end notifications return via `citizen:follow_state`,
 * including endings caused by Esc or entering editing mode in Unity.
 */
export async function sendCitizenFollow(windowType: string, citizenId: number): Promise<void> {
  await invoke("send_window_action", { windowType, entity: null, action: "citizen:follow", params: { citizen_id: citizenId } });
}
/** Stop following. Even if not following, Unity repeats the end notification for citizen_id to restore buttons stuck after a missed notification. */
export async function sendCitizenUnfollow(windowType: string, citizenId: number): Promise<void> {
  await invoke("send_window_action", { windowType, entity: null, action: "citizen:unfollow", params: { citizen_id: citizenId } });
}
export const IPC_EVENT_CITIZEN_FOLLOW_STATE = "citizen:follow_state";
export const IPC_EVENT_CITIZEN_PICK_FAILED = "citizen:pick_failed";
/** Volume for Unity sound effects/ambient audio. volume = master volume × effects volume (0–1). */
export async function sendAmbienceVolume(volume: number): Promise<void> {
  await invoke("send_window_action", { windowType: "cockpit", entity: null, action: "ambience:volume", params: { volume } });
}

/** rain/snow range from 0=none to 3=heavy. weight is occurrence percentage under current climate/season; even zero-weight weather can be selected manually. */
export type WeatherState = {
  id: string;
  locked: boolean;
  rain: number;
  snow: number;
  presets: { id: string; weight: number; rain: number; snow: number }[];
};

export async function queryWeather(): Promise<void> {
  await invoke("send_window_action", { windowType: "cockpit", entity: null, action: "weather:query", params: {} });
}

/** id=null releases the override. Unity's response owns weather state; do not randomly select weather in the UI. */
export async function setWeather(id: string | null): Promise<void> {
  await invoke("send_window_action", { windowType: "cockpit", entity: null, action: "weather:set", params: { id } });
}

/**
 * Move the 3D (Unity) window to an adjacent display: direction=1 next, -1 previous. UI and Unity enumerate
 * displays differently, so send no index; Unity selects the adjacent display and remembers its destination.
 */
export async function sendMoveGraphicDisplay(direction: 1 | -1): Promise<void> {
  await invoke("send_window_action", { windowType: "settings", entity: null, action: "system:move_display", params: { direction } });
}

/**
 * Graphics program state types.
 * @see city-genesis/docs/ja/programs/IPC連携の整理.md
 */
export type GraphicState =
  | "STATE_NORMAL"
  | "STATE_EDIT_PATH"
  | "STATE_EDIT_BUILDING"
  | "STATE_EDIT_ZONE"
  | "STATE_EDIT_PARCEL"
  | "STATE_EDIT_PARCEL_SHAPE"
  | "STATE_SET_GRID"
  | "STATE_EDIT_RESOURCE"
  | "STATE_EDIT_TRANSIT"
  | "STATE_EDIT_PROP"
  | "STATE_EDIT_CONNECTOR"
  | "STATE_REMOVE_OBJECTS"
  | "STATE_MERGE_PATH"
  | "STATE_SPLIT_PATH"
  | "STATE_EDIT_ROAD_MEMBERS"
  | "STATE_EDIT_GRADING";

/* ---- ui -> graphic sends ---- */

/**
 * `view.change_view`
 * Notify graphics of the display mode selected in display options. flags is an ID array of toggles independent
 * of color mode ("plan"=planned view, "public"=public facility highlights, "speed_limit"=speed limit signs),
 * sent as CSV. Omitted flags sends null, which Unity interprets as preserving current flags; an empty array
 * explicitly disables all toggles.
 */
export async function sendChangeDisplay(
  layer: string,
  mode: string,
  subMode?: string | null,
  flags?: string[] | null,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "display_options",
    entity: null,
    action: "view:change_view",
    params: {
      layer,
      mode,
      sub_mode: subMode ?? null,
      flags: flags != null ? flags.join(",") : null,
    },
  });
}

/**
 * `world.change_state`
 * Switch graphics state (normal, building construction, etc.). Additional extra parameters merge into params for
 * sending. For example, { path_type: 1, path_variant: 212 } in road placement mode updates Unity's
 * RoadPlanManager. windowType identifies the source window; defaults to "path_build".
 */
export async function sendChangeState(
  state: GraphicState,
  extra?: Record<string, unknown>,
  windowType: string = "path_build",
): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "world:change_state",
    params: { state, ...extra },
  });
}

/**
 * `edit_path.change_node`
 * Change node elevation (relative height) during path placement. Unity's radial menu owns curve/straight and
 * snapping modes, but elevation/embankment need numeric fine-tuning of existing nodes; both panel steppers (this
 * function) and Unity keys can edit them. nodeIndex=-1 refers to the next node to place.
 */
export async function sendEditPathChangeNode(nodeIndex: number, height: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "edit_path:change_node",
    params: { node_index: nodeIndex, height },
  });
}

/**
 * `edit_path.change_embankment`
 * Change node embankment offset (-5 to +5m) during path placement. Available for all road types, including
 * streets that cannot be elevated. nodeIndex=-1 means the next node to place.
 */
export async function sendEditPathChangeEmbankment(nodeIndex: number, embankmentHeight: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "edit_path:change_embankment",
    params: { node_index: nodeIndex, embankment_height: embankmentHeight },
  });
}

/**
 * `edit_building.update_shape`
 * Change the building footprint during construction mode. Apply backend preview geometry to Unity's 3D preview.
 * center_point and height are required for preview mesh placement and scale.
 */
export async function sendEditBuildingUpdateShape(
  polygon: number[][],
  floors: number,
  centerPoint?: number[],
  height?: number,
  placementRevision?: number,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_shape",
    params: {
      polygon,
      floors,
      center_point: centerPoint ?? null,
      height: height ?? null,
      placement_revision: placementRevision ?? null,
    },
  });
}

/**
 * `edit_building:placement_state`. Unity owns placement; panels only reflect it without echoing. position is the
 * fixed [x, 0, z] position (null if unfixed); parcel_id=-1 means no parcel selected. request_id echoes the
 * dimension-change request value; panels cannot confirm until the latest request's response arrives.
 */
export type BuildingPlacementState = {
  variant: number;
  revision: number;
  mode: "parcel" | "free" | "roadside";
  position: number[] | null;
  width: number;
  depth: number;
  angle: number;
  parcel_id: number;
  request_id?: string | null;
};

/** Send only placement setting differences; receive corrected position/dimensions through placement_state. */
export async function sendBuildingPlacementCommand(variant: number, command: {
  width?: number; depth?: number; angle?: number; reset?: boolean; labels?: Record<string, string>; request_id?: string;
} = {}): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:placement_command",
    params: { command: { variant, ...command } },
  });
}

export const IPC_EVENT_BUILDING_PLACEMENT_STATE = "edit_building:placement_state";

/**
 * `edit_building.update_direct_params`
 * Synchronize rectangle width/depth, rotation, and floors with Unity in direct placement mode. Unity draws a
 * cursor-following rectangle ghost from these parameters. Send on variant selection and each value change, both
 * before and after fixing the position.
 */
export async function sendEditBuildingUpdateDirectParams(
  width: number,
  depth: number,
  angle: number,
  floors: number,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_direct_params",
    params: { width, depth, angle, floors },
  });
}

/**
 * `edit_building.update_floors`
 * Change building floor count during construction mode.
 */
export async function sendEditBuildingUpdateFloors(floors: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_floors",
    params: { floors },
  });
}

/**
 * `edit_building.update_landmark`
 * Change landmark type in building construction mode (landmarks master ID; 0=not a landmark). Unity switches to
 * the landmark-specific model (Resources/Models/Buildings/landmark-{model_prefix}) and regenerates the preview
 * mesh.
 */
export async function sendEditBuildingUpdateLandmark(landmark: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_landmark",
    params: { landmark },
  });
}

/**
 * `edit_building.update_visual_variant`
 * Change appearance variants (visual_variant1/2/3) during building construction. Unity regenerates the preview
 * with cached footprint polygons, avoiding a backend round trip.
 */
export async function sendEditBuildingUpdateVisualVariant(
  visualVariant: number,
  visualVariant2: number,
  visualVariant3: number,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_visual_variant",
    params: {
      visual_variant: visualVariant,
      visual_variant2: visualVariant2,
      visual_variant3: visualVariant3,
    },
  });
}

/**
 * `building_construction:pick_source`
 * Start source-building selection in Unity for copy construction. Unity allows only building clicks
 * (MODE_PICK_BUILDING) and returns `building_construction:copy_source` when selected.
 *
 * This is generic existing-building selection; the scenario-mode agent creation panel uses it to select a host
 * building too. windowType identifies the sender without changing Unity behavior. Windows sharing this channel
 * are mutually exclusive in window-mutex-groups.json to prevent multiple panels receiving one notification.
 */
export async function sendPickCopySource(windowType: string = "building_construction"): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "building_construction:pick_source",
    params: {},
  });
}

/**
 * Infrastructure supply facility coverage region shape.
 * - Circle: {type:"circle", center:[x,z], radius}
 * - Arbitrary: {type:"polygon", polygons:[{exterior:[[x,z],...], holes:[[[x,z],...],...]}]}
 * Matches the coverage region format returned by the core API.
 */
export type EffectRangeRegion =
  | { type: "circle"; center: number[]; radius: number }
  | {
      type: "polygon";
      polygons: { exterior: number[][]; holes: number[][][] }[];
    };

/**
 * `edit_building.update_effect_range`
 * Visualize coverage in Unity's 3D view during building construction, carrying two region types:
 * - region: preview coverage if the selected infrastructure facility is built, determined after site selection.
 * - existing: coverage of existing facilities supplying the same service goods, always shown during variant
 * selection.
 * Clear coverage when both region and existing are null/empty (a variant has no coverage, selection clears, or
 * the panel closes).
 */
export async function sendEditBuildingUpdateEffectRange(
  region: EffectRangeRegion | null,
  existing: EffectRangeRegion[] | null = null,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "building_construction",
    entity: null,
    action: "edit_building:update_effect_range",
    params: {
      region,
      existing,
    },
  });
}

/**
 * `edit_parcel:update_preview`
 * Draw preview shapes on Unity's 2D overlay in parcel creation mode. After fetching a backend preview, the UI
 * sends polygons to Unity. Bulk creation includes hundreds of parcels; round coordinates to 1cm precision to
 * limit MMF use (overlay-only values; the backend handles confirmation).
 *
 * @param mode Creation mode (empty_area / along_path / split_rectangle, etc.).
 * @param previewPolygons Preview parcel polygons [ [ [x,y,z], ... ], ... ].
 * @param originalPolygon Original parcel polygon for split mode. Optional.
 */
export async function sendEditParcelUpdatePreview(
  mode: string,
  previewPolygons: number[][][],
  originalPolygon?: number[][] | null,
): Promise<void> {
  const roundPoint = (point: number[]) => point.map((v) => Math.round(v * 100) / 100);
  await invoke("send_window_action", {
    windowType: "parcel_creation",
    entity: null,
    action: "edit_parcel:update_preview",
    params: {
      mode,
      preview_polygons: previewPolygons.map((polygon) => polygon.map(roundPoint)),
      original_polygon: originalPolygon ? originalPolygon.map(roundPoint) : null,
    },
  });
}

/**
 * `edit_parcel:reset_selection`
 * Clear only the 3D selection/preview while retaining parcel creation mode. Send immediately after confirmation
 * or when Clear is pressed. **Confirmation does not leave the mode**, allowing continued 3D clicks to create the
 * next parcel without returning to the panel (Unity's Ctrl radial menu switches creation modes).
 */
export async function sendEditParcelResetSelection(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_creation",
    entity: null,
    action: "edit_parcel:reset_selection",
    params: {},
  });
}

/**
 * `edit_parcel:set_rect_params`
 * Send rectangle width/depth to Unity during parcel creation. Used only to match the local rectangle preview
 * drawn in 3D while dragging its angle after selecting a point. Backend preview (`edit_parcel:update_preview`)
 * remains authoritative for confirmed geometry.
 */
export async function sendEditParcelSetRectParams(width: number, height: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_creation",
    entity: null,
    action: "edit_parcel:set_rect_params",
    params: { width, height },
  });
}

/**
 * `edit_parcel:start_shape_edit`
 * Start parcel shape editing. Unity enters a state allowing vertex editing for the specified parcel.
 */
export async function sendEditParcelStartShapeEdit(
  parcelId: number,
  polygon: number[][],
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_detail",
    entity: null,
    action: "edit_parcel:start_shape_edit",
    params: { parcel_id: parcelId, polygon },
  });
}

/**
 * `edit_parcel:cancel_shape_edit`
 * Cancel parcel shape editing.
 */
export async function sendEditParcelCancelShapeEdit(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_detail",
    entity: null,
    action: "edit_parcel:cancel_shape_edit",
    params: {},
  });
}

/**
 * `edit_parcel:finish_shape_edit`
 * Finish parcel shape editing. Send after the UI successfully saves the polygon via PATCH /api/v1/parcels/{id};
 * Unity resets editing state and reloads data (same role as `edit_path:finish`).
 */
export async function sendEditParcelFinishShapeEdit(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_detail",
    entity: null,
    action: "edit_parcel:finish_shape_edit",
    params: {},
  });
}

/**
 * `edit_zone:finish_merge`
 * Return UI confirmation/cancellation of district merging to Unity. merged=true means POST
 * /api/v1/zones/{id}/merge succeeded; Unity refetches districts/roads to display the result. merged=false
 * cancels only pending confirmation, retaining district editing mode.
 */
export async function sendEditZoneFinishMerge(merged: boolean): Promise<void> {
  await invoke("send_window_action", {
    windowType: "zone_merge",
    entity: null,
    action: "edit_zone:finish_merge",
    params: { merged: merged ? 1 : 0 },
  });
}

/**
 * `parcel:show_front_path`
 * While parcel details are open, ask Unity to visualize the selected parcel's road access (front_path) with a
 * blue arrow crossing the road-facing boundary. Unity fetches front_path_id / facade_edge_index / polygon from
 * parcel_id and draws it.
 */
export async function sendShowParcelFrontPath(parcelId: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_detail",
    entity: null,
    action: "parcel:show_front_path",
    params: { parcel_id: parcelId },
  });
}

/**
 * `parcel:hide_front_path`
 * Clear road access arrows when the parcel detail panel closes.
 */
export async function sendHideParcelFrontPath(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "parcel_detail",
    entity: null,
    action: "parcel:hide_front_path",
    params: {},
  });
}

/**
 * Path interference highlight payload, matching backend `compute_path_interference` / `GET|POST
 * /api/v1/paths/interference`. Parcels have four construction eligibility classes; buildings are agent_ids on
 * those parcels; paths are existing path_ids at grade-separated crossings; rivers are string IDs of rivers
 * crossed at ground level.
 */
export type PathInterferenceHighlight = {
  parcels: {
    blocking: number[];
    removable: number[];
    shavable: number[];
    exempt: number[];
  };
  building_ids: number[];
  colliding_path_ids: number[];
  colliding_river_ids?: string[];
};

/**
 * `world:highlight_objects`
 * Highlight parcels, buildings, and existing paths affected by a planned path in Unity's 3D view. Parcel colors:
 * blocking=red / removable=orange / shavable=yellow / exempt=none. Buildings/paths use warning colors. Send both
 * when placement geometry is finalized or confirmed (windowType="path_build") and when opening pre-construction
 * path details (windowType="path_detail").
 */
export async function sendHighlightObjects(
  interference: PathInterferenceHighlight,
  windowType: string = "path_build",
): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "world:highlight_objects",
    params: {
      // Highlight source. Unity retains independent layers per
      // source so clearing one window's highlights cannot erase another's.
      source: windowType,
      parcels: interference.parcels,
      building_ids: interference.building_ids,
      colliding_path_ids: interference.colliding_path_ids,
      colliding_river_ids: interference.colliding_river_ids ?? [],
    },
  });
}

/**
 * `world:clear_highlights`
 * Clear only this source's (windowType) interference highlights created by `world:highlight_objects`. Send when
 * placement ends or details close.
 */
export async function sendClearHighlights(
  windowType: string = "path_build",
): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "world:clear_highlights",
    params: { source: windowType },
  });
}

/**
 * `edit_connector:show`
 * While AgentDetailPanel is open, ask Unity to show read-only colored markers for facility terminal nodes
 * (infra_node_id for infrastructure, depot_node_id for the running network). Show only one marker for shared
 * nodes (infra_node_id == depot_node_id).
 */
export async function sendEditConnectorShow(
  agentId: number,
  infraNodeId: number,
  depotNodeId: number,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "agent_detail",
    entity: null,
    action: "edit_connector:show",
    params: { agent_id: agentId, infra_node_id: infraNodeId, depot_node_id: depotNodeId },
  });
}

/**
 * `edit_connector:hide`
 * Clear terminal highlights when the panel closes or switches agents. Unity returns to normal only from
 * connector editing (`STATE_EDIT_CONNECTOR`); in other states, only clear highlights.
 */
export async function sendEditConnectorHide(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "agent_detail",
    entity: null,
    action: "edit_connector:hide",
    params: {},
  });
}

/**
 * `edit_resource:finish`
 * End resource editing, reset Unity editing state, and reload data. Tauri UI sends POST /api/v1/resources
 * directly; this event only cleans up after success (clear editing state/session reload). Same role as road
 * construction's `edit_path:finish`.
 */
export async function sendEditResourceFinish(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "resource_edit",
    entity: null,
    action: "edit_resource:finish",
    params: {},
  });
}

/**
 * `edit_transit:finish`
 * End public transport line placement, reset Unity's stop gizmos, and reload data. Tauri UI sends POST
 * /api/v1/transit/lines directly; this event only cleans up after success (clear editing state/session reload).
 * Same role as `edit_path:finish` and `edit_resource:finish`.
 */
export async function sendEditTransitFinish(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "transit_creation",
    entity: null,
    action: "edit_transit:finish",
    params: {},
  });
}

/**
 * `edit_grading:finish`
 * End earthworks area drawing. The UI sends POST /api/v1/grading directly, so this only cleans up after a
 * successful order. Unity handles drawing cleanup → normal state → data refetch in this single event; panels
 * must not separately send `STATE_NORMAL`. Closing after confirmation also makes Rust's close_window_by_label
 * send `STATE_NORMAL`; Unity is already normal, so it only reapplies display/input without cancelling the
 * refetch (redundant but harmless).
 */
export async function sendEditGradingFinish(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "grading",
    entity: null,
    action: "edit_grading:finish",
    params: {},
  });
}

/**
 * `edit_prop:change_settings`
 * Change numeric settings outside dropdowns (rotation, scale, density, seed) during prop placement. Unity
 * immediately applies them to ghost/scatter previews, like `edit_path:change_curved` for road previews.
 * Individual mode uses rotation / scale / height (ground-relative m); area mode uses density / seed.
 */
export async function sendEditPropChangeSettings(params: {
  height?: number;
  rotation?: number;
  scale?: number;
  density?: number;
  seed?: number;
}): Promise<void> {
  await invoke("send_window_action", {
    windowType: "prop_placement",
    entity: null,
    action: "edit_prop:change_settings",
    params,
  });
}

/**
 * `remove_object:confirm_result`
 * Return a dialog answer for Unity's bulldozer confirmation request (`remove_object:request_confirm`,
 * area-placed props). accepted=true makes Unity DELETE and reload differences. Unity never deletes before
 * receiving an answer. Cancellation only closes the dialog, but always return false to release Unity's pending
 * confirmation.
 */
export async function sendRemoveObjectConfirmResult(
  objectType: string,
  id: number,
  accepted: boolean,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "bulldozer",
    entity: null,
    action: "remove_object:confirm_result",
    params: { object_type: objectType, id, accepted: accepted ? 1 : 0 },
  });
}

/**
 * `edit_prop:finish`
 * End prop placement, reset Unity placement state (ghost/scatter previews), and reload data. Tauri UI sends POST
 * /api/v1/props/... directly; this event only cleans up after success. Same role as `edit_path:finish` and
 * `edit_transit:finish`.
 */
export async function sendEditPropFinish(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "prop_placement",
    entity: null,
    action: "edit_prop:finish",
    params: {},
  });
}

/**
 * `edit_prop:reset`
 * Clear all placed instances/vertices and tentative confirmation state during prop placement.
 */
export async function sendEditPropReset(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "prop_placement",
    entity: null,
    action: "edit_prop:reset",
    params: {},
  });
}

/**
 * `session.reset`
 * Clear all current 3D object and terrain data.
 */
export async function sendSessionReset(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "session:reset",
    params: {},
  });
}

/**
 * `session.reload`
 * Fetch backend data and redraw 3D.
 */
export async function sendSessionReload(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "session:reload",
    params: {},
  });
}

/** After the demolition-by-explosion API succeeds, ask Unity to refetch the building, switch to rubble, and play a one-shot effect. */
export async function sendBuildingExploded(agentId: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "building:exploded",
    params: { agent_id: agentId },
  });
}

/**
 * `session.force_reload`
 * Remove all 3D objects, fetch backend data, and redraw. Use when explicitly rebuilding every object, such as
 * the system tray Reload button.
 */
export async function sendSessionForceReload(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "session:force_reload",
    params: {},
  });
}

/**
 * `zone.updated`
 * Notify graphics that the UI updated district information (name, building regulations, etc.). Graphics
 * refetches the district from the backend and updates its cache.
 */
export async function sendZoneUpdated(zoneId: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "zone:updated",
    params: { zone_id: zoneId },
  });
}

/**
 * `edit_path.finish`
 * End path placement; Unity resets editing state and reloads data. Call after Tauri submits directly to the
 * backend.
 */
export async function sendEditPathFinish(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "edit_path:finish",
    params: {},
  });
}

/** Apply station building passage mode (elevated/ground/underground) to the placement preview. Initial value comes from STATE_EDIT_PATH passage_mode. */
export async function sendSetPassageMode(passageMode: string): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build", entity: null,
    action: "edit_path:change_passage_mode", params: { passage_mode: passageMode },
  });
}

/** Apply station building ends (both/start/end) to the placement preview. Initial value comes from STATE_EDIT_PATH house_ends. */
export async function sendSetHouseEnds(houseEnds: string): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build", entity: null,
    action: "edit_path:change_house_ends", params: { house_ends: houseEnds },
  });
}

/**
 * `edit_path.change_platform_side`
 * Change station path platform side (PLATFORM_SIDE_OUTSIDE/INSIDE/BOTH). Unity immediately switches preview
 * platform extents, as with curve mode preview updates. Send only for station variants allowing side selection
 * (single-track stations with platform_width>0; railBuild.hasPlatformSideChoice). Integrated double-track
 * stations define side/island positions by variant, so do not send for them.
 */
export async function sendSetPlatformSide(platformSide: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "edit_path:change_platform_side",
    params: { platform_side: platformSide },
  });
}

/**
 * `edit_path.change_lateral_offsets`
 * Change combined path (multiple tracks) lateral offsets in m (positive right in placement direction) and travel
 * directions (DIR_*). Arrays have equal length and matching indices. Unity immediately redraws parallel preview
 * paths at each offset; changing track count preserves the node sequence. Directions resolve station platform
 * extents (TrackPath.ResolvePlatformExtents) and draw direction arrows. Empty arrays restore a single centerline
 * preview. Initial values arrive via STATE_EDIT_PATH lateral_offsets / travel_directions params.
 */
export async function sendEditPathChangeLateralOffsets(
  lateralOffsets: number[],
  travelDirections: number[],
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "edit_path:change_lateral_offsets",
    params: { lateral_offsets: lateralOffsets, travel_directions: travelDirections },
  });
}

/**
 * `edit_path.change_grid_divisions`
 * Change rectangle subdivisions for grid placement ([first side (point 1→2), second side (point 2→3)]). Unity
 * redraws the temporary grid. Initial value comes from STATE_EDIT_PATH grid_divisions.
 */
export async function sendEditPathChangeGridDivisions(gridDivisions: [number, number]): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "edit_path:change_grid_divisions",
    params: { grid_divisions: gridDivisions },
  });
}

/**
 * Geometry operations from the 2D path shape editor (path_shape_editor) and core results returned by the
 * construction panel. Fields depend on op.
 * - apply_layout: JSON sequence of [start, end] pairs from placement layout expansion (POST /paths/layout);
 * accepted only for the matching source editing generation.
 * - select_node: index.
 * - move_node: index, x, z (XZ only; Unity preserves ground-relative height).
 * - move_control: index, which("in"|"out"), x, z (Unity preserves absolute control elevation).
 * - insert_node: edge_index (edge from points[i] to points[i+1]), x, z.
 * - remove_node: index.
 */
export type PathShapeCommand =
  | { op: "apply_alignment"; revision: string; alignment_json: string; junction_json: string }
  | { op: "apply_route"; revision: string; route_json: string }
  | { op: "apply_layout"; revision: string; layout_json: string }
  | { op: "undo_route"; revision: string }
  | { op: "select_node"; index: number }
  | { op: "move_node"; index: number; x: number; z: number }
  | { op: "move_control"; index: number; which: "in" | "out"; x: number; z: number }
  | { op: "insert_node"; edge_index: number; x: number; z: number }
  | { op: "remove_node"; index: number };

/**
 * `edit_path.shape_command`
 * Send 2D editor node/control operations to Unity. Unity's RoadPlanManager retains geometry ownership, applying
 * existing move/insert/delete logic without snapping and returning the full state via `edit_path:update_shape`.
 * Send once on drag end, never during dragging. Unity ignores requests outside Editing or during a 3D drag.
 */
export async function sendEditPathShapeCommand(command: PathShapeCommand): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_shape_editor",
    entity: null,
    action: "edit_path:shape_command",
    params: command,
  });
}

/** Callout shown at a 3D world position by Unity. Text is already translated by Tauri. */
export type PathJunctionLabel = { position: number[]; text: string };

/** Share full-line construction validation (alignment and track connection violation callouts) with the Unity editing generation used for validation. */
export async function sendEditPathAlignment(revision: string, alignments: unknown, junctions: PathJunctionLabel[] = []): Promise<void> {
  await sendEditPathShapeCommand({
    op: "apply_alignment",
    revision,
    alignment_json: JSON.stringify(alignments ?? null),
    junction_json: JSON.stringify(junctions),
  });
}

/**
 * `edit_path.query_shape`
 * Query the current placement shape; Unity replies once with `edit_path:update_shape`. Since update_shape is
 * otherwise sent only on changes, the 2D editor remounted on each opening uses this for initial state (same
 * request/response pattern as `camera:query_state`).
 */
export async function sendEditPathQueryShape(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_shape_editor",
    entity: null,
    action: "edit_path:query_shape",
    params: {},
  });
}

/**
 * `edit_path.set_follow_terrain`
 * Change per-path terrain following during placement (-1=unset [variant default] / 0=off / 1=on). Unity
 * immediately switches preview terrain following/tunnel visualization and remains authoritative. The updated
 * follow_terrain returns in edit_path:update_shape and is included in confirmation. Ignored for variants without
 * height_editable.
 */
export async function sendEditPathSetFollowTerrain(value: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "edit_path:set_follow_terrain",
    params: { value },
  });
}

/**
 * `world.change_state` (STATE_MERGE_PATH)
 * Start merge-target selection. Unity colors candidate paths connected to the specified path at shared nodes and
 * returns the clicked one through `edit_path:select_merge_target`. pathId is the survivor currently open in path
 * details.
 */
export async function sendStartPathMerge(pathId: number): Promise<void> {
  await sendChangeState("STATE_MERGE_PATH", { path_id: pathId }, "path_detail");
}

/**
 * `world.change_state` (STATE_SPLIT_PATH)
 * Start split-point selection. Unity marks the cursor projection onto the target centerline and returns the
 * clicked point through `edit_path:select_split_point`.
 */
export async function sendStartPathSplit(pathId: number): Promise<void> {
  await sendChangeState("STATE_SPLIT_PATH", { path_id: pathId }, "path_detail");
}

/**
 * `world.change_state` (STATE_NORMAL)
 * Cancel path merge/split selection (cleanup when closing without confirmation). Unity clears candidate colors
 * and split markers, returning to normal. On confirmation, use `sendFinishPathTopologyEdit`, which also redraws.
 */
export async function sendEndPathTopologyEdit(): Promise<void> {
  await sendChangeState("STATE_NORMAL", undefined, "path_detail");
}

/**
 * `edit_path.finish_topology_edit`
 * Notify successful merge/split submission. Unity ends selection, destroys removed paths, returns to normal, and
 * refetches in one handler. Separate `session:reload` and `world:change_state` events can race asynchronous
 * differential reload against state transitions and leave the display stale; use one event like
 * `edit_path:finish`. removedPathId is the absorbed path ID for merges (0 for splits), ensuring immediate 3D
 * removal without waiting for differential reload deletion detection.
 */
export async function sendFinishPathTopologyEdit(removedPathId: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_detail",
    entity: null,
    action: "edit_path:finish_topology_edit",
    params: { removed_path_id: removedPathId },
  });
}

/* ---- Roads (named roads) ---- */

/**
 * `world.set_road_focus`
 * Highlight sections of the road open in details using a band in its road color. Draw above other content along
 * section centerlines, as with public transport highlights. Each send replaces the previous highlight.
 */
export async function sendRoadFocus(roadId: number, color: string, pathIds: number[]): Promise<void> {
  await invoke("send_window_action", {
    windowType: "road_detail",
    entity: null,
    action: "world:set_road_focus",
    params: { road_id: roadId, color, path_ids: pathIds },
  });
}

/**
 * `world.clear_road_focus` clears road highlights (Rust also sends on panel close). With roadId, Unity clears
 * only if it matches the highlighted road, preserving another detail panel's highlights.
 */
export async function sendClearRoadFocus(roadId?: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "road_detail",
    entity: null,
    action: "world:clear_road_focus",
    params: roadId != null ? { road_id: roadId } : {},
  });
}

/**
 * `world.refresh_roads`
 * Send after road creation/rename/membership changes/deletion to make Unity refetch GET /api/v1/roads/. Rebuild
 * 3D road name labels only when the list version changes.
 */
export async function sendRefreshRoads(windowType: string = "road_detail"): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "world:refresh_roads",
    params: {},
  });
}

/**
 * `world.change_state` (STATE_EDIT_ROAD_MEMBERS)
 * Start road section editing. Unity colors current members and returns `edit_road:toggle_path` on
 * street/motorway section clicks; the UI updates membership through APIs. sessionId is a positive integer
 * assigned for each panel editing session, echoed by Unity in edit_road:ended. Panels compare it to prevent
 * delayed completion notifications from clearing newer edits.
 */
export async function sendStartRoadMemberEdit(
  roadId: number,
  color: string,
  pathIds: number[],
  sessionId: number,
): Promise<void> {
  await sendChangeState(
    "STATE_EDIT_ROAD_MEMBERS",
    { road_id: roadId, color, path_ids: pathIds, session_id: sessionId },
    "road_detail",
  );
}

/**
 * `edit_road.set_members`
 * During section editing, send the full membership set and candidate sections for automatic straight-ahead
 * extension. Always send the full set rather than a diff. previewPathIds are faintly drawn unconfirmed
 * candidates.
 */
export async function sendRoadEditSetMembers(
  roadId: number,
  pathIds: number[],
  previewPathIds: number[] = [],
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "road_detail",
    entity: null,
    action: "edit_road:set_members",
    params: { road_id: roadId, path_ids: pathIds, preview_path_ids: previewPathIds },
  });
}

/**
 * `edit_road.cancel`
 * End section editing only if Unity is editing this road, returning to normal. Do nothing in another mode or
 * when editing another road. Do not send STATE_NORMAL, which could cancel another Unity mode (such as road
 * placement) entered without UI knowledge. Rust sends the same `edit_road:cancel` on panel close.
 */
export async function sendEndRoadMemberEdit(roadId: number): Promise<void> {
  await invoke("send_window_action", {
    windowType: "road_detail",
    entity: null,
    action: "edit_road:cancel",
    params: { road_id: roadId },
  });
}

/**
 * A section was clicked in section editing mode (Unity → UI). Payload: { path_id, path_name, path_type, shift }.
 * shift=true means Shift+click, adding all sections reached by following straight ahead from this section.
 */
export const IPC_EVENT_ROAD_TOGGLE_PATH = "edit_road:toggle_path";

export type RoadTogglePathPayload = {
  path_id: number;
  path_name: string;
  path_type: number;
  shift: boolean;
};

/**
 * Unity exited section editing (Unity → UI), regardless of cause: Done, Esc, another mode, edit_road:cancel from
 * a closed panel, session reset, etc. session_id echoes the starting STATE_EDIT_ROAD_MEMBERS value (absent if
 * Unity does not return it).
 */
export const IPC_EVENT_ROAD_EDIT_ENDED = "edit_road:ended";

export type RoadEditEndedPayload = {
  road_id: number;
  session_id?: number;
};

/**
 * `view.set_grid`
 * Notify graphics of grid visibility/settings. Dot display mode has been removed.
 */
export async function sendSetGrid(
  mode: "off" | "line",
  size?: number,
  angle?: number,
  originX?: number,
  originZ?: number,
): Promise<void> {
  await invoke("send_window_action", {
    windowType: "path_build",
    entity: null,
    action: "view:set_grid",
    params: {
      mode,
      ...(size !== undefined ? { size } : {}),
      ...(angle !== undefined ? { angle } : {}),
      ...(originX !== undefined ? { origin_x: originX } : {}),
      ...(originZ !== undefined ? { origin_z: originZ } : {}),
    },
  });
}

/**
 * `game.start`
 * Notify graphics that the game started. Send when the backend creates a session and current_save_id becomes
 * non-null.
 */
export async function sendGameStart(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "game:start",
    params: {},
  });
}

/**
 * `game.end`
 * Notify graphics that the game ended. Send when the session is discarded and current_save_id returns to null.
 */
export async function sendGameEnd(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "game:end",
    params: {},
  });
}

/**
 * `system.focus`
 * Bring the graphics (Unity) window to the OS foreground so users can immediately interact with 3D after
 * pressing a UI panel button.
 */
export async function sendFocusGraphic(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "system:focus",
    params: {},
  });
}

/**
 * `system.shutdown`
 * Shut down the graphics program.
 */
export async function sendSystemShutdown(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "",
    entity: null,
    action: "system:shutdown",
    params: {},
  });
}

/**
 * `camera.query_state`
 * Query the current 3D camera position/heading (reply: `camera:state`). Do not continuously follow camera
 * motion, which would waste MMF capacity. Send when opening or reloading the 2D city map and draw that camera
 * position once.
 */
export async function sendCameraQueryState(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "city_map",
    entity: null,
    action: "camera:query_state",
    params: {},
  });
}

/**
 * `camera.focus`
 * Smoothly move the 3D camera, preserving heading, until the bounding rectangle of planar geometry [[x, z], ...]
 * fits on screen. Pass coordinates instead of IDs because Unity has no objects outside its resident area.
 * Ignored while following a citizen or riding in drive mode.
 */
export async function sendCameraFocus(windowType: string, polygon: number[][]): Promise<void> {
  const xs = polygon.map((p) => p[0]);
  const zs = polygon.map((p) => p[1]);
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "camera:focus",
    params: { min_x: Math.min(...xs), min_z: Math.min(...zs), max_x: Math.max(...xs), max_z: Math.max(...zs) },
  });
}

/**
 * `view.query_state`
 * Query the graphics program's current display state.
 */
export async function sendViewQueryState(): Promise<void> {
  await invoke("send_window_action", {
    windowType: "display_options",
    entity: null,
    action: "view:query_state",
    params: {},
  });
}

/**
 * Display preset names, which must match Unity's `PRESET_*` in `IpcViewCoordinator`.
 * - Public transport list/creation display: all lines plus planned roads/buildings.
 * - Parcel detail display: parcel boundaries.
 */
export const VIEW_PRESET_TRANSIT = "transit";
export const VIEW_PRESET_PARCEL_DETAIL = "parcel_detail";

/**
 * `view.apply_preset`
 * Temporarily replace the 3D display while a window is open. Operations with dedicated editing states (STATE_*,
 * such as road placement/district editing) already get state-specific presets from Unity and need not call this.
 * **Only for windows without their own state**; STATE_NORMAL, sent by Rust on close, restores the original
 * display. Unity defines presets; the UI supplies only names. These changes do not enter Unity's remembered
 * normal display; do not use sendChangeDisplay for this, which would overwrite the user's choices.
 */
export async function sendViewApplyPreset(preset: string, windowType: string): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "view:apply_preset",
    params: { preset },
  });
}

/**
 * `view.restore_preset`
 * Release a window-specific preset and restore the previous display. Used by stateless windows such as details
 * that can coexist with other editing states, safely releasing only their own preset without sending
 * STATE_NORMAL.
 */
export async function sendViewRestorePreset(preset: string, windowType: string): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "view:restore_preset",
    params: { preset },
  });
}

/**
 * `view.set_object_filter`
 * Send the complete set of individually hidden buildings/roads to graphics. Independent of layer visibility
 * (`view:change_view`); shared by construction project tag toggles and detail panel individual toggles. **Always
 * send the entire currently hidden set, not a diff**, preventing divergence from missed additions/removals.
 * Unity owns state; never send it to the backend.
 */
export async function sendSetObjectFilter(
  hiddenBuildingIds: number[],
  hiddenPathIds: number[],
  windowType: string = "construction_projects",
): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "view:set_object_filter",
    params: {
      hidden_building_ids: hiddenBuildingIds,
      hidden_path_ids: hiddenPathIds,
    },
  });
}

/**
 * `view.query_object_filter`
 * Query graphics for current per-object visibility filters. Reply: `view:object_filter_state`
 * (`IPC_EVENT_VIEW_OBJECT_FILTER_STATE`).
 */
export async function sendQueryObjectFilter(
  windowType: string = "construction_projects",
): Promise<void> {
  await invoke("send_window_action", {
    windowType,
    entity: null,
    action: "view:query_object_filter",
    params: {},
  });
}

/* ---- graphic -> ui received event names (constants) ---- */
/* Rust uses these event names when forwarding to the frontend through Tauri emit. */

/**
 * An already placed node was selected during path placement (Unity → UI). Payload includes height (relative
 * elevation) and embankment_height, synchronizing panel steppers and the edited node_id. node_id=-1 means
 * next-placement values.
 */
export const IPC_EVENT_EDIT_PATH_SELECT_NODE = "edit_path:select_node";

/**
 * The placement path shape changed (Unity → UI). Broadcast to all WebViews so both placement and 2D shape editor
 * panels receive it. Payload type: PathShapeUpdatePayload in components/path/pathBuildConstants.ts.
 */
export const IPC_EVENT_EDIT_PATH_UPDATE_SHAPE = "edit_path:update_shape";

/**
 * A merge target was selected in STATE_MERGE_PATH (Unity → UI). Payload: { path_id: number; path_name: string }.
 * Path details fetch a merge preview (dry_run), display length, and await confirmation.
 */
export const IPC_EVENT_EDIT_PATH_SELECT_MERGE_TARGET = "edit_path:select_merge_target";

/**
 * A split point was selected in STATE_SPLIT_PATH (Unity → UI). Payload: { point: [x, y, z] }, already projected
 * onto the target centerline. Path details fetch a split preview (dry_run) and display both resulting lengths.
 */
export const IPC_EVENT_EDIT_PATH_SELECT_SPLIT_POINT = "edit_path:select_split_point";

/** A parcel was selected in building construction mode. */
export const IPC_EVENT_EDIT_BUILDING_SELECT_PARCEL = "edit_building:select_parcel";

/**
 * A point outside parcels was clicked in direct building placement mode. Payload: { position: [x, y, z] }. The
 * UI requests a backend rectangle footprint preview centered here and enters direct placement mode.
 */
export const IPC_EVENT_EDIT_BUILDING_SELECT_POINT = "edit_building:select_point";

/**
 * A parcel where the selected building cannot stand was clicked in building construction mode. Payload: { parcel_id,
 * reason, water_depth, berth_depth }. reason is the backend's site code ("incompatible_parcel_type" or
 * "insufficient_water_depth"); water_depth / berth_depth (m) are the parcel's depth at the quay and the port's requirement.
 */
export const IPC_EVENT_EDIT_BUILDING_INVALID_PARCEL = "edit_building:invalid_parcel";

/**
 * A source building was selected for copy construction. Payload: { source_agent_id, variant, floors,
 * visual_variant, visual_variant2, visual_variant3 }. The panel uses these as initial values and enters
 * STATE_EDIT_BUILDING parcel selection. The agent_creation panel also receives this through sendPickCopySource,
 * using source_agent_id as its host building and ignoring other fields.
 */
export const IPC_EVENT_BUILDING_CONSTRUCTION_COPY_SOURCE = "building_construction:copy_source";

/**
 * Middle-button drag rotation ended (released) in direct placement mode. Unity reports its finalized angle in
 * degrees; the UI updates rectAngle.
 */
export const IPC_EVENT_EDIT_BUILDING_UPDATE_DIRECT_ANGLE = "edit_building:update_direct_angle";

/** A 3D point was clicked during parcel creation (empty_area). */
export const IPC_EVENT_EDIT_PARCEL_SELECT_POINT = "edit_parcel:select_point";

/**
 * A path was selected during parcel creation (along_path). Payload: { path_id, position }, with position
 * projected onto the path centerline as [x, y, z]. For clicked-point scope, this anchors creation at that
 * location only.
 */
export const IPC_EVENT_EDIT_PARCEL_SELECT_PATH = "edit_parcel:select_path";

/** An existing parcel was selected during parcel creation (split modes). */
export const IPC_EVENT_EDIT_PARCEL_SELECT_PARCEL = "edit_parcel:select_parcel";

/**
 * The 3D radial menu (Ctrl) changed parcel creation mode (Unity → UI). Payload: { mode } (empty_area /
 * along_path / … / split_random). Unity (ParcelEditorManager.CurrentMode) owns creation mode; the panel adjusts
 * inputs/selection accordingly. **Receiving the current mode means reset selection** (Esc in 3D or reselecting
 * the same mode). Clear selection only, retaining width/depth and other inputs.
 */
export const IPC_EVENT_EDIT_PARCEL_SET_MODE = "edit_parcel:set_mode";

/** The rectangle angle changed in 3D during parcel creation (empty_area). */
export const IPC_EVENT_EDIT_PARCEL_UPDATE_ROTATION = "edit_parcel:update_rotation";

/** Response to the display options panel's state query. */
export const IPC_EVENT_VIEW_CURRENT_STATE = "view:current_state";

/**
 * Current per-object visibility filter (Unity → UI). Payload: { hidden_building_ids: number[], hidden_path_ids:
 * number[] }. Sent after sendSetObjectFilter() or in response to sendQueryObjectFilter(). Unity owns visibility;
 * panels use this to stay synchronized.
 */
export const IPC_EVENT_VIEW_OBJECT_FILTER_STATE = "view:object_filter_state";

/**
 * 3D camera state (Unity → UI), replying to sendCameraQueryState(). Payload: { position: [x,y,z], yaw: number },
 * with yaw in clockwise degrees from north (world +Z)=0. The 2D city map overlays the camera marker from this.
 */
export const IPC_EVENT_CAMERA_STATE = "camera:state";

/** The edited polygon changed in parcel shape editing mode. */
export const IPC_EVENT_EDIT_PARCEL_UPDATE_SHAPE = "edit_parcel:update_shape";

/** Selected district changed in district editing mode (Unity → UI). zone_id=0 means no selection. */
export const IPC_EVENT_EDIT_ZONE_SELECTED = "edit_zone:selected";

/**
 * District merge source/destination selected (Unity → UI). Payload: { target_zone_id, target_zone_name,
 * source_zone_id, source_zone_name }. Since merging irreversibly removes a district, Unity does not submit. The
 * icon panel opens zone_merge, a small confirmation-only window; the UI submits from its confirmation button and
 * returns the result through sendEditZoneFinishMerge.
 */
export const IPC_EVENT_EDIT_ZONE_REQUEST_MERGE = "edit_zone:request_merge";

/** An object was selected in the 3D view. */
export const IPC_EVENT_WORLD_SELECT_OBJECT = "world:select_object";

/** Grid angle changed through two-point selection. */
export const IPC_EVENT_VIEW_GRID_ANGLE_CHANGED = "view:grid_angle_changed";

/**
 * Grid visibility toggled in the 3D radial menu (Unity → UI). Payload: { visible: boolean }. Unity links grid
 * visibility and snapping; the road construction panel only follows visibility for its grid settings UI.
 */
export const IPC_EVENT_VIEW_GRID_TOGGLED = "view:grid_toggled";

/** The edited polygon changed in resource editing mode. */
export const IPC_EVENT_EDIT_RESOURCE_UPDATE_SHAPE = "edit_resource:update_shape";

/**
 * The earthworks area polygon changed during drawing (Unity → UI). Payload: { polygon: number[][], finalized:
 * boolean }, with [x, z] vertices. finalized becomes true only after closing the area and entering vertex
 * editing; GradingPanel requests an estimate only then.
 */
export const IPC_EVENT_EDIT_GRADING_UPDATE_SHAPE = "edit_grading:update_shape";

/**
 * Line tour points (stops/waypoints) changed during public transport placement (Unity → UI). Payload: { points:
 * TransitRoutePoint[] } in traversal order; each point is {kind: "stop" | "waypoint", position: [x, y, z]}.
 * Public transport paths use relative Y=0, so y is zero. TransitCreationPanel updates point counts and the
 * points sent on confirmation.
 */
export const IPC_EVENT_EDIT_TRANSIT_UPDATE_POINTS = "edit_transit:update_points";

/**
 * Placed instances changed in individual prop placement (Unity → UI). Payload: { instances: [{prop_type, x, y,
 * z, rotation, scale}] }. y is ground-relative height in m. Includes tentative placements from before a type
 * change. PropPlacementPanel updates the count and POST instance list.
 */
export const IPC_EVENT_EDIT_PROP_UPDATE_INSTANCES = "edit_prop:update_instances";

/**
 * Scatter area polygon changed in area prop placement (Unity → UI). Payload: { polygon: number[][] }, with [x,
 * z] vertices, matching edit_resource:update_shape. PropPlacementPanel updates vertex count and the POST
 * polygon.
 */
export const IPC_EVENT_EDIT_PROP_UPDATE_SHAPE = "edit_prop:update_shape";

/**
 * 3D controls (middle-button rotation or PageUp/PageDown height changes) changed the next ghost's settings in
 * individual prop placement (Unity → UI). Payload: { rotation, height }. PropPlacementPanel only updates
 * steppers without echoing edit_prop:change_settings, as with direct building placement's
 * edit_building:update_direct_angle.
 */
export const IPC_EVENT_EDIT_PROP_PLACEMENT_SETTINGS = "edit_prop:placement_settings";

/**
 * In bulldozer mode, an object requiring confirmation was clicked (Unity → UI, addressed to cockpit). Payload: {
 * object_type, id, prop_type }. Currently only "prop_scatter" (irreversible removal of an entire scatter area
 * such as a forest). The cockpit shows confirmation and replies via sendRemoveObjectConfirmResult.
 */
export const IPC_EVENT_REMOVE_OBJECT_REQUEST_CONFIRM = "remove_object:request_confirm";

/** Rust retains Unity's initial rendering completion, independent of Tips subscription order. */
export function getInitialLoadingReady(): Promise<boolean> {
  return invoke<boolean>("get_initial_loading_ready");
}

export function startGameFromTips(): Promise<void> {
  return invoke("close_tips_window");
}
