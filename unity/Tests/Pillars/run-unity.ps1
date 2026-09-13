param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (!$UnityExe) {
    [xml]$projectXml = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
    $coreReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $editorRoot = $coreReference.HintPath -replace '[\\/]Data[\\/].*$', ''
    $UnityExe = Join-Path $editorRoot 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/PillarUnityQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/ProjectSettings" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item -LiteralPath "$PSScriptRoot/Unity/PillarQA.cs" -Destination "$qaRoot/Assets/Editor" -Force
$resultPath = Join-Path $qaRoot 'result.txt'
Set-Content -LiteralPath $resultPath -Value 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @(
    '-batchmode', '-nographics', '-projectPath', ('"' + $qaRoot + '"'),
    '-executeMethod', 'PillarQA.Run', '-pillarSourceRoot', ('"' + $unityRoot + '"'),
    '-logFile', ('"' + (Join-Path $qaRoot 'qa.log') + '"')
) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Pillar QA timed out; inspect Temp/PillarUnityQA/qa.log' }
$result = Get-Content -LiteralPath $resultPath -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { exit 1 }
