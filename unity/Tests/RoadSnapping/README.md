# Road snapping checks

[日本語](README.ja.md)

Checks parallel and endpoint-aligned snapping, projections in `PolylineUtils` and `RailJunctionGeometry`, and the distinction between rail snap candidates and infrastructure connections.

## Prerequisites

- The Unity Editor version specified in `unity/ProjectSettings/ProjectVersion.txt`.
- .NET 8 SDK.

After cloning, open `unity/` in the Unity Editor and generate its C# project files. Check that `unity/Assembly-CSharp.csproj` exists. Unity assemblies are referenced from the locally installed Editor.

## Run

From this directory:

```powershell
./run.ps1
```

`run.ps1` builds the runtime assembly and the test, regenerating the required DLLs in `bin/` and `obj/`. These generated files are excluded from Git. The test runs on .NET without launching Unity.

Results are printed to the console.
