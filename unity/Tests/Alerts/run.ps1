param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $unityRoot 'Tests/BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$project = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
    $core = $project.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path ($core.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/AlertsQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/Assets/Images", "$qaRoot/Packages", "$qaRoot/ProjectSettings", "$qaRoot/Runtime" -Force | Out-Null
Copy-Item -LiteralPath "$unityRoot/Temp/bin/Debug/Assembly-CSharp.dll" -Destination "$qaRoot/Runtime" -Force
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings" -Force
Copy-Item -LiteralPath "$PSScriptRoot/AlertsQA.cs" -Destination "$qaRoot/Assets/Editor" -Force
foreach ($name in @('sand-clock', 'sanitation', 'bribery', 'electricity', 'rubble')) {
    Copy-Item -LiteralPath "$unityRoot/Assets/Images/$name.png", "$unityRoot/Assets/Images/$name.png.meta" -Destination "$qaRoot/Assets/Images" -Force
}
Set-Content -LiteralPath "$qaRoot/Packages/manifest.json" -Value '{"dependencies":{"com.unity.render-pipelines.universal":"17.4.0","com.unity.nuget.newtonsoft-json":"3.2.2","com.unity.ugui":"2.0.0","com.unity.inputsystem":"1.19.0"}}'
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @('-batchmode', '-nographics', '-projectPath', ('"'+$qaRoot+'"'), '-executeMethod', 'AlertsQA.Run', '-sourceRoot', ('"'+$unityRoot+'"'), '-logFile', ('"'+"$qaRoot/qa.log"+'"')) -WindowStyle Hidden -PassThru
$deadline = (Get-Date).AddMinutes(6)
while (!$process.WaitForExit(1000)) {
    if ((Get-Date) -gt $deadline) { Stop-Process -Id $process.Id; throw 'Alerts QA timed out' }
}
$result = Get-Content -LiteralPath "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Alerts QA failed' }
