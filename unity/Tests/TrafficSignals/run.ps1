param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (!$UnityExe) {
    [xml]$projectXml = Get-Content -LiteralPath (Join-Path $unityRoot 'Assembly-CSharp.csproj') -Raw
    $coreReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path (Split-Path (Split-Path (Split-Path (Split-Path $coreReference.HintPath)))) 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/TrafficSignalsQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/Assets/Models/Props", "$qaRoot/Assets/Paths", "$qaRoot/ProjectSettings" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings" -Force
Copy-Item -LiteralPath "$PSScriptRoot/TrafficSignalsQA.cs", "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs", "$unityRoot/Assets/Editor/ModelImportSettings.cs" -Destination "$qaRoot/Assets/Editor" -Force
Get-ChildItem -LiteralPath "$unityRoot/Assets/Models/Props" -Filter 'traffic_signal_*.fbx' | Copy-Item -Destination "$qaRoot/Assets/Models/Props" -Force
# Copy only the components used by roads in the actual master (variant-specific and shared by type).
foreach ($folder in @('p1', 'p2', 'p211', 'p212', 'p221', 'p231', 'p241')) {
    Copy-Item -LiteralPath "$unityRoot/Assets/Models/Paths/$folder" -Destination "$qaRoot/Assets/Paths" -Recurse -Force
}
Remove-Item -LiteralPath "$qaRoot/*.png" -Force -ErrorAction SilentlyContinue
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'TrafficSignalsQA.Run', '-signalsSourceRoot', ('"' + $unityRoot + '"'), '-logFile', ('"' + "$qaRoot/qa.log" + '"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(600000)) { Stop-Process -Id $process.Id; throw 'Traffic signals QA timed out; inspect Temp/TrafficSignalsQA/qa.log' }
Get-Content -LiteralPath "$qaRoot/result.txt"
if ($process.ExitCode -ne 0 -or !(Select-String -LiteralPath "$qaRoot/result.txt" -Pattern '^PASS' -Quiet)) { exit 1 }
