# Visual Mode Control System

This document outlines the system for controlling the visual representation of objects (Buildings, Paths, Parcels, etc.) in the application.

## Overview

The visual state of objects is managed centrally by `VisualModeHandler` and individually by `VisualModeObject` components attached to each GameObject. This ensures consistent behavior for highlighting, mode switching (Normal, Plan, Construction), and material management.

## Components

### 1. VisualModeHandler
-   **Role**: Global manager that maintains the current `VisualMode` for each `InputObjectType`.
-   **Functionality**:
    -   Stores current mode (e.g., `MODE_NORMAL`, `MODE_PLAN`) for each type.
    -   Notifies registered `VisualModeObject`s when the mode changes.
    -   Supports batch processing for performance.

### 2. VisualModeObject
-   **Role**: Component attached to GameObjects (Buildings, Paths) to handle their specific visual updates.
-   **Functionality**:
    -   **Material Management**: Stores original materials (supporting submeshes) and swaps them based on state.
    -   **Renderer Configuration**:
        -   Can be configured to apply effects to **child renderers** (`useChildRenderers`).
        -   Can be configured to **ignore the self renderer** (`ignoreSelfRenderer`), useful when the main object has an invisible collider mesh.
    -   **States**:
        -   `Preview`: Used when placing an object. Shows `PreviewMaterial`.
        -   `Construction`: Shows `ConstructionMaterial`.
        -   `Pending`/`Planned`: Shows `PendingMaterial`/`PlannedMaterial` ONLY in `MODE_PLAN`. Hidden otherwise.
        -   `Active`: Normal display.
    -   **Hover**: Handles mouseover highlighting by applying `HighlightMaterial`.

### 3. MeshFactory
-   **Role**: Generates meshes for objects.
-   **Functionality**:
    -   `GenerateBuildingMesh`: Generates the mesh. Now supports an `isConstruction` flag to generate specific construction meshes (e.g., scaffolding) if needed.
    -   **Note**: Material replacement for previews is NO LONGER handled here. It is handled by `VisualModeObject`.

## Visual Modes

| Mode | Description | Behavior |
| :--- | :--- | :--- |
| `MODE_NORMAL` | Standard view | Active objects visible. Planned objects hidden. |
| `MODE_PLAN` | Planning view | Active objects visible. Planned/Pending objects visible with specific materials. |
| `MODE_HIDDEN` | Hide all | Objects hidden. |
| `MODE_HIGHLIGHT_OWNER` | Owner view | Highlights objects owned by the player/city. |
| `MODE_BOUNDARY` | Boundary view | Used for Parcels to show outlines. |

## Usage

### Setting up a new Object
1.  Attach `VisualModeObject` to the GameObject.
2.  Set `objectType` (e.g., `InputObjectType.BUILDING`).
3.  Ensure `Renderer` is present.
4.  (Optional) Assign custom materials in the Inspector if defaults are not sufficient.

### Handling Preview Objects
When creating a preview object (e.g., following the mouse cursor):
```csharp
var vmo = previewObject.GetComponent<VisualModeObject>();
if (vmo != null) {
    vmo.SetPreviewMode(true);
}
```

### Handling Hover
InputHandlers (e.g., `BuildingInputHandler`) should delegate hover events:
```csharp
public void onHoverStart(int mode) {
    visualModeObject.SetHover(true);
}

public void onHoverEnd(int mode) {
    visualModeObject.SetHover(false);
}
```
