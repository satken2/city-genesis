param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $unityRoot 'Tests/BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$projectXml = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj"
    $core = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path ($core.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/CameraQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/ProjectSettings" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/TagManager.asset" -Destination "$qaRoot/ProjectSettings/TagManager.asset" -Force
Copy-Item -LiteralPath "$PSScriptRoot/CameraQA.cs" -Destination "$qaRoot/Assets/Editor/CameraQA.cs" -Force
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @(
    '-batchmode', '-projectPath', ('"' + $qaRoot + '"'),
    '-executeMethod', 'CameraQA.Run', '-sourceRoot', ('"' + $unityRoot + '"'),
    '-logFile', ('"' + "$qaRoot/qa.log" + '"')
) -WindowStyle Hidden -PassThru
$deadline = (Get-Date).AddMinutes(4)
while (!$process.WaitForExit(1000)) {
    if ((Get-Date) -gt $deadline) { Stop-Process -Id $process.Id; throw 'Camera QA timed out' }
}
$result = Get-Content -LiteralPath "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Camera QA failed' }
