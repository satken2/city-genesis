$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$qaRoot = Join-Path $unityRoot 'Temp/VehicleAudioQA'
foreach ($folder in @('Assets/Resources/Audio/Vehicles', 'Assets/Runtime', 'Packages', 'ProjectSettings')) {
    New-Item -ItemType Directory -Path (Join-Path $qaRoot $folder) -Force | Out-Null
}
Copy-Item "$unityRoot/Assets/Resources/Audio/Vehicles/*.ogg*" "$qaRoot/Assets/Resources/Audio/Vehicles" -Force
Copy-Item "$unityRoot/Assets/Scripts/Weather/VehicleAmbience.cs" "$qaRoot/Assets/Runtime" -Force
Copy-Item "$PSScriptRoot/VehicleAudioQA.cs" "$qaRoot/Assets/Runtime" -Force
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings" -Force
Set-Content "$qaRoot/Packages/manifest.json" '{"dependencies":{"com.unity.modules.audio":"1.0.0"}}'
Set-Content "$qaRoot/result.txt" 'RUNNING'
$process = Start-Process -FilePath 'C:/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor/Unity.exe' -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'VehicleAudioQA.Run', '-logFile', ('"' + $qaRoot + '/qa.log"')) -WindowStyle Hidden -PassThru
Write-Output "Vehicle audio QA process: $($process.Id); results: $qaRoot/result.txt"
