param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $unityRoot 'Assembly-CSharp.csproj'
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $coreReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $editorRoot = $coreReference.HintPath -replace '[\\/]Data[\\/].*$', ''
    $UnityExe = Join-Path $editorRoot 'Unity.exe'
}
if (!(Test-Path -LiteralPath $UnityExe)) { throw "Unity.exe not found: $UnityExe" }
$qaRoot = Join-Path $unityRoot 'Temp/QuayQA'
New-Item -ItemType Directory -Path (Join-Path $qaRoot 'Assets/Editor'), (Join-Path $qaRoot 'ProjectSettings'), (Join-Path $qaRoot 'Packages') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'QuayQA.cs') -Destination (Join-Path $qaRoot 'Assets/Editor/QuayQA.cs') -Force
Copy-Item -LiteralPath (Join-Path $unityRoot 'ProjectSettings/ProjectVersion.txt') -Destination (Join-Path $qaRoot 'ProjectSettings/ProjectVersion.txt') -Force
# 描画は使わないので、地形と物理の組み込みモジュールだけにしてパッケージの解決を待たない。
Set-Content -LiteralPath (Join-Path $qaRoot 'Packages/manifest.json') -Value '{"dependencies":{"com.unity.modules.physics":"1.0.0","com.unity.modules.terrain":"1.0.0","com.unity.modules.terrainphysics":"1.0.0"}}'
$resultPath = Join-Path $qaRoot 'result.txt'
Set-Content -LiteralPath $resultPath -Value 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @(
    '-batchmode', '-nographics', '-projectPath', ('"' + $qaRoot + '"'),
    '-executeMethod', 'QuayQA.Run', '-quaySourceRoot', ('"' + $unityRoot + '"'),
    '-logFile', ('"' + (Join-Path $qaRoot 'qa.log') + '"')
) -WindowStyle Hidden -PassThru
# 終了コードを確実に読めるよう、終了前にハンドルを取っておく。
$null = $process.Handle
if (!$process.WaitForExit(300000)) { Stop-Process -Id $process.Id; throw 'Quay QA timed out; inspect Temp/QuayQA/qa.log' }
$result = Get-Content -LiteralPath $resultPath -Raw
Write-Output $result
Write-Output "exit=$($process.ExitCode)"
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { exit 1 }
