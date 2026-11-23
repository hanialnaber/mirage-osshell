# Starts Mirage with --smoke on a real Windows desktop, screenshots the screen
# while it runs, and waits for its report (memory, CPU, views that mounted).
param([string]$Exe, [string]$Out)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$report = Join-Path $Out 'smoke-windows.json'
$env:MIRAGE_SMOKE_OUT = $report
$p = Start-Process -FilePath $Exe -ArgumentList '--smoke' -PassThru
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
function Shot($name) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
Start-Sleep -Seconds 12; Shot 'windows-runner-1'
Start-Sleep -Seconds 8; Shot 'windows-runner-2'
$deadline = (Get-Date).AddSeconds(120)
while (-not (Test-Path $report) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
if (-not (Test-Path $report)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; throw 'Mirage did not write its smoke report' }
Start-Sleep -Seconds 3
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
Get-Content $report
