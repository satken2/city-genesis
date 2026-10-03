param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $unityRoot 'Tests/BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$project = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj"
    $reference = $project.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path ($reference.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/UndergroundQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/Assets/Resources/Shaders", "$qaRoot/Assets/Shaders", "$qaRoot/Packages", "$qaRoot/ProjectSettings" -Force | Out-Null
[xml]$references = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
$referencePaths = @($references.Project.ItemGroup.Reference | ForEach-Object {
    if ($_.HintPath) { [IO.Path]::GetFullPath([IO.Path]::Combine($unityRoot, $_.HintPath)) }
})
Set-Content -LiteralPath "$qaRoot/references.txt" -Value $referencePaths
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt", "$unityRoot/ProjectSettings/TagManager.asset" -Destination "$qaRoot/ProjectSettings" -Force
Copy-Item -LiteralPath "$PSScriptRoot/UndergroundQA.cs" -Destination "$qaRoot/Assets/Editor" -Force
foreach ($folder in @('Resources/Shaders','Shaders')) {
    Copy-Item -Path "$unityRoot/Assets/$folder/*" -Destination "$qaRoot/Assets/$folder" -Recurse -Force
}
Copy-Item -Path "$unityRoot/Assets/Resources/*.mat", "$unityRoot/Assets/Resources/*.mat.meta" -Destination "$qaRoot/Assets/Resources" -Force
foreach ($type in @('p1','p4','p221','p405','p451','p461','p462','p502')) {
    foreach ($folder in @("Models/Paths/$type")) {
        if (Test-Path -LiteralPath "$unityRoot/Assets/$folder") {
            New-Item -ItemType Directory -Path "$qaRoot/Assets/$folder" -Force | Out-Null
            Copy-Item -Path "$unityRoot/Assets/$folder/*" -Destination "$qaRoot/Assets/$folder" -Recurse -Force
        }
    }
}
Set-Content -LiteralPath "$qaRoot/Packages/manifest.json" -Value '{"dependencies":{"com.unity.render-pipelines.universal":"17.4.0","com.unity.nuget.newtonsoft-json":"3.2.2","com.unity.ugui":"2.0.0","com.unity.inputsystem":"1.19.0"}}'
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @('-batchmode','-projectPath',('"'+$qaRoot+'"'),'-executeMethod','UndergroundQA.Run','-sourceRoot',('"'+$unityRoot+'"'),'-logFile',('"'+"$qaRoot/qa.log"+'"')) -WindowStyle Hidden -PassThru
$deadline = (Get-Date).AddMinutes(6)
while (!$process.WaitForExit(1000)) {
    if ((Get-Date) -gt $deadline) { Stop-Process -Id $process.Id; throw 'Underground QA timed out' }
}
$result = Get-Content -LiteralPath "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Underground QA failed' }
