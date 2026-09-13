$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $unityRoot 'Tests/BuildRuntime.ps1')
[xml]$project = Get-Content -LiteralPath (Join-Path $unityRoot 'Assembly-CSharp.csproj') -Raw
$reference = $project.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
$editorRoot = $reference.HintPath -replace '[\\/]Data[\\/].*$', ''
$qa = Join-Path $unityRoot 'Temp/OccupancyQA'
New-Item -ItemType Directory -Path "$qa/Assets/Editor", "$qa/ProjectSettings" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qa/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item -LiteralPath "$PSScriptRoot/OccupancyQA.cs" -Destination "$qa/Assets/Editor/OccupancyQA.cs" -Force
$process = Start-Process -FilePath (Join-Path $editorRoot 'Unity.exe') -ArgumentList @(
    '-batchmode', '-projectPath', ('"' + $qa + '"'), '-executeMethod', 'OccupancyQA.Run',
    '-occupancyRoot', ('"' + $unityRoot + '"'), '-logFile', ('"' + "$qa/qa.log" + '"')
) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Occupancy QA timed out' }
Get-Content -LiteralPath "$qa/result.txt"
if ($process.ExitCode -ne 0) { exit 1 }
