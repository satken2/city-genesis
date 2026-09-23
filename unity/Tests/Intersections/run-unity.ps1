param([string]$UnityExe, [switch]$PavedModels, [switch]$Equipment, [switch]$Platforms, [switch]$PlatformEnds, [switch]$RailYard, [switch]$StationHouse, [switch]$LandmarkPaths, [switch]$SyncLandmarkPrefabs)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $unityRoot 'Assembly-CSharp.csproj'
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (!$UnityExe) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $coreReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $editorRoot = $coreReference.HintPath -replace '[\\/]Data[\\/].*$', ''
    $UnityExe = Join-Path $editorRoot 'Unity.exe'
}
if (!(Test-Path -LiteralPath $UnityExe)) { throw "Unity.exe not found: $UnityExe" }
$qaRoot = Join-Path $unityRoot 'Temp/IntersectionUnityQA'
$editorAssets = Join-Path $qaRoot 'Assets/Editor'
New-Item -ItemType Directory -Path $editorAssets -Force | Out-Null
New-Item -ItemType Directory -Path "$qaRoot/ProjectSettings" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Unity/IntersectionQA.cs') -Destination $editorAssets -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Unity/MotorwayMergeQA.cs') -Destination $editorAssets -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Unity/RoadEquipmentQA.cs') -Destination $editorAssets -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Unity/PlatformQA.cs') -Destination $editorAssets -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Unity/RailYardQA.cs') -Destination $editorAssets -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Unity/StationHouseQA.cs') -Destination $editorAssets -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Unity/LandmarkPathsQA.cs') -Destination $editorAssets -Force
Copy-Item -LiteralPath "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs" -Destination $editorAssets -Force
if ($StationHouse) {
    Copy-Item -LiteralPath "$unityRoot/ProjectSettings/TagManager.asset" -Destination "$qaRoot/ProjectSettings/TagManager.asset" -Force
}
if ($RailYard) {
    foreach ($variant in @(1060,1064)) {
        $yardAssets = Join-Path $qaRoot "Assets/Buildings/a$variant"
        New-Item -ItemType Directory -Path $yardAssets -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $unityRoot "Assets/Models/Buildings/a$variant/0") -Destination $yardAssets -Recurse -Force
    }
}
$pathAssets = Join-Path $qaRoot 'Assets/Paths'
New-Item -ItemType Directory -Path $pathAssets -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $unityRoot 'Assets/Models/Paths') | Copy-Item -Destination $pathAssets -Recurse -Force
$resultPath = Join-Path $qaRoot 'result.txt'
Set-Content -LiteralPath $resultPath -Value 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @(
    '-batchmode', '-projectPath', ('"' + $qaRoot + '"'),
    '-executeMethod', $(if ($LandmarkPaths) { 'LandmarkPathsQA.Run' } elseif ($StationHouse) { 'StationHouseQA.Run' } elseif ($RailYard) { 'RailYardQA.Run' } elseif ($Platforms -or $PlatformEnds) { 'PlatformQA.Run' } elseif ($Equipment) { 'RoadEquipmentQA.Run' } else { 'IntersectionQA.Run' }), '-intersectionSourceRoot', ('"' + $unityRoot + '"'),
    $(if ($PlatformEnds) { '-platformEndsOnly' } else { '-platformFull' }),
    '-logFile', ('"' + (Join-Path $qaRoot 'qa.log') + '"'), $(if ($PavedModels) { '-pavedModels' } else { '-defaultModels' }), $(if ($SyncLandmarkPrefabs) { '-syncLandmarkPrefabs' } else { '-noPrefabSync' })
) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Intersection QA timed out; inspect Temp/IntersectionUnityQA/qa.log' }
$result = Get-Content -LiteralPath $resultPath -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { exit 1 }
