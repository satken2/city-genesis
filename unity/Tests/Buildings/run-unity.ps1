param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$projectXml = Get-Content "$unityRoot/Assembly-CSharp.csproj"
    $core = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path ($core.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/BuildingCornerQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/ProjectSettings" -Force | Out-Null
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item "$PSScriptRoot/BuildingCornerQA.cs" "$qaRoot/Assets/Editor/BuildingCornerQA.cs" -Force
Get-ChildItem "$unityRoot/Assets/Models/Buildings" -Recurse -Filter '*-c1.fbx' | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath("$unityRoot/Assets/Models/Buildings", $_.FullName)
    $destination = Join-Path "$qaRoot/Assets/Buildings" $relative
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    Copy-Item -LiteralPath ($_.FullName + '.meta') -Destination ($destination + '.meta') -Force
}
Set-Content "$qaRoot/result.txt" 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @(
    '-batchmode', '-projectPath', ('"' + $qaRoot + '"'),
    '-executeMethod', 'BuildingCornerQA.Run', '-sourceRoot', ('"' + $unityRoot + '"'),
    '-logFile', ('"' + "$qaRoot/qa.log" + '"')
) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Building corner QA timed out' }
$result = Get-Content "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Building corner QA failed' }
