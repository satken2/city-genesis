$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
$qaRoot = Join-Path $unityRoot 'Temp/ExplosionQA'
foreach ($folder in @('Assets/Runtime', 'Assets/Editor', 'Assets/Resources/Shaders', 'Assets/Resources/Audio/Effects', 'Assets/Settings', 'Packages', 'ProjectSettings', 'Captures')) {
    New-Item -ItemType Directory -Path (Join-Path $qaRoot $folder) -Force | Out-Null
}
foreach ($name in @('FireEffectFactory', 'VisualEffectManager')) {
    Copy-Item "$unityRoot/Assets/Scripts/Utils/$name.cs" "$qaRoot/Assets/Runtime" -Force
}
Copy-Item "$unityRoot/Assets/Resources/Shaders/FireParticle.shader" "$qaRoot/Assets/Resources/Shaders" -Force
Copy-Item "$unityRoot/Assets/Resources/Audio/Effects/*.ogg*" "$qaRoot/Assets/Resources/Audio/Effects" -Force
Copy-Item "$PSScriptRoot/ExplosionQaHost.cs" "$qaRoot/Assets/Runtime" -Force
Copy-Item "$PSScriptRoot/ExplosionQA.cs" "$qaRoot/Assets/Editor" -Force
foreach ($name in @('PC_RPAsset.asset', 'PC_Renderer.asset', 'DefaultVolumeProfile.asset', 'UniversalRenderPipelineGlobalSettings.asset')) {
    Copy-Item "$unityRoot/Assets/Settings/$name*" "$qaRoot/Assets/Settings" -Force
}
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings" -Force
$manifest = Get-Content "$unityRoot/Packages/manifest.json" -Raw | ConvertFrom-Json -AsHashtable
[void]$manifest.dependencies.Remove('com.rlabrecque.steamworks.net')
[void]$manifest.dependencies.Remove('com.siccity.gltfutility')
$manifest | ConvertTo-Json -Depth 10 | Set-Content "$qaRoot/Packages/manifest.json"
Set-Content "$qaRoot/result.txt" 'RUNNING'
$process = Start-Process -FilePath 'C:/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor/Unity.exe' -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'ExplosionQA.Run', '-logFile', ('"' + $qaRoot + '/qa.log"')) -WindowStyle Hidden -PassThru
Write-Output "Explosion QA process: $($process.Id); results: $qaRoot/result.txt"
