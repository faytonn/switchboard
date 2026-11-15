# Starts the Switchboard hub automatically when you log in to Windows.
#
# It registers a Task Scheduler task that runs scripts/serve.sh inside WSL, in a hidden console
# (conhost --headless). While that wsl.exe process runs, WSL stays up, so the hub, tmux and the
# sessions keep running even with every terminal closed. If the hub exits with an error,
# Task Scheduler starts it again after a minute.
#
# Usage (PowerShell, from the repo):   .\deploy\windows\install-task.ps1 [-Distro Ubuntu]
# Remove:                              .\deploy\windows\uninstall-task.ps1
param([string]$Distro = "Ubuntu")

$ErrorActionPreference = "Stop"
$taskName = "Switchboard hub"
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$repoInWsl = (wsl.exe -d $Distro -e wslpath -a ($repo -replace '\\', '/')).Trim()

# bash -lc: a login shell, so ~/.profile sets PATH. The log lives in WSL, not on the Windows disk.
# No double quotes inside: this string is itself double-quoted on the wsl.exe command line.
$command = "exec '$repoInWsl/scripts/serve.sh' --log ~/.switchboard/hub.log"
$action = New-ScheduledTaskAction -Execute "conhost.exe" `
    -Argument "--headless wsl.exe -d $Distro --exec bash -lc `"$command`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
    -Description "Runs the Switchboard hub (scripts/serve.sh) in WSL at logon and keeps WSL running." -Force | Out-Null

Write-Host "Registered '$taskName'. It starts at your next logon; to start it now:"
Write-Host "  Start-ScheduledTask -TaskName '$taskName'"
Write-Host "Logs: wsl -e tail -f ~/.switchboard/hub.log"
