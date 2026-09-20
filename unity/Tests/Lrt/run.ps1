$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& "$unityRoot/Tests/BuildRuntime.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
[xml]$projectXml = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
$reference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
$editorRoot = $reference.HintPath -replace '[\\/]Data[\\/].*$', ''
$qaRoot = Join-Path $unityRoot 'Temp/LrtUnityQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/Assets/Paths", "$qaRoot/ProjectSettings", "$qaRoot/Packages" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item -LiteralPath "$PSScriptRoot/LrtQA.cs", "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs" -Destination "$qaRoot/Assets/Editor" -Force
foreach ($id in @(222,242,303,431)) {
    Copy-Item -LiteralPath "$unityRoot/Assets/Models/Paths/p$id" -Destination "$qaRoot/Assets/Paths" -Recurse -Force
}
Set-Content -LiteralPath "$qaRoot/Packages/manifest.json" -Value '{"dependencies":{}}'
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath (Join-Path $editorRoot 'Unity.exe') -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'LrtQA.Run', '-sourceRoot', ('"' + $unityRoot + '"'), '-logFile', ('"' + "$qaRoot/qa.log" + '"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'LRT QA timed out; inspect Temp/LrtUnityQA/qa.log' }
Get-Content -LiteralPath "$qaRoot/result.txt"
if ($process.ExitCode -ne 0 -or !(Select-String -LiteralPath "$qaRoot/result.txt" -Pattern '^PASS' -Quiet)) { exit 1 }
