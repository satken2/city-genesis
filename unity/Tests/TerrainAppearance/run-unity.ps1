$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$qaRoot = Join-Path $unityRoot 'Temp/TerrainAppearanceQA'
foreach ($folder in @('Assets/Editor', 'Assets/Resources/Shaders', 'Assets/Shaders', 'Assets/Settings', 'Packages', 'ProjectSettings')) {
    New-Item -ItemType Directory -Path (Join-Path $qaRoot $folder) -Force | Out-Null
}
Copy-Item "$PSScriptRoot/TerrainAppearanceQA.cs" "$qaRoot/Assets/Editor" -Force
foreach ($name in @('DepthOffsetTerrainLit.shader', 'DepthOffsetSimpleLitFar.shader')) {
    Copy-Item "$unityRoot/Assets/Resources/Shaders/$name" "$qaRoot/Assets/Resources/Shaders" -Force
}
foreach ($name in @('TerrainSurfaceAppearance.hlsl', 'DepthOffsetTerrainLitMacroVariation.hlsl', 'TerrainCliffRock.hlsl')) {
    Copy-Item "$unityRoot/Assets/Shaders/$name" "$qaRoot/Assets/Shaders" -Force
}
foreach ($name in @('PC_RPAsset.asset', 'PC_Renderer.asset', 'DefaultVolumeProfile.asset', 'UniversalRenderPipelineGlobalSettings.asset')) {
    Copy-Item "$unityRoot/Assets/Settings/$name*" "$qaRoot/Assets/Settings" -Force
}
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings" -Force
$manifest = Get-Content "$unityRoot/Packages/manifest.json" -Raw | ConvertFrom-Json -AsHashtable
$manifest.dependencies.Remove('com.rlabrecque.steamworks.net')
$manifest.dependencies.Remove('com.siccity.gltfutility')
$manifest | ConvertTo-Json -Depth 10 | Set-Content "$qaRoot/Packages/manifest.json"
Set-Content "$qaRoot/result.txt" 'RUNNING'
$unityExe = 'C:/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor/Unity.exe'
$process = Start-Process -FilePath $unityExe -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'TerrainAppearanceQA.Run', '-logFile', ('"' + $qaRoot + '/qa.log"')) -WindowStyle Hidden -PassThru
Write-Output "Terrain appearance QA process: $($process.Id); results: $qaRoot/result.txt"
