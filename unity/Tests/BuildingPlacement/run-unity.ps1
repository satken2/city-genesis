param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $unityRoot 'Tests/BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$projectXml = Get-Content "$unityRoot/Assembly-CSharp.csproj"
    $core = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path ($core.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/BuildingPlacementQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/ProjectSettings" -Force | Out-Null
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item "$PSScriptRoot/BuildingPlacementQA.cs" "$qaRoot/Assets/Editor/BuildingPlacementQA.cs" -Force
Set-Content "$qaRoot/result.txt" 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @(
    '-batchmode', '-projectPath', ('"' + $qaRoot + '"'),
    '-executeMethod', 'BuildingPlacementQA.Run', '-sourceRoot', ('"' + $unityRoot + '"'),
    '-logFile', ('"' + "$qaRoot/qa.log" + '"')
) -WindowStyle Hidden -PassThru
$deadline = (Get-Date).AddMinutes(4)
while (!$process.WaitForExit(1000)) {
    if ((Get-Date) -gt $deadline) { Stop-Process -Id $process.Id; throw 'Building placement QA timed out' }
}
$result = Get-Content "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Building placement QA failed' }
