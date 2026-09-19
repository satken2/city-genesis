$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
[xml]$project = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
$reference = $project.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
$unityExe = Join-Path ($reference.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
$qaRoot = Join-Path $unityRoot 'Temp/BusStopsQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/Assets/Models/Props", "$qaRoot/Packages", "$qaRoot/ProjectSettings" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings" -Force
Copy-Item -LiteralPath "$PSScriptRoot/BusStopsQA.cs", "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs", "$unityRoot/Assets/Editor/ModelImportSettings.cs" -Destination "$qaRoot/Assets/Editor" -Force
Copy-Item -LiteralPath "$unityRoot/Assets/Models/Props/bus_stop.fbx" -Destination "$qaRoot/Assets/Models/Props" -Force
Set-Content -LiteralPath "$qaRoot/Packages/manifest.json" -Value '{"dependencies":{"com.unity.render-pipelines.universal":"17.4.0","com.unity.nuget.newtonsoft-json":"3.2.2"}}'
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath $unityExe -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'BusStopsQA.Run', '-busStopsSourceRoot', ('"' + $unityRoot + '"'), '-logFile', ('"' + "$qaRoot/qa.log" + '"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Bus stops QA timed out' }
Get-Content -LiteralPath "$qaRoot/result.txt"
if ($process.ExitCode -ne 0 -or !(Select-String -LiteralPath "$qaRoot/result.txt" -Pattern '^PASS' -Quiet)) { exit 1 }
