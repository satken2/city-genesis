param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $unityRoot 'Assembly-CSharp.csproj'
& dotnet build $project -v quiet /clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (!$UnityExe) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $core = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path ($core.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/VehicleUnityQA'
$editorAssets = Join-Path $qaRoot 'Assets/Editor'
New-Item -ItemType Directory -Path $editorAssets -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'VehicleQA.cs') -Destination $editorAssets -Force
$resultPath = Join-Path $qaRoot 'result.txt'
Set-Content -LiteralPath $resultPath -Value 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @(
    '-batchmode', '-projectPath', ('"' + $qaRoot + '"'),
    '-executeMethod', 'VehicleQA.Run', '-vehicleSourceRoot', ('"' + $unityRoot + '"'),
    '-logFile', ('"' + (Join-Path $qaRoot 'qa.log') + '"')
) -WindowStyle Hidden -PassThru
$process.WaitForExit()
$result = Get-Content -LiteralPath $resultPath -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { exit 1 }
