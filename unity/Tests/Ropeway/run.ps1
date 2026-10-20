$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
[xml]$projectXml = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
$coreReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
$editorRoot = $coreReference.HintPath -replace '[\\/]Data[\\/].*$', ''
$qaRoot = Join-Path $unityRoot 'Temp/RopewayUnityQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/Assets/Models/Paths", "$qaRoot/Assets/Models/Props", "$qaRoot/ProjectSettings", "$qaRoot/Packages" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item -LiteralPath "$PSScriptRoot/RopewayQA.cs", "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs", "$unityRoot/Assets/Editor/ModelImportSettings.cs" -Destination "$qaRoot/Assets/Editor" -Force
foreach ($id in @(601,602)) {
    New-Item -ItemType Directory -Path "$qaRoot/Assets/Models/Paths/p$id" -Force | Out-Null
    Get-ChildItem -LiteralPath "$unityRoot/Assets/Models/Paths/p$id" -Filter '*.fbx' | Copy-Item -Destination "$qaRoot/Assets/Models/Paths/p$id" -Force
}
Set-Content -LiteralPath "$qaRoot/Packages/manifest.json" -Value '{"dependencies":{}}'
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath (Join-Path $editorRoot 'Unity.exe') -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'RopewayQA.Run', '-ropewayReport', ('"' + "$qaRoot/result.txt" + '"'), '-logFile', ('"' + "$qaRoot/qa.log" + '"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Ropeway QA timed out; inspect Temp/RopewayUnityQA/qa.log' }
Get-Content -LiteralPath "$qaRoot/result.txt"
if ($process.ExitCode -ne 0 -or !(Select-String -LiteralPath "$qaRoot/result.txt" -Pattern '^PASS' -Quiet)) { exit 1 }

