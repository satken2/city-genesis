param([string]$UnityExe, [switch]$PedestrianModelsOnly, [switch]$TransitOnly)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $unityRoot 'Assembly-CSharp.csproj'
& (Join-Path $unityRoot 'Tests/BuildRuntime.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (!$UnityExe) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $core = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path ($core.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/VehicleUnityQA'
$editorAssets = Join-Path $qaRoot 'Assets/Editor'
New-Item -ItemType Directory -Path $editorAssets -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'VehicleQA.cs') -Destination $editorAssets -Force
Copy-Item -LiteralPath (Join-Path $unityRoot 'Assets/Editor/ModelPrefabGenerator.cs') -Destination $editorAssets -Force
Copy-Item -LiteralPath (Join-Path $unityRoot 'Assets/Editor/ModelImportSettings.cs') -Destination $editorAssets -Force
foreach ($vehicleType in @('10','11','12','13','20','21','22','23')) {
    foreach ($relativeRoot in @('Assets/Models/Vehicles','Assets/Resources/Models/Vehicles')) {
        $destination = Join-Path $qaRoot $relativeRoot
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $unityRoot "$relativeRoot/$vehicleType") -Destination $destination -Recurse -Force
    }
}
foreach ($relativeRoot in @('Assets/Models/Props','Assets/Resources/Models/Props')) {
    $destination = Join-Path $qaRoot $relativeRoot
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $unityRoot $relativeRoot) -Filter 'pedestrian_*' -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}
$configDestination = Join-Path $qaRoot 'Assets/Resources/Config'
New-Item -ItemType Directory -Path $configDestination -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $unityRoot 'Assets/Resources/Config/transit_vehicle_models.json') -Destination $configDestination -Force
Copy-Item -LiteralPath (Join-Path $unityRoot 'Assets/Resources/Config/vehicle_model_config.yaml') -Destination $configDestination -Force
$resultPath = Join-Path $qaRoot 'result.txt' 
Set-Content -LiteralPath $resultPath -Value 'RUNNING'
$qaArguments = @(
    '-batchmode', '-projectPath', ('"' + $qaRoot + '"'),
    '-executeMethod', 'VehicleQA.Run', '-vehicleSourceRoot', ('"' + $unityRoot + '"'),
    '-logFile', ('"' + (Join-Path $qaRoot 'qa.log') + '"')
)
if ($PedestrianModelsOnly) { $qaArguments += '-pedestrianModelsOnly' }
if ($TransitOnly) { $qaArguments += '-transitOnly' }
$process = Start-Process -FilePath $UnityExe -ArgumentList $qaArguments -WindowStyle Hidden -PassThru
$process.WaitForExit()
$result = Get-Content -LiteralPath $resultPath -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { exit 1 }
