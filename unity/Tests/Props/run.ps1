$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $unityRoot 'Assembly-CSharp.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$coreReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
$editorRoot = $coreReference.HintPath -replace '[\\/]Data[\\/].*$', ''
$unityExe = Join-Path $editorRoot 'Unity.exe'
$qaRoot = Join-Path $unityRoot 'Temp/PropsUnityQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/Assets/Models/Props", "$qaRoot/Packages" -Force | Out-Null
New-Item -ItemType Directory -Path "$qaRoot/ProjectSettings" -Force | Out-Null
New-Item -ItemType Directory -Path "$qaRoot/Assets/Settings" -Force | Out-Null
foreach ($name in @('PC_RPAsset.asset', 'PC_Renderer.asset', 'DefaultVolumeProfile.asset', 'UniversalRenderPipelineGlobalSettings.asset')) {
    Copy-Item -LiteralPath "$unityRoot/Assets/Settings/$name", "$unityRoot/Assets/Settings/$name.meta" -Destination "$qaRoot/Assets/Settings" -Force
}
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
& (Join-Path $PSScriptRoot "../BuildRuntime.ps1")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PropsQA.cs') -Destination "$qaRoot/Assets/Editor/PropsQA.cs" -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ForestQA.cs') -Destination "$qaRoot/Assets/Editor/ForestQA.cs" -Force
New-Item -ItemType Directory -Path "$qaRoot/Assets/Resources/Shaders" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/Assets/Resources/Shaders/Forest.shader", "$unityRoot/Assets/Resources/Shaders/ForestPlacement.compute" -Destination "$qaRoot/Assets/Resources/Shaders" -Force
$missingCluster = (Get-Content -LiteralPath "$unityRoot/Assets/Resources/Shaders/Forest.shader" -Raw).Replace('CityGenesis/Forest', 'CityGenesis/ForestWithoutCluster').Replace('#pragma multi_compile _ _CLUSTER_LIGHT_LOOP', '')
Set-Content -LiteralPath "$qaRoot/Assets/Resources/Shaders/ForestWithoutCluster.shader" -Value $missingCluster
Copy-Item -LiteralPath "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs", "$unityRoot/Assets/Editor/ModelImportSettings.cs" -Destination "$qaRoot/Assets/Editor" -Force
Get-ChildItem -LiteralPath "$unityRoot/Assets/Models/Props" | Copy-Item -Destination "$qaRoot/Assets/Models/Props" -Recurse -Force
Set-Content -LiteralPath "$qaRoot/Packages/manifest.json" -Value '{"dependencies":{"com.unity.render-pipelines.universal":"17.4.0","com.unity.nuget.newtonsoft-json":"3.2.2"}}'
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath $unityExe -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'PropsQA.Run', '-propsSourceRoot', ('"' + $unityRoot + '"'), '-logFile', ('"' + "$qaRoot/qa.log" + '"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Props QA timed out; inspect Temp/PropsUnityQA/qa.log' }
Get-Content -LiteralPath "$qaRoot/result.txt"
if ($process.ExitCode -ne 0 -or !(Select-String -LiteralPath "$qaRoot/result.txt" -Pattern '^PASS' -Quiet)) { exit 1 }
