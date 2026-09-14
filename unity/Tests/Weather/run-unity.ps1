$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
$qaRoot = Join-Path $unityRoot 'Temp/WeatherUnityQA'
foreach ($folder in @('Assets/Resources/Audio/Ambience', 'Assets/Resources/Audio/Ambience', 'Assets/Runtime', 'Assets/Editor', 'Assets/Resources/Weather', 'Assets/Settings', 'Packages', 'ProjectSettings', 'Captures')) {
    New-Item -ItemType Directory -Path (Join-Path $qaRoot $folder) -Force | Out-Null
}
Copy-Item "$unityRoot/Assets/Resources/Audio/Ambience/*.ogg*" "$qaRoot/Assets/Resources/Audio/Ambience" -Force
Copy-Item "$unityRoot/Assets/Resources/Audio/Ambience/*.ogg*" "$qaRoot/Assets/Resources/Audio/Ambience" -Force
Copy-Item "$unityRoot/Assets/Scripts/Weather/*.cs" "$qaRoot/Assets/Runtime" -Force
Copy-Item "$unityRoot/Assets/Scripts/UI/UiFontProvider.cs" "$qaRoot/Assets/Runtime" -Force
Copy-Item "$unityRoot/Assets/Resources/Weather/*.shader" "$qaRoot/Assets/Resources/Weather" -Force
Copy-Item "$PSScriptRoot/WeatherQaHost.cs" "$qaRoot/Assets/Runtime" -Force
Copy-Item "$PSScriptRoot/WeatherQA.cs" "$qaRoot/Assets/Editor" -Force
foreach ($name in @('PC_RPAsset.asset', 'PC_Renderer.asset', 'DefaultVolumeProfile.asset', 'UniversalRenderPipelineGlobalSettings.asset')) {
    Copy-Item "$unityRoot/Assets/Settings/$name" "$qaRoot/Assets/Settings" -Force
    Copy-Item "$unityRoot/Assets/Settings/$name.meta" "$qaRoot/Assets/Settings" -Force
}
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings" -Force
$manifest = Get-Content "$unityRoot/Packages/manifest.json" -Raw | ConvertFrom-Json -AsHashtable
$manifest.dependencies.Remove('com.rlabrecque.steamworks.net')
$manifest.dependencies.Remove('com.siccity.gltfutility')
$manifest.dependencies['com.unity.nuget.newtonsoft-json'] = '3.2.2'
$manifest | ConvertTo-Json -Depth 10 | Set-Content "$qaRoot/Packages/manifest.json"
Set-Content "$qaRoot/result.txt" 'RUNNING'
$unityExe = 'C:/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor/Unity.exe'
$process = Start-Process -FilePath $unityExe -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'WeatherQA.Run', '-logFile', ('"' + $qaRoot + '/qa.log"')) -WindowStyle Hidden -PassThru
Write-Output "Weather QA process: $($process.Id); results: $qaRoot/result.txt"
