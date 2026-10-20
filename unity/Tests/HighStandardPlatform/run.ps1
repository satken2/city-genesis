param([switch]$SyncPrefabs)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $unityRoot 'Tests/BuildRuntime.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
[xml]$projectXml = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
$coreReference = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
$editorRoot = $coreReference.HintPath -replace '[\\/]Data[\\/].*$', ''
$qaRoot = Join-Path $unityRoot 'Temp/HighStandardPlatformQA'
New-Item -ItemType Directory -Force -Path "$qaRoot/Assets/Editor", "$qaRoot/Assets/Models/Paths/p462", "$qaRoot/Packages", "$qaRoot/ProjectSettings" | Out-Null
Copy-Item -LiteralPath "$unityRoot/ProjectSettings/ProjectVersion.txt" -Destination "$qaRoot/ProjectSettings" -Force
Copy-Item -LiteralPath "$PSScriptRoot/HighStandardPlatformQA.cs", "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs", "$unityRoot/Assets/Editor/ModelImportSettings.cs" -Destination "$qaRoot/Assets/Editor" -Force
Get-ChildItem -LiteralPath "$unityRoot/Assets/Models/Paths/p462" -File | Copy-Item -Destination "$qaRoot/Assets/Models/Paths/p462" -Force
Set-Content -LiteralPath "$qaRoot/Packages/manifest.json" -Value '{"dependencies":{}}'
Set-Content -LiteralPath "$qaRoot/result.txt" -Value 'RUNNING'
$process = Start-Process -FilePath (Join-Path $editorRoot 'Unity.exe') -ArgumentList @('-batchmode','-projectPath',('"'+$qaRoot+'"'),'-executeMethod','HighStandardPlatformQA.Run','-sourceRoot',('"'+$unityRoot+'"'),'-logFile',('"'+$qaRoot+'/qa.log"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Platform QA timed out' }
$result = Get-Content -LiteralPath "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { exit 1 }
if ($SyncPrefabs) {
    $prefabs = "$unityRoot/Assets/Resources/Models/Paths/p462"
    New-Item -ItemType Directory -Force -Path $prefabs | Out-Null
    foreach ($source in Get-ChildItem -LiteralPath "$qaRoot/Assets/Models/Paths/p462" -Filter '*.fbx.meta') {
        $target = Join-Path "$unityRoot/Assets/Models/Paths/p462" $source.Name
        if (Test-Path -LiteralPath $target) {
            $a = (Select-String -LiteralPath $source.FullName -Pattern '^guid:').Line
            $b = (Select-String -LiteralPath $target -Pattern '^guid:').Line
            if ($a -ne $b) { throw "FBX GUID changed during QA: $target" }
        } else { Copy-Item -LiteralPath $source.FullName -Destination $target }
    }
    foreach ($file in Get-ChildItem -LiteralPath "$qaRoot/Assets/Resources/Models/Paths/p462" -File) {
        $target = Join-Path $prefabs $file.Name
        if (!(Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $file.FullName -Destination $target }
    }
    foreach ($relative in @('Models/Paths/p462.meta','Resources/Models/Paths/p462.meta')) {
        if (!(Test-Path -LiteralPath "$unityRoot/Assets/$relative")) {
            Copy-Item -LiteralPath "$qaRoot/Assets/$relative" -Destination "$unityRoot/Assets/$relative"
        }
    }
}
