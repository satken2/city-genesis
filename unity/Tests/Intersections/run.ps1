$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $unityRoot 'Assembly-CSharp.csproj'
& dotnet build $project -v quiet /clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$coreReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
$jsonReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'Newtonsoft.Json' }
& dotnet run --project (Join-Path $PSScriptRoot 'Intersections.csproj') "-p:UnityCorePath=$($coreReference.HintPath)" "-p:NewtonsoftPath=$(Join-Path $unityRoot $jsonReference.HintPath)" -v quiet
exit $LASTEXITCODE
