$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$qaRoot = Join-Path $unityRoot 'Library/WaterFogQA'
foreach ($folder in @('Assets/Editor', 'Assets/Resources/Shaders', 'Assets/Shaders', 'Assets/Settings', 'Packages', 'ProjectSettings')) {
    New-Item -ItemType Directory -Path (Join-Path $qaRoot $folder) -Force | Out-Null
}
Copy-Item "$PSScriptRoot/WaterFogQA.cs" "$qaRoot/Assets/Editor" -Force
Copy-Item "$unityRoot/Assets/Resources/Shaders/WaterLit.shader" "$qaRoot/Assets/Resources/Shaders" -Force
Copy-Item "$unityRoot/Assets/Shaders/Water.hlsl" "$qaRoot/Assets/Shaders" -Force
foreach ($name in @('PC_RPAsset.asset', 'PC_Renderer.asset', 'DefaultVolumeProfile.asset', 'UniversalRenderPipelineGlobalSettings.asset')) {
    Copy-Item "$unityRoot/Assets/Settings/$name*" "$qaRoot/Assets/Settings" -Force
}
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings" -Force
$manifest = Get-Content "$unityRoot/Packages/manifest.json" -Raw | ConvertFrom-Json
@{ dependencies = @{ 'com.unity.render-pipelines.universal' = $manifest.dependencies.'com.unity.render-pipelines.universal' } } | ConvertTo-Json -Depth 5 | Set-Content "$qaRoot/Packages/manifest.json"
Set-Content "$qaRoot/result.txt" 'RUNNING'
$unityExe = 'C:/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor/Unity.exe'
$process = Start-Process -FilePath $unityExe -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'WaterFogQA.Run', '-logFile', ('"' + $qaRoot + '/qa.log"')) -WindowStyle Hidden -PassThru
Write-Output "Water fog QA process: $($process.Id); results: $qaRoot/result.txt"
