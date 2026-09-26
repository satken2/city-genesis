$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$qaRoot = Join-Path $unityRoot 'Temp/PavingUnityQA'
foreach ($folder in @('Assets/Editor', 'Assets/Resources/Shaders', 'Assets/Models/Paths/Textures', 'Assets/Models/Paths/p301', 'Assets/Settings', 'Packages', 'ProjectSettings')) {
    New-Item -ItemType Directory -Path (Join-Path $qaRoot $folder) -Force | Out-Null
}
Copy-Item "$PSScriptRoot/PavingQA.cs" "$qaRoot/Assets/Editor" -Force
Copy-Item "$unityRoot/Assets/Resources/Shaders/PathPaving.shader*" "$qaRoot/Assets/Resources/Shaders" -Force
Copy-Item "$unityRoot/Assets/Resources/PATH_PAVING_STONE.mat*" "$qaRoot/Assets/Resources" -Force
Copy-Item "$unityRoot/Assets/Models/Paths/Textures/PATH_PAVING_STONE_*.png*" "$qaRoot/Assets/Models/Paths/Textures" -Force
Copy-Item "$unityRoot/Assets/Models/Paths/p301/body*.fbx*" "$qaRoot/Assets/Models/Paths/p301" -Force
foreach ($name in @('PC_RPAsset.asset', 'PC_Renderer.asset', 'DefaultVolumeProfile.asset', 'UniversalRenderPipelineGlobalSettings.asset')) {
    Copy-Item "$unityRoot/Assets/Settings/$name*" "$qaRoot/Assets/Settings" -Force
}
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings" -Force
$manifest = Get-Content "$unityRoot/Packages/manifest.json" -Raw | ConvertFrom-Json
$manifest.dependencies.PSObject.Properties.Remove('com.rlabrecque.steamworks.net')
$manifest.dependencies.PSObject.Properties.Remove('com.siccity.gltfutility')
$manifest | ConvertTo-Json -Depth 10 | Set-Content "$qaRoot/Packages/manifest.json"
Set-Content "$qaRoot/result.txt" 'RUNNING'
$unityExe = 'C:/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor/Unity.exe'
$process = Start-Process -FilePath $unityExe -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'PavingQA.Run', '-logFile', ('"' + $qaRoot + '/qa.log"')) -WindowStyle Hidden -PassThru
Write-Output "Paving QA process: $($process.Id); results: $qaRoot/result.txt"
