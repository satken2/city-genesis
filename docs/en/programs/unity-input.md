# Unity input

Unity's input system routes 3D interactions according to target and editing purpose. Input modes determine interaction meaning; [visual modes](unity-visual-modes.md) determine appearance. These are separate responsibilities.

## Input and editing

A common entry point receives input and dispatches it to target-specific handlers. Handlers notify detail views or delegate to editing managers, which own geometry, selection, and previews.

Each target type has its own input mode. The editing purpose can change what a click means, and unrelated target types must not intercept input. Mode presets switch these modes together.

## Selection and camera interaction

Common arbitration distinguishes clicks from drags so one gesture cannot trigger both editing and camera movement. Edit handles and overlay selection participate in this arbitration.

Input suspension caused by UI interaction, camera operation, connection loss, or tutorial restrictions is distinct from changing input modes. Discard pending gestures on suspension so an old click cannot commit after resumption. Observation camera modes are also separate from city-editing states.

## UI boundary

The UI supplies an editing state and initial conditions; Unity switches input, display, and editing state together. Unity owns geometry, and panel numeric inputs feed the same editing path as 3D interaction. Follow [IPC](ipc.md) for notifications, submission, and cancellation responsibilities.

Previews aid interaction; the core decides whether confirmation is allowed. Estimated warnings are distinct from definitive core constraints. Snapping and placement use [shared coordinates and heights](../geometries/A_path-geometry.md), rather than deriving separate definitions from rendered positions.

