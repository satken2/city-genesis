param([string]$QaRoot)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
[xml]$projectXml = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
$coreReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
$editorRoot = $coreReference.HintPath -replace '[\\/]Data[\\/].*$', ''
if (!$QaRoot) { $QaRoot = Join-Path $unityRoot 'Temp/AmbientShipsQA' }
New-Item -ItemType Directory -Path "$QaRoot/Assets/Editor", "$QaRoot/Assets/Models/Props", "$QaRoot/ProjectSettings", "$QaRoot/Packages" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$QaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item -LiteralPath "$PSScriptRoot/AmbientShipsQA.cs", "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs", "$unityRoot/Assets/Editor/ModelImportSettings.cs" -Destination "$QaRoot/Assets/Editor" -Force
foreach ($name in @('ambient_container_ship', 'ambient_tanker', 'ambient_bulk_carrier', 'ambient_cruise_ship', 'ambient_fishing_boat', 'ambient_airliner', '304', '305', '306', '307')) {
    foreach ($suffix in @('', '_close', '_far')) {
        $fbx = "$unityRoot/Assets/Models/Props/$name$suffix.fbx"
        if (Test-Path -LiteralPath $fbx) { Copy-Item -LiteralPath $fbx -Destination "$QaRoot/Assets/Models/Props" -Force }
    }
}
Set-Content -LiteralPath "$QaRoot/Packages/manifest.json" -Value '{"dependencies":{}}'
Set-Content -LiteralPath "$QaRoot/result.txt" -Value 'RUNNING'
$arguments = @('-batchmode', '-projectPath', ('"' + $QaRoot + '"'), '-executeMethod', 'AmbientShipsQA.Run', '-shipsSourceRoot', ('"' + $unityRoot + '"'), '-logFile', ('"' + "$QaRoot/qa.log" + '"'))
$process = Start-Process -FilePath (Join-Path $editorRoot 'Unity.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(300000)) { Stop-Process -Id $process.Id; throw "Ambient ships QA timed out; inspect $QaRoot/qa.log" }
Get-Content -LiteralPath "$QaRoot/result.txt"
if ($process.ExitCode -ne 0 -or !(Select-String -LiteralPath "$QaRoot/result.txt" -Pattern '^PASS' -Quiet)) { exit 1 }
