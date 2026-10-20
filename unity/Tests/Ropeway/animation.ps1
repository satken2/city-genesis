$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& "$unityRoot/Tests/BuildRuntime.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
[xml]$projectXml = Get-Content -LiteralPath "$unityRoot/Assembly-CSharp.csproj" -Raw
$core = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
$editorRoot = $core.HintPath -replace '[\\/]Data[\\/].*$', ''
$qaRoot = Join-Path $unityRoot 'Temp/RopewayAnimationQA'
New-Item -ItemType Directory -Force -Path "$qaRoot/Assets/Editor", "$qaRoot/ProjectSettings", "$qaRoot/Packages" | Out-Null
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item "$PSScriptRoot/RopewayAnimationQA.cs", "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs", "$unityRoot/Assets/Editor/ModelImportSettings.cs" "$qaRoot/Assets/Editor" -Force
Set-Content "$qaRoot/Packages/manifest.json" '{"dependencies":{}}'
foreach ($id in @(601,602)) {
    $dest="$qaRoot/Assets/Models/Paths/p$id"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Get-ChildItem "$unityRoot/Assets/Models/Paths/p$id" -Filter '*.fbx*' | Copy-Item -Destination $dest -Force
}
Set-Content "$qaRoot/animation-result.txt" 'RUNNING'
$process=Start-Process "$editorRoot/Unity.exe" -ArgumentList @('-batchmode','-projectPath',('"'+$qaRoot+'"'),'-executeMethod','RopewayAnimationQA.Run','-sourceRoot',('"'+$unityRoot+'"'),'-logFile',('"'+$qaRoot+'/qa.log"')) -WindowStyle Hidden -PassThru
if(!$process.WaitForExit(240000)){Stop-Process -Id $process.Id; throw 'Ropeway animation QA timed out'}
$result=Get-Content "$qaRoot/animation-result.txt" -Raw
Write-Output $result
if($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')){exit 1}
# Preserve the GUIDs of actual FBXs and synchronize only new assets created by the standard prefab generator.
foreach($part in @('p601/cabin','p602/terminal_rotor','p602/terminal_support')) {
    $meta="Assets/Models/Paths/$part.fbx.meta"
    if(Test-Path "$unityRoot/$meta") {
        if((Select-String -LiteralPath "$unityRoot/$meta" -Pattern '^guid:').Line -ne (Select-String -LiteralPath "$qaRoot/$meta" -Pattern '^guid:').Line){throw "GUID mismatch: $part"}
    } else { Copy-Item "$qaRoot/$meta" "$unityRoot/$meta" }
    foreach($suffix in @('.prefab','.prefab.meta')) {
        $relative="Assets/Resources/Models/Paths/$part$suffix"
        if(!(Test-Path "$unityRoot/$relative")){Copy-Item "$qaRoot/$relative" "$unityRoot/$relative"}
    }
}
