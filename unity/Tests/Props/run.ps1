$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $unityRoot 'Assembly-CSharp.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$coreReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
$editorRoot = $coreReference.HintPath -replace '[\\/]Data[\\/].*$', ''
$unityExe = Join-Path $editorRoot 'Unity.exe'
$qaRoot = Join-Path $unityRoot 'Temp/PropsUnityQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/Assets/Models/Props", "$qaRoot/Packages" -Force | Out-Null
# Unityがcsprojを再生成する前でも、新規ソースを本編アセンブリに含めて検証する。
$targets = Join-Path $qaRoot 'PropsBuild.targets'
$includes = @('PropGeometry', 'PropRenderModel') | Where-Object { !(Select-String -LiteralPath $project -SimpleMatch "DataLoaders\$_.cs" -Quiet) } | ForEach-Object { '<Compile Include="$(MSBuildProjectDirectory)/Assets/Scripts/DataLoaders/' + $_ + '.cs" />' }
$targetXml = '<Project><ItemGroup Condition="' + "'" + '$(MSBuildProjectName)' + "' == 'Assembly-CSharp'" + '">' + ($includes -join '') + '</ItemGroup></Project>'
Set-Content -LiteralPath $targets -Value $targetXml
& dotnet build $project -v quiet /clp:ErrorsOnly "-p:CustomAfterMicrosoftCommonTargets=$targets"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PropsQA.cs') -Destination "$qaRoot/Assets/Editor/PropsQA.cs" -Force
Get-ChildItem -LiteralPath "$unityRoot/Assets/Models/Props" -File | Copy-Item -Destination "$qaRoot/Assets/Models/Props" -Force
Set-Content -LiteralPath "$qaRoot/Packages/manifest.json" -Value '{"dependencies":{"com.unity.render-pipelines.universal":"17.4.0","com.unity.nuget.newtonsoft-json":"3.2.2"}}'
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath $unityExe -ArgumentList @('-batchmode', '-projectPath', ('"' + $qaRoot + '"'), '-executeMethod', 'PropsQA.Run', '-propsSourceRoot', ('"' + $unityRoot + '"'), '-logFile', ('"' + "$qaRoot/qa.log" + '"')) -WindowStyle Hidden -PassThru
$process.WaitForExit()
Get-Content -LiteralPath "$qaRoot/result.txt"
if ($process.ExitCode -ne 0 -or !(Select-String -LiteralPath "$qaRoot/result.txt" -Pattern '^PASS' -Quiet)) { exit 1 }
