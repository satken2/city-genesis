param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$projectXml = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
    $core = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' } | Select-Object -First 1
    $UnityExe = Join-Path ($core.HintPath -replace '[\/]Data[\/].*$', '') 'Unity.exe'
}
if (!(Test-Path -LiteralPath $UnityExe)) { throw "Unity.exe not found: $UnityExe" }
$qaRoot = Join-Path $unityRoot 'Temp/RoadsideStoreQA'
$buildings = 'Assets/Models/Buildings'
foreach ($folder in @('Assets/Editor', $buildings, 'Assets/Settings', 'Assets/Resources/Config', 'Packages', 'ProjectSettings')) {
    New-Item -ItemType Directory -Path (Join-Path $qaRoot $folder) -Force | Out-Null
}
Copy-Item "$PSScriptRoot/RoadsideStoreQA.cs" "$qaRoot/Assets/Editor" -Force
# The game resolves parts through the weights table and the mirror prefabs, which the standard generator makes here from the FBXs.
Copy-Item "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs", "$unityRoot/Assets/Editor/ModelImportSettings.cs" "$qaRoot/Assets/Editor" -Force
Copy-Item -LiteralPath "$unityRoot/Assets/Resources/Config/model_weights.json" -Destination "$qaRoot/Assets/Resources/Config" -Force
foreach ($stale in "$qaRoot/$buildings/a2020", "$qaRoot/Assets/Resources/Models") {
    if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Recurse -Force }
}
Copy-Item "$unityRoot/$buildings/a2020" "$qaRoot/$buildings" -Recurse -Force
foreach ($name in @('PC_RPAsset.asset', 'PC_Renderer.asset', 'DefaultVolumeProfile.asset', 'UniversalRenderPipelineGlobalSettings.asset')) {
    Copy-Item "$unityRoot/Assets/Settings/$name*" "$qaRoot/Assets/Settings" -Force
}
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings" -Force
# The foundation's concrete and the fallback building material.
Copy-Item "$unityRoot/Assets/Resources/COMMON_CONCRETE.mat*", "$unityRoot/Assets/Resources/BuildingMaterial.mat*" "$qaRoot/Assets/Resources" -Force
# The shared leaf materials of the planting, with their shader and color texture.
Copy-Item -LiteralPath "$unityRoot/Assets/Models/Materials" -Destination "$qaRoot/Assets/Models" -Recurse -Force
New-Item -ItemType Directory -Path "$qaRoot/Assets/Resources/Shaders" -Force | Out-Null
Copy-Item "$unityRoot/Assets/Resources/Shaders/Foliage.shader*" "$qaRoot/Assets/Resources/Shaders" -Force
New-Item -ItemType Directory -Path "$qaRoot/Assets/Models/Props/Textures" -Force | Out-Null
Copy-Item "$unityRoot/Assets/Models/Props/Textures/PROP_LEAF_GREEN_color.png*" "$qaRoot/Assets/Models/Props/Textures" -Force
# The game's depth offsets: buildings use Custom/DepthOffsetLit (-1,-1); the same shader with (1,1) stands in for the terrain shader.
New-Item -ItemType Directory -Path "$qaRoot/Assets/Shaders" -Force | Out-Null
$offsetShader = Get-Content -LiteralPath "$unityRoot/Assets/Resources/Shaders/DepthOffsetLit.shader" -Raw -Encoding UTF8
Copy-Item "$unityRoot/Assets/Resources/Shaders/DepthOffsetLit.shader*" "$qaRoot/Assets/Shaders" -Force
Copy-Item "$unityRoot/Assets/Shaders/Foliage.hlsl*" "$qaRoot/Assets/Shaders" -Force
$terrainShader = $offsetShader.Replace('Shader "Custom/DepthOffsetLit"', 'Shader "Tests/DepthOffsetTerrainQA"').Replace('Offset -1, -1', 'Offset 1, 1')
[IO.File]::WriteAllText("$qaRoot/Assets/Shaders/DepthOffsetTerrainQA.shader", $terrainShader)
$manifest = Get-Content "$unityRoot/Packages/manifest.json" -Raw | ConvertFrom-Json
$manifest.dependencies.PSObject.Properties.Remove('com.rlabrecque.steamworks.net')
$manifest.dependencies.PSObject.Properties.Remove('com.siccity.gltfutility')
[IO.File]::WriteAllText("$qaRoot/Packages/manifest.json", ($manifest | ConvertTo-Json -Depth 10))
Set-Content "$qaRoot/result.txt" 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'RoadsideStoreQA.Run', '-sourceRoot', ('"' + $unityRoot + '"'), '-logFile', ('"' + "$qaRoot/qa.log" + '"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(1200000)) { Stop-Process -Id $process.Id; throw 'Roadside store QA timed out' }
$result = Get-Content "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Roadside store QA failed' }
# Even while the main editor is running, hand over the import settings and the mirror prefabs the standard generator made here,
# when the main project has neither yet (its own may have been fixed by hand).
$mirror = "$unityRoot/Assets/Resources/Models/Buildings/a2020"
if (!(Test-Path -LiteralPath $mirror) -and !(Get-ChildItem "$unityRoot/$buildings/a2020" -Recurse -Filter '*.meta')) {
    Copy-Item "$qaRoot/Assets/Resources/Models/Buildings/a2020" (Split-Path $mirror) -Recurse
    Copy-Item "$qaRoot/Assets/Resources/Models/Buildings/a2020.meta" (Split-Path $mirror)
    Copy-Item "$qaRoot/$buildings/a2020.meta" "$unityRoot/$buildings"
    Copy-Item "$qaRoot/$buildings/a2020/0.meta" "$unityRoot/$buildings/a2020"
    Copy-Item "$qaRoot/$buildings/a2020/0/*.meta" "$unityRoot/$buildings/a2020/0"
}
