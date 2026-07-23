# Input Control System

This document outlines the system for handling user input (mouse clicks, hovers, drags) in the application.

## Overview

Input handling is centralized in the `InputHandler` component, which performs raycasting and dispatches events to specific objects based on their type and the current interaction mode. This separates the input detection logic from the specific behavior of each object.

## Core Components

### 1. InputHandler
- **Role**: Global manager (Singleton).
- **Location**: `Assets/Scripts/InputHandlers/InputHandler.cs`
- **Functionality**:
    - **Raycasting**: Performs Raycasting from the main camera to detect objects and terrain under the mouse cursor.
    - **Mode Management**: Maintains the current `InputMode` for each `InputObjectType`.
    - **Event Dispatch**: Dispatches events (`onClick`, `onRightClick`, `onHoverStart`, `onHoverEnd`, `onDragStart`, `onDragUpdate`, `onDragEnd`) to the specific handler components attached to objects.
    - **Terrain Handling**: Handles terrain interaction (position calculation) separately from object interaction to ensure mouse position is always available.

### 2. InputObjectType
- **Role**: Defines the types of interactable objects.
- **Constants**:
    - `BUILDING` (1)
    - `PARCEL` (2)
    - `PATH` (3)
    - `NODE` (4)
    - `TERRAIN` (5)
    - `ZONE` (6)

### 3. InputMode
- **Role**: Defines the current state of interaction.
- **Constants**:
    - `MODE_NONE`: No interaction.
    - `MODE_NORMAL`: Standard interaction (select, view info).
    - `MODE_ROAD_CONSTRUCTION`: For placing roads.
    - `MODE_BUILDING_CONSTRUCTION`: For placing/demolishing buildings.
    - `MODE_PARCEL_CREATION_*`: Various modes for creating parcels.
    - `MODE_EDIT`: General edit mode.
- **Behavior**: Each object type can be in a different mode simultaneously. For example, during road construction, `TERRAIN` might be in `MODE_ROAD_CONSTRUCTION` while `BUILDING` is in `MODE_NONE` to prevent accidental clicks.

### 4. Specific Input Handlers
- **Role**: Components attached to GameObjects to handle events dispatched by `InputHandler`.
- **Examples**:
    - `BuildingInputHandler`
    - `PathInputHandler`
    - `TerrainInputHandler`
    - `NodeInputHandler`
    - `ParcelInputHandler`
- **Pattern**: These classes do not inherit from a common base class but implicitly implement a set of methods that `InputHandler` calls via `GetComponent`.

## Event Flow

1.  **Raycast**: `InputHandler.Update()` performs a raycast every frame.
2.  **Hit Detection**:
    - If an object is hit, `InputHandler.GetObjectType()` determines its type based on name prefix or components.
    - If no object is hit, it falls back to checking for Terrain.
3.  **Mode Retrieval**: `InputHandler` looks up the current mode for that object type using `GetModeForObjectType(objectType)`.
4.  **Dispatch**:
    - **Hover**:
        - `CallObjectHoverStart`: Called when the mouse enters an object.
        - `CallObjectHoverEnd`: Called when the mouse leaves an object.
        - `CallObjectHover`: Called every frame while hovering.
    - **Click**:
        - `CallObjectClick`: Called on left mouse button down. Passes the current `mode` to the handler's `onClick(int mode)` method.
    - **Right Click**:
        - `CallObjectRightClick`: Called on right mouse button down.
    - **Drag**:
        - `StartDrag` / `UpdateDrag` / `EndDrag`: Manages drag state. For some objects (Nodes, Zone Vertices), it delegates to the handler. For others (Terrain), it might notify a manager like `RoadPlanManager`.

## Implementing a New Handler

To make a new object type interactable:

1.  **Create a Handler Script**: Create a script that implements the expected methods.
    ```csharp
    public class MyObjectInputHandler : MonoBehaviour
    {
        public void onClick(int mode)
        {
            switch (mode)
            {
                case InputMode.MODE_NORMAL:
                    // Handle normal click
                    break;
                // Handle other modes
            }
        }

        public void onHoverStart(int mode)
        {
            // Trigger visual feedback
        }

        public void onHoverEnd(int mode)
        {
            // Stop visual feedback
        }
        
        // Implement onRightClick, onDragStart, etc. if needed
    }
    ```
2.  **Register Type**:
    - Add a new constant to `InputObjectType`.
    - Update `InputHandler.GetObjectType()` to recognize the new object (e.g., by checking `name.StartsWith("MyObject_")`).
    - Update `InputHandler` dispatch methods (`CallObjectClick`, etc.) to include a case for the new type.
3.  **Attach Component**: Attach the handler script to the GameObject.

## Interaction with Visual System

Input handlers typically delegate visual feedback (highlighting) to the `VisualModeObject` component, maintaining separation of concerns.

```csharp
private VisualModeObject visualModeObject;

private void Start() {
    visualModeObject = GetComponent<VisualModeObject>();
}

public void onHoverStart(int mode) {
    if (visualModeObject != null) {
        visualModeObject.SetHover(true);
    }
}
```

This ensures that the visual representation (handled by `VisualModeObject`) is synchronized with the input state (handled by `InputHandler`).
