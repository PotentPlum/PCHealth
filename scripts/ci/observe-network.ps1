# Launches the built app and watches the network endpoints it owns (T-36, spec §3.5 and §9).
# The app is not touched after launch, so nothing a user would start (such as the update check) runs.
# A control process with a loopback connection proves that the polling sees connections at all.
param(
    [Parameter(Mandatory)][string]$Exe,
    [int]$Seconds = 30
)
$ErrorActionPreference = 'Stop'

function Get-Endpoints([int]$ProcessId) {
    @(Get-NetTCPConnection -OwningProcess $ProcessId -ErrorAction SilentlyContinue |
        ForEach-Object { "TCP $($_.LocalAddress):$($_.LocalPort) -> $($_.RemoteAddress):$($_.RemotePort) $($_.State)" }) +
    @(Get-NetUDPEndpoint -OwningProcess $ProcessId -ErrorAction SilentlyContinue |
        ForEach-Object { "UDP $($_.LocalAddress):$($_.LocalPort)" })
}

# Control: a process holding a loopback connection must be seen.
$control = Start-Process pwsh -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', @'
$l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start()
$c = [Net.Sockets.TcpClient]::new(); $c.Connect([Net.IPAddress]::Loopback, $l.LocalEndpoint.Port)
Start-Sleep -Seconds 20
'@
$controlSeen = $false
for ($i = 0; $i -lt 60 -and -not $controlSeen; $i++) {
    Start-Sleep -Milliseconds 250
    $controlSeen = (Get-Endpoints $control.Id).Count -gt 0
}
Stop-Process -Id $control.Id -Force -ErrorAction SilentlyContinue
if (-not $controlSeen) { Write-Error 'The control connection was not seen; this check cannot observe connections here.' }
Write-Host 'Control: a loopback connection was seen.'

$app = Start-Process -FilePath $Exe -PassThru
$seen = @()
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    if ($app.HasExited) { Write-Error "The app exited during startup with code $($app.ExitCode)." }
    $seen += Get-Endpoints $app.Id
    Start-Sleep -Milliseconds 200
}

$app.Refresh()
$title = $app.MainWindowTitle
Stop-Process -Id $app.Id -Force

if ($title) { Write-Host "Main window shown: '$title'." }
else { Write-Host '::warning::The app ran but no main window title was visible (no interactive desktop?).' }

$seen = @($seen | Sort-Object -Unique)
if ($seen.Count -gt 0) {
    $seen | ForEach-Object { Write-Host $_ }
    Write-Error "The app used the network in its first $Seconds seconds without being asked."
}

Write-Host "No TCP connection or UDP endpoint owned by the app in its first $Seconds seconds."
