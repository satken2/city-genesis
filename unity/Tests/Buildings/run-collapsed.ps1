param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$projectXml = Get-Content "$unityRoot/Assembly-CSharp.csproj"
    $core = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' } | Select-Object -First 1
    $UnityExe = Join-Path ($core.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/CollapsedDebrisQA'
foreach ($folder in @('Assets/Editor', 'Assets/Resources/Textures', 'Assets/Settings', 'Packages', 'ProjectSettings')) {
    New-Item -ItemType Directory -Path (Join-Path $qaRoot $folder) -Force | Out-Null
}
Copy-Item "$PSScriptRoot/CollapsedDebrisQA.cs" "$qaRoot/Assets/Editor" -Force
Copy-Item "$unityRoot/Assets/Resources/Collapsed*.mat*" "$qaRoot/Assets/Resources" -Force
Copy-Item "$unityRoot/Assets/Resources/Textures/collapsed-*" "$qaRoot/Assets/Resources/Textures" -Force
foreach ($name in @('PC_RPAsset.asset', 'PC_Renderer.asset', 'DefaultVolumeProfile.asset', 'UniversalRenderPipelineGlobalSettings.asset')) {
    Copy-Item "$unityRoot/Assets/Settings/$name*" "$qaRoot/Assets/Settings" -Force
}
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings" -Force
$manifest = Get-Content "$unityRoot/Packages/manifest.json" -Raw | ConvertFrom-Json
$manifest.dependencies.PSObject.Properties.Remove('com.rlabrecque.steamworks.net')
$manifest.dependencies.PSObject.Properties.Remove('com.siccity.gltfutility')
$manifest | ConvertTo-Json -Depth 10 | Set-Content "$qaRoot/Packages/manifest.json"
Set-Content "$qaRoot/result.txt" 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'CollapsedDebrisQA.Run', '-sourceRoot', ('"' + $unityRoot + '"'), '-logFile', ('"' + "$qaRoot/qa.log" + '"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(1200000)) { Stop-Process -Id $process.Id; throw 'Collapsed debris QA timed out' }
$result = Get-Content "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Collapsed debris QA failed' }
