$targets = Get-CimInstance Win32_Process | Where-Object {
  $_.CommandLine -like '*city\genesis-ui*' -and (
    $_.Name -in @('node.exe', 'cargo.exe', 'genesis-ui.exe')
  )
}

if (-not $targets) {
  Write-Host "No genesis-ui dev processes found."
  exit 0
}

$targets | Select-Object ProcessId, Name, CommandLine | Format-Table -AutoSize
$targets | ForEach-Object {
  Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
}

Write-Host "Stopped genesis-ui dev processes."
