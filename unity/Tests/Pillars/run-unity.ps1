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
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/ProjectSettings", "$qaRoot/Packages" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item -LiteralPath "$PSScriptRoot/Unity/PillarQA.cs", "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs", "$unityRoot/Assets/Editor/ModelImportSettings.cs" -Destination "$qaRoot/Assets/Editor" -Force
foreach ($entry in @('p4/pillar_leg', 'p4/pillar_crossbeam', 'p421/pillar', 'p421/pillar_cap')) {
    $destination = Join-Path "$qaRoot/Assets/Models/Paths" (Split-Path $entry)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath "$unityRoot/Assets/Models/Paths/$entry.fbx" -Destination $destination -Force
    if (Test-Path -LiteralPath "$unityRoot/Assets/Models/Paths/$entry.fbx.meta") {
        Copy-Item -LiteralPath "$unityRoot/Assets/Models/Paths/$entry.fbx.meta" -Destination $destination -Force
    }
}
Set-Content -LiteralPath "$qaRoot/Packages/manifest.json" -Value '{"dependencies":{}}'
$resultPath = Join-Path $qaRoot 'result.txt'
Set-Content -LiteralPath $resultPath -Value 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @(
    '-batchmode', '-projectPath', ('"' + $qaRoot + '"'),
    '-executeMethod', 'PillarQA.Run', '-pillarSourceRoot', ('"' + $unityRoot + '"'),
    '-logFile', ('"' + (Join-Path $qaRoot 'qa.log') + '"')
) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Pillar QA timed out; inspect Temp/PillarUnityQA/qa.log' }
$result = Get-Content -LiteralPath $resultPath -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { exit 1 }
# 正規の生成器が作った門型2部品の meta とミラーを本編へ写す。
foreach ($part in @('pillar_leg', 'pillar_crossbeam')) {
    foreach ($entry in @("Assets/Models/Paths/p4/$part.fbx.meta", "Assets/Resources/Models/Paths/p4/$part.prefab", "Assets/Resources/Models/Paths/p4/$part.prefab.meta")) {
        Copy-Item -LiteralPath "$qaRoot/$entry" -Destination "$unityRoot/$entry" -Force
    }
}
