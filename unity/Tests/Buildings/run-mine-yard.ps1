param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$projectXml = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
    $core = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path ($core.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
if (!(Test-Path -LiteralPath $UnityExe)) { throw "Unity.exe not found: $UnityExe" }
$qaRoot = Join-Path $unityRoot 'Temp/MineYardQA'
$buildings = 'Assets/Models/Buildings'
foreach ($folder in @('Assets/Editor', "$buildings/Materials", "$buildings/Textures", 'Assets/Settings', 'Packages', 'ProjectSettings')) {
    New-Item -ItemType Directory -Path (Join-Path $qaRoot $folder) -Force | Out-Null
}
Copy-Item "$PSScriptRoot/MineYardQA.cs" "$qaRoot/Assets/Editor" -Force
Copy-Item "$unityRoot/$buildings/a1022" "$qaRoot/$buildings" -Recurse -Force
Copy-Item "$unityRoot/$buildings/Materials/BD_GROUND_ORE*.mat*" "$qaRoot/$buildings/Materials" -Force
Copy-Item "$unityRoot/$buildings/Textures/BD_GROUND_ORE.png*" "$qaRoot/$buildings/Textures" -Force
foreach ($name in @('PC_RPAsset.asset', 'PC_Renderer.asset', 'DefaultVolumeProfile.asset', 'UniversalRenderPipelineGlobalSettings.asset')) {
    Copy-Item "$unityRoot/Assets/Settings/$name*" "$qaRoot/Assets/Settings" -Force
}
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings" -Force
# The game's depth offsets: buildings use Custom/DepthOffsetLit (-1,-1); the same shader with (1,1) stands in for the terrain shader.
New-Item -ItemType Directory -Path "$qaRoot/Assets/Shaders" -Force | Out-Null
$offsetShader = Get-Content -LiteralPath "$unityRoot/Assets/Resources/Shaders/DepthOffsetLit.shader" -Raw -Encoding UTF8
Set-Content -LiteralPath "$qaRoot/Assets/Shaders/DepthOffsetLit.shader" -Value $offsetShader -Encoding UTF8
$terrainShader = $offsetShader.Replace('Shader "Custom/DepthOffsetLit"', 'Shader "Tests/DepthOffsetTerrainQA"').Replace('Offset -1, -1', 'Offset 1, 1')
Set-Content -LiteralPath "$qaRoot/Assets/Shaders/DepthOffsetTerrainQA.shader" -Value $terrainShader -Encoding UTF8
$manifest = Get-Content "$unityRoot/Packages/manifest.json" -Raw | ConvertFrom-Json
$manifest.dependencies.PSObject.Properties.Remove('com.rlabrecque.steamworks.net')
$manifest.dependencies.PSObject.Properties.Remove('com.siccity.gltfutility')
$manifest | ConvertTo-Json -Depth 10 | Set-Content "$qaRoot/Packages/manifest.json"
Set-Content "$qaRoot/result.txt" 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'MineYardQA.Run', '-sourceRoot', ('"' + $unityRoot + '"'), '-logFile', ('"' + "$qaRoot/qa.log" + '"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(1200000)) { Stop-Process -Id $process.Id; throw 'Mine yard QA timed out' }
$result = Get-Content "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Mine yard QA failed' }
