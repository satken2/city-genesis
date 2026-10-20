$targets = Get-CimInstance Win32_Process | Where-Object {
  $_.CommandLine -like '*city\city-genesis/tauri*' -and (
    $_.Name -in @('node.exe', 'cargo.exe', 'city-genesis/tauri.exe')
  )
}

if (-not $targets) {
  Write-Host "No city-genesis/tauri dev processes found."
  exit 0
}

$targets | Select-Object ProcessId, Name, CommandLine | Format-Table -AutoSize
$targets | ForEach-Object {
  Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
}

Write-Host "Stopped city-genesis/tauri dev processes."
