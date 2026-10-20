param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $unityRoot 'Assembly-CSharp.csproj') -Raw
    $reference = $project.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path ($reference.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/LodRuntimeQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/Assets/Runtime", "$qaRoot/ProjectSettings", "$qaRoot/Packages" -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LodQaHost.cs') -Destination "$qaRoot/Assets/Runtime/LodQaHost.cs" -Force
Set-Content -LiteralPath "$qaRoot/Assets/Runtime/LodQA.Runtime.asmdef" -Value '{"name":"LodQA.Runtime"}'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LodQA.cs') -Destination "$qaRoot/Assets/Editor/LodQA.cs" -Force
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/TagManager.asset" -Destination "$qaRoot/ProjectSettings/TagManager.asset" -Force
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Set-Content -LiteralPath "$qaRoot/Packages/manifest.json" -Value '{"dependencies":{"com.unity.render-pipelines.universal":"17.4.0","com.unity.nuget.newtonsoft-json":"3.2.2"}}'
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'LodQA.Run', '-lodSourceRoot', ('"' + $unityRoot + '"'), '-logFile', ('"' + "$qaRoot/qa.log" + '"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'LOD QA timed out; inspect Temp/LodRuntimeQA/qa.log' }
Get-Content -LiteralPath "$qaRoot/result.txt"
if ($process.ExitCode -ne 0 -or !(Select-String -LiteralPath "$qaRoot/result.txt" -Pattern '^PASS' -Quiet)) { exit 1 }
