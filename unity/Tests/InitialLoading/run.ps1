param([string]$UnityEditor = 'C:/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor')
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$compileDir = Join-Path $unityRoot 'Temp/InitialLoadingQA'
$qaRoot = Join-Path $unityRoot 'Temp/InitialLoadingUnityQA'
New-Item -ItemType Directory -Path $compileDir, "$qaRoot/Assets/Editor", "$qaRoot/Packages", "$qaRoot/ProjectSettings" -Force | Out-Null
# Unityが作成した参照を使い、本編のDLLを上書きせず検証用にコンパイルする。
$response = Get-ChildItem "$unityRoot/Library/Bee/artifacts/*/Assembly-CSharp.rsp" | Where-Object { $_.Directory.Name -like '*E.dag' } | Select-Object -First 1
if (!$response) { throw '先に本編をUnity Editorでインポートしてください。' }
$text = Get-Content -LiteralPath $response.FullName -Raw
$text = $text -replace '-out:"[^"]+"', '-out:"Temp/InitialLoadingQA/Assembly-CSharp.dll"' -replace '-refout:"[^"]+"', '-refout:"Temp/InitialLoadingQA/Assembly-CSharp.ref.dll"'
Set-Content -LiteralPath "$compileDir/compile.rsp" -Value $text
Push-Location $unityRoot
try {
    & "$UnityEditor/Data/NetCoreRuntime/dotnet.exe" "$UnityEditor/Data/DotNetSdkRoslyn/csc.dll" '@Temp/InitialLoadingQA/compile.rsp'
    if ($LASTEXITCODE -ne 0) { throw 'Runtime compilation failed' }
} finally { Pop-Location }
Copy-Item -LiteralPath "$PSScriptRoot/InitialLoadingQA.cs" -Destination "$qaRoot/Assets/Editor/InitialLoadingQA.cs" -Force
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Set-Content -LiteralPath "$qaRoot/Packages/manifest.json" -Value '{"dependencies":{"com.unity.render-pipelines.universal":"17.4.0","com.unity.nuget.newtonsoft-json":"3.2.2","com.unity.inputsystem":"1.19.0","com.unity.ugui":"2.0.0"}}'
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath "$UnityEditor/Unity.exe" -ArgumentList @('-batchmode', '-nographics', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'InitialLoadingQA.Run', '-sourceRoot', ('"' + $unityRoot + '"'), '-logFile', ('"' + "$qaRoot/qa.log" + '"')) -WorkingDirectory $qaRoot -WindowStyle Hidden -PassThru
Write-Output "Initial loading QA process: $($process.Id); results: $qaRoot/result.txt"
