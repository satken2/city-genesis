param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$project = Get-Content "$unityRoot/Assembly-CSharp.csproj"
    $core = $project.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path ($core.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/ConstructionPathQA'
foreach ($folder in 'Assets/Editor','Assets/Models/Props','Assets/Models/Paths','Assets/Resources/Shaders','Assets/Shaders','Assets/Settings','Packages','ProjectSettings') {
    New-Item -ItemType Directory -Path "$qaRoot/$folder" -Force | Out-Null
}
Copy-Item "$PSScriptRoot/ConstructionPathQA.cs" "$qaRoot/Assets/Editor" -Force
foreach ($file in 'ModelPrefabGenerator.cs','ModelImportSettings.cs') { Copy-Item "$unityRoot/Assets/Editor/$file" "$qaRoot/Assets/Editor" -Force }
Copy-Item "$unityRoot/Assets/Models/Props/construction_*.fbx*" "$qaRoot/Assets/Models/Props" -Force
foreach ($variant in 'p1','p2','p4','p401') {
    New-Item -ItemType Directory -Path "$qaRoot/Assets/Models/Paths/$variant" -Force | Out-Null
    Copy-Item "$unityRoot/Assets/Models/Paths/$variant/*" "$qaRoot/Assets/Models/Paths/$variant" -Force
}
Copy-Item "$unityRoot/Assets/Resources/Shaders/PathDirt.shader*" "$qaRoot/Assets/Resources/Shaders" -Force
Copy-Item "$unityRoot/Assets/Shaders/PathDirt.hlsl" "$qaRoot/Assets/Shaders" -Force
Copy-Item "$unityRoot/Assets/Resources/ConstructionPathDirt.mat*" "$qaRoot/Assets/Resources" -Force
foreach ($name in 'PC_RPAsset.asset','PC_Renderer.asset','DefaultVolumeProfile.asset','UniversalRenderPipelineGlobalSettings.asset') {
    Copy-Item "$unityRoot/Assets/Settings/$name*" "$qaRoot/Assets/Settings" -Force
}
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings" -Force
$manifest = Get-Content "$unityRoot/Packages/manifest.json" -Raw | ConvertFrom-Json -AsHashtable
$manifest.dependencies.Remove('com.rlabrecque.steamworks.net')
$manifest.dependencies.Remove('com.siccity.gltfutility')
$manifest | ConvertTo-Json -Depth 10 | Set-Content "$qaRoot/Packages/manifest.json"
Set-Content "$qaRoot/result.txt" 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @('-batchmode','-projectPath',('"'+$qaRoot+'"'),'-executeMethod','ConstructionPathQA.Run','-sourceRoot',('"'+$unityRoot+'"'),'-logFile',('"'+$qaRoot+'/qa.log"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(300000)) { Stop-Process -Id $process.Id; throw 'Construction QA timed out; inspect Temp/ConstructionPathQA/qa.log' }
$result = Get-Content "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Construction QA failed' }
# 正規の生成器が作ったFBX設定・ミラーを新規4モデルだけ本編へ戻す。
foreach ($category in 'Models/Props','Resources/Models/Props') {
    New-Item -ItemType Directory -Path "$unityRoot/Assets/$category" -Force | Out-Null
    Get-ChildItem "$qaRoot/Assets/$category" -Filter 'construction_*' -File | ForEach-Object {
        $target = "$unityRoot/Assets/$category/$($_.Name)"
        if (!(Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $_.FullName -Destination $target }
    }
}
