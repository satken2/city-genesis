param([string]$UnityExe)
$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot '../BuildRuntime.ps1')
if (!$UnityExe) {
    [xml]$projectXml = Get-Content "$unityRoot/Assembly-CSharp.csproj"
    $core = $projectXml.Project.ItemGroup.Reference | Where-Object { $_.Include -eq 'UnityEngine.CoreModule' }
    $UnityExe = Join-Path ($core.HintPath -replace '[\\/]Data[\\/].*$', '') 'Unity.exe'
}
$qaRoot = Join-Path $unityRoot 'Temp/OpenSpaceQA'
New-Item -ItemType Directory -Path "$qaRoot/Assets/Editor", "$qaRoot/ProjectSettings" -Force | Out-Null
Copy-Item "$unityRoot/ProjectSettings/ProjectVersion.txt" "$qaRoot/ProjectSettings/ProjectVersion.txt" -Force
Copy-Item "$PSScriptRoot/OpenSpaceQA.cs" "$qaRoot/Assets/Editor/OpenSpaceQA.cs" -Force
Copy-Item "$unityRoot/Assets/Editor/ModelPrefabGenerator.cs" "$qaRoot/Assets/Editor/ModelPrefabGenerator.cs" -Force
New-Item -ItemType Directory -Path "$qaRoot/Assets/Models/Props/Textures","$qaRoot/Assets/Models/Buildings/Textures" -Force | Out-Null
Copy-Item "$unityRoot/Assets/Models/Props/Textures/PROP_LEAF_GREEN_color.png*" "$qaRoot/Assets/Models/Props/Textures" -Force
# 駅前ロータリーの上屋の広告とタクシー乗り場の看板は建物と共有の画像を使い、インポーターが同名の共有材質へ再マップしている。
New-Item -ItemType Directory -Path "$qaRoot/Assets/Models/Buildings/Materials" -Force | Out-Null
foreach ($name in 'BD_MALL_ADS','BD_SIGN_ATLAS') {
    Copy-Item "$unityRoot/Assets/Models/Buildings/Textures/$name.png*" "$qaRoot/Assets/Models/Buildings/Textures" -Force
    if (Test-Path "$unityRoot/Assets/Models/Buildings/Materials/$name.mat") { Copy-Item "$unityRoot/Assets/Models/Buildings/Materials/$name.mat*" "$qaRoot/Assets/Models/Buildings/Materials" -Force }
}
foreach ($id in 3001,3002,3003,3004) {
    New-Item -ItemType Directory -Path "$qaRoot/Assets/Models/Buildings/a$id/0" -Force | Out-Null
    Get-ChildItem "$unityRoot/Assets/Models/Buildings/a$id/0" -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination "$qaRoot/Assets/Models/Buildings/a$id/0/$($_.Name)" -Force
    }
}
Set-Content "$qaRoot/result.txt" 'RUNNING'
$process = Start-Process -FilePath $UnityExe -ArgumentList @('-batchmode','-projectPath',('"'+$qaRoot+'"'),'-executeMethod','OpenSpaceQA.Run','-sourceRoot',('"'+$unityRoot+'"'),'-logFile',('"'+"$qaRoot/qa.log"+'"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw 'Open space QA timed out' }
$result = Get-Content "$qaRoot/result.txt" -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS')) { throw 'Open space QA failed' }
# 本編エディタが起動中でも、正規の生成器によるミラーを対象4件だけ同期できる。
foreach ($id in 3001,3002,3003,3004) {
    foreach ($category in 'Models/Buildings','Resources/Models/Buildings') {
        $source = "$qaRoot/Assets/$category/a$id"
        $destination = "$unityRoot/Assets/$category/a$id"
        New-Item -ItemType Directory -Path "$destination/0" -Force | Out-Null
        Get-ChildItem "$source/0" -File | ForEach-Object {
            $target = "$destination/0/$($_.Name)"
            if (!(Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $_.FullName -Destination $target }
        }
    }
}
$layoutDir = Join-Path $unityRoot '../../genesis-gallery/pipeline/layouts'
New-Item -ItemType Directory -Path $layoutDir -Force | Out-Null
foreach ($id in 3001,3002,3004) { Copy-Item "$qaRoot/a$id.json" "$layoutDir/a$id.json" -Force }
