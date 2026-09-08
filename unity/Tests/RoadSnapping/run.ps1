$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $unityRoot 'Tests/BuildRuntime.ps1')
[xml]$projectXml = Get-Content -LiteralPath (Join-Path $unityRoot 'Assembly-CSharp.csproj') -Raw
$coreReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
& dotnet run --project (Join-Path $PSScriptRoot 'RoadSnapping.csproj') "-p:UnityCorePath=$($coreReference.HintPath)" -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Road snapping checks failed' }
