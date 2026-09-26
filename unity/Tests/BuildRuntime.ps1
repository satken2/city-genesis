$ErrorActionPreference = 'Stop'
$unityRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $unityRoot 'Assembly-CSharp.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$known = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$targets = Join-Path $unityRoot 'Temp/LodQA/LodBuild.targets'
New-Item -ItemType Directory -Path (Split-Path $targets) -Force | Out-Null
$document = [xml]'<Project><ItemGroup /></Project>'
$group = $document.SelectSingleNode('/Project/ItemGroup')
$group.SetAttribute('Condition', "'" + '$(MSBuildProjectName)' + "' == 'Assembly-CSharp'")
# Unityが生成したcsprojは次にエディタが開くまで更新されないので、削除・改名済みのソースは外し、新しいソースは足す。
foreach ($item in $projectXml.Project.ItemGroup.Compile) {
    if (!$item.Include) { continue }
    $path = [IO.Path]::GetFullPath((Join-Path $unityRoot $item.Include))
    [void]$known.Add($path)
    if (!(Test-Path -LiteralPath $path)) {
        $element = $document.CreateElement('Compile')
        $element.SetAttribute('Remove', $item.Include)
        [void]$group.AppendChild($element)
    }
}
Get-ChildItem -LiteralPath (Join-Path $unityRoot 'Assets/Scripts') -Filter '*.cs' -File -Recurse | ForEach-Object {
    if (!$known.Contains($_.FullName)) {
        $element = $document.CreateElement('Compile')
        $element.SetAttribute('Include', $_.FullName)
        [void]$group.AppendChild($element)
    }
}
$document.Save($targets)
& dotnet build $project -v quiet /clp:ErrorsOnly "-p:CustomAfterMicrosoftCommonTargets=$targets"
if ($LASTEXITCODE -ne 0) { throw 'Unity runtime assembly build failed' }
