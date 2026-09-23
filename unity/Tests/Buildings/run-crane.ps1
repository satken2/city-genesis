param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$projectXml = Get-Content "$unityRoot/Assembly-CSharp.csproj"
    $core = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path ($core.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/ConstructionCraneQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/Assets/Crane", "$qaRoot/ProjectSettings" -Force | Out-Null
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item "$PSScriptRoot/ConstructionCraneQA.cs" "$qaRoot/Assets/Editor/ConstructionCraneQA.cs" -Force
Get-ChildItem "$unityRoot/Assets/Models/Buildings/common/crane-*.fbx*" -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination "$qaRoot/Assets/Crane/$($_.Name)" -Force
}
Set-Content "$qaRoot/result.txt" 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @(
    '-batchmode', '-projectPath', ('"' + $qaRoot + '"'),
    '-executeMethod', 'ConstructionCraneQA.Run', '-sourceRoot', ('"' + $unityRoot + '"'),
    '-logFile', ('"' + "$qaRoot/qa.log" + '"')
) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Construction crane QA timed out' }
$result = Get-Content "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Construction crane QA failed' }
