# Removes the logon task and stops the hub it started. Sessions in tmux keep running until WSL
# shuts down or you stop them.
$taskName = "Switchboard hub"
Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
wsl.exe -e pkill -f "Switchboard.Hub"
Write-Host "Removed '$taskName'."
