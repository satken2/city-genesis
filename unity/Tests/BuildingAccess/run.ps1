$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& "$unityRoot/Tests/BuildRuntime.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Runtime build failed' }
[xml]$project = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
$core = $project.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
$editorRoot = $core.HintPath -replace '[\\/]Data[\\/].*$', ''
$qaRoot = Join-Path $unityRoot 'Temp/BuildingAccessQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/ProjectSettings", "$qaRoot/Packages", "$qaRoot/Assets/Resources/Config" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item -LiteralPath "$PSScriptRoot/RoadAccessQA.cs", "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs" -Destination "$qaRoot/Assets/Editor" -Force
foreach ($part in 'Buildings/a1081/0','Buildings/a3002/0','Paths/p221','Paths/p1') {
    New-Item -ItemType Directory -Path "$qaRoot/Assets/Models/$part" -Force | Out-Null
    Copy-Item "$unityRoot/Assets/Models/$part/*" "$qaRoot/Assets/Models/$part" -Force
}
Copy-Item -LiteralPath "$unityRoot/Assets/Resources/Config/model_weights.json" -Destination "$qaRoot/Assets/Resources/Config" -Force
Set-Content -LiteralPath "$qaRoot/Assets/Resources/Config/agent_model_config.yaml" -Value "agents:`n  1081:`n    model: true`n    variants: [0]`n  3002:`n    model: true`n    variants: [0]"
Set-Content -LiteralPath "$qaRoot/Packages/manifest.json" -Value '{"dependencies":{}}'
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath (Join-Path $editorRoot 'Unity.exe') -ArgumentList @('-batchmode','-projectPath',('"'+$qaRoot+'"'),'-executeMethod','RoadAccessQA.Run','-sourceRoot',('"'+$unityRoot+'"'),'-logFile',('"'+"$qaRoot/qa.log"+'"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Building access QA timed out' }
$result = Get-Content -LiteralPath "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Building access QA failed' }
