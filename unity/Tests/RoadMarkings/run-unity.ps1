param([string]$UnityExe = 'C:/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor/Unity.exe', [switch]$Wait)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$qaRoot = Join-Path $unityRoot 'Temp/RoadMarkingsQA'
foreach ($folder in @('Assets/Editor','Assets/Resources/Shaders','Assets/Shaders','Assets/Settings','Assets/Paths','Packages','ProjectSettings')) {
    New-Item -ItemType Directory -Path (Join-Path $qaRoot $folder) -Force | Out-Null
}
Copy-Item -LiteralPath "$PSScriptRoot/RoadMarkingsQA.cs" -Destination "$qaRoot/Assets/Editor" -Force
foreach ($name in @('RoadSurface.shader','RoadMarkingBake.shader','DepthOffsetLit.shader')) {
    Copy-Item -LiteralPath "$unityRoot/Assets/Resources/Shaders/$name" -Destination "$qaRoot/Assets/Resources/Shaders" -Force
}
Copy-Item -LiteralPath "$unityRoot/Assets/Shaders/RoadSurface.hlsl" -Destination "$qaRoot/Assets/Shaders" -Force
foreach ($name in @('PC_RPAsset.asset','PC_Renderer.asset','DefaultVolumeProfile.asset','UniversalRenderPipelineGlobalSettings.asset')) {
    Copy-Item "$unityRoot/Assets/Settings/$name*" "$qaRoot/Assets/Settings" -Force
}
Copy-Item -LiteralPath "$unityRoot/Assets/Models/Paths/p1" -Destination "$qaRoot/Assets/Paths" -Recurse -Force
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings" -Force
Set-Content -LiteralPath "$qaRoot/Packages/manifest.json" -Value '{"dependencies":{"com.unity.render-pipelines.universal":"17.4.0","com.unity.nuget.newtonsoft-json":"3.2.2"}}'
[xml]$project = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
$references = foreach ($reference in $project.SelectNodes('//Reference/HintPath')) {
    if ([IO.Path]::IsPathRooted($reference.InnerText)) { $reference.InnerText }
    else { [IO.Path]::GetFullPath((Join-Path $unityRoot $reference.InnerText)) }
}
Set-Content -LiteralPath "$qaRoot/references.txt" -Value $references
Set-Content -LiteralPath "$qaRoot/source.txt" -Value $unityRoot
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @('-batchmode','-projectPath',('"'+$qaRoot+'"'),'-executeMethod','RoadMarkingsQA.Run','-logFile',('"'+$qaRoot+'/qa.log"')) -WindowStyle Hidden -PassThru
Write-Output "Road markings QA process: $($process.Id); results: $qaRoot/result.txt"
if ($Wait) {
    $process.WaitForExit()
    Get-Content -LiteralPath "$qaRoot/result.txt"
    exit $process.ExitCode
}
