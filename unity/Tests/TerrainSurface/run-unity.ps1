param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $unityRoot 'Assembly-CSharp.csproj'
& dotnet build $project -v quiet /clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (!$UnityExe) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $coreReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $editorRoot = $coreReference.HintPath -replace '[\\/]Data[\\/].*$', ''
    $UnityExe = Join-Path $editorRoot 'Unity.exe'
}
if (!(Test-Path -LiteralPath $UnityExe)) { throw "Unity.exe not found: $UnityExe" }
$qaRoot = Join-Path $unityRoot 'Temp/TerrainSurfaceQA'
$editorAssets = Join-Path $qaRoot 'Assets/Editor'
New-Item -ItemType Directory -Path $editorAssets -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TerrainSurfaceQA.cs') -Destination $editorAssets -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SurfaceDepthQA.shader') -Destination (Join-Path $qaRoot 'Assets') -Force
$resultPath = Join-Path $qaRoot 'result.txt'
Set-Content -LiteralPath $resultPath -Value 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @(
    '-batchmode', '-projectPath', ('"' + $qaRoot + '"'),
    '-executeMethod', 'TerrainSurfaceQA.Run', '-surfaceSourceRoot', ('"' + $unityRoot + '"'),
    '-logFile', ('"' + (Join-Path $qaRoot 'qa.log') + '"')
) -WindowStyle Hidden -PassThru
$process.WaitForExit()
$result = Get-Content -LiteralPath $resultPath -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { exit 1 }
