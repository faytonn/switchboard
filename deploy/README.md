# Running Switchboard always-on

`scripts/serve.sh` is the single entry point. It waits for Postgres, builds if needed, and runs
the hub in the foreground. Each platform only decides *when* to run it:

| Where | How | Files |
|---|---|---|
| Windows + WSL (now) | Task Scheduler task at logon, hidden | `windows/install-task.ps1`, `windows/uninstall-task.ps1` |
| Linux server / Raspberry Pi (later) | systemd user service + `loginctl enable-linger` | `systemd/switchboard-hub.service` |

The hub then keeps the manager session alive by itself (see `ManagerSupervisor`).

## Why a scheduled task and not `.wslconfig`

Microsoft's WSL config docs (wsl-config, updated 2026-10-16) list two timeouts:

- `[general] instanceIdleTimeout`: "The number of milliseconds that a distro is idle, before it
  is shut down. Set to -1 to disable auto shutdown" (default 15000).
- `[wsl2] vmIdleTimeout`: "The number of milliseconds that a VM is idle, before it is shut down"
  (default 60000).

Setting `instanceIdleTimeout=-1` would stop WSL from shutting down, but it can't *start*
anything after a reboot, and it changes every distro on the machine (Docker Desktop's too).
The logon task does both jobs: it starts the hub, and its open `wsl.exe` keeps the distro up.
The docs don't define "idle" or say whether background processes like tmux count, so we don't
rely on that.

## Also needed on Windows

- Docker Desktop: Settings > General > "Start Docker Desktop when you sign in", so Postgres
  comes up too (the container has `restart: unless-stopped`).
