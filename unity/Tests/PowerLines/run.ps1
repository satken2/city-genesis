$ErrorActionPreference='Stop'
$unityRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& "$PSScriptRoot/../BuildRuntime.ps1"
$qaRoot=Join-Path $unityRoot 'Temp/PowerLinesQA'
New-Item -ItemType Directory -Force -Path "$qaRoot/Assets/Editor","$qaRoot/ProjectSettings","$qaRoot/Packages","$qaRoot/Assets/Models/Paths" | Out-Null
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs","$unityRoot/Assets/Editor/ModelImportSettings.cs","$PSScriptRoot/PowerLinesQA.cs" "$qaRoot/Assets/Editor" -Force
Set-Content "$qaRoot/Packages/manifest.json" '{"dependencies":{"com.unity.render-pipelines.universal":"17.4.0","com.unity.nuget.newtonsoft-json":"3.2.2"}}'
foreach($id in 1101,1102) {
    $dest="$qaRoot/Assets/Models/Paths/p$id"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    foreach($suffix in @('','_close')) {
        $file="$unityRoot/Assets/Models/Paths/p$id/pillar_cap$suffix.fbx"
        Copy-Item $file $dest -Force
        if(Test-Path "$file.meta"){Copy-Item "$file.meta" $dest -Force}
    }
}
$railDest="$qaRoot/Assets/Models/Paths/p421"
New-Item -ItemType Directory -Force -Path $railDest | Out-Null
Copy-Item "$unityRoot/Assets/Models/Paths/p421/*.fbx*" $railDest -Force
[xml]$projectXml=Get-Content "$unityRoot/Assembly-CSharp.csproj" -Raw
$coreReference=$projectXml.Project.ItemGroup.Reference | Where-Object {$_.Include -eq 'UnityEngine.CoreModule'}
$editorRoot=$coreReference.HintPath -replace '[\\/]Data[\\/].*$',''
$process=Start-Process -FilePath "$editorRoot/Unity.exe" -ArgumentList @('-batchmode','-projectPath',('"'+$qaRoot+'"'),'-executeMethod','PowerLinesQA.Run','-sourceRoot',('"'+$unityRoot+'"'),'-logFile',('"'+$qaRoot+'/qa.log"')) -WindowStyle Hidden -PassThru
if(!$process.WaitForExit(240000)){Stop-Process -Id $process.Id; throw 'Power line QA timed out'}
$result=Get-Content "$qaRoot/result.txt" -Raw
Write-Output $result
if($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')){exit 1}
# Synchronize only new wrappers created by the standard generator, preserving existing GUIDs.
foreach($id in 1101,1102) {
    foreach($meta in Get-ChildItem "$qaRoot/Assets/Models/Paths/p$id" -Filter '*_close.fbx.meta') {
        $target="$unityRoot/Assets/Models/Paths/p$id/$($meta.Name)"
        if(Test-Path $target){
            if((Select-String $target -Pattern '^guid:').Line -ne (Select-String $meta.FullName -Pattern '^guid:').Line){throw "GUID mismatch: $target"}
        }else{Copy-Item $meta.FullName $target}
    }
    $targetDir="$unityRoot/Assets/Resources/Models/Paths/p$id"
    foreach($file in Get-ChildItem "$qaRoot/Assets/Resources/Models/Paths/p$id" -Filter '*_close.prefab*') {
        $target=Join-Path $targetDir $file.Name
        if(!(Test-Path $target)){Copy-Item $file.FullName $target}
    }
}
