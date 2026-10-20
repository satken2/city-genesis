$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $unityRoot 'Tests/BuildRuntime.ps1')
[xml]$projectXml = Get-Content -LiteralPath (Join-Path $unityRoot 'Assembly-CSharp.csproj') -Raw
$coreReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
$editorRoot = $coreReference.HintPath -replace '[\\/]Data[\\/].*$', ''
$unityExe = Join-Path $editorRoot 'Unity.exe'
$qaRoot = Join-Path $unityRoot 'Temp/RoadPreviewQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/ProjectSettings" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item -LiteralPath "$PSScriptRoot/PreviewQA.cs" -Destination "$qaRoot/Assets/Editor" -Force
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath $unityExe -ArgumentList @(
    '-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'PreviewQA.Run',
    '-previewSourceRoot', ('"' + $unityRoot + '"'), '-logFile', ('"' + $qaRoot + '/qa.log"')
) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Road preview QA timed out' }
$result = Get-Content -LiteralPath "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Road preview QA failed' }
