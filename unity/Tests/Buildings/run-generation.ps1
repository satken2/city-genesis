param([string]$UnityExe, [string]$BaselineDirectory, [string]$OutputName = 'current')
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$project = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
    $reference = $project.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path ($reference.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/BuildingGenerationQA'
$outputDirectory = Join-Path $qaRoot $OutputName
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/ProjectSettings", $outputDirectory -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item -LiteralPath "$PSScriptRoot/BuildingGenerationQA.cs" -Destination "$qaRoot/Assets/Editor/BuildingGenerationQA.cs" -Force
Get-ChildItem -LiteralPath "$unityRoot/Assets/Models/Buildings/a2001" -Recurse -File | Where-Object { $_.Extension -eq '.fbx' -or $_.Name.EndsWith('.fbx.meta') } | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath("$unityRoot/Assets/Models/Buildings", $_.FullName)
    $destination = Join-Path "$qaRoot/Assets/Models/Buildings" $relative
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
}
Set-Content -LiteralPath "$outputDirectory/result.txt" -Value 'RUNNING'
$arguments = @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'BuildingGenerationQA.Run',
    '-sourceRoot', ('"' + $unityRoot + '"'), '-generationOutput', ('"' + $outputDirectory + '"'),
    '-logFile', ('"' + "$outputDirectory/qa.log" + '"'))
if ($BaselineDirectory) { $arguments += @('-baselineDir', ('"' + $BaselineDirectory + '"')) }
$process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Building generation QA timed out' }
$result = Get-Content -LiteralPath "$outputDirectory/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Building generation QA failed' }
Get-Content -LiteralPath "$outputDirectory/timings.csv"
