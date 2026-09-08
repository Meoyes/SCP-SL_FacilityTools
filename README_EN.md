[中文](./README.md) | [English](README_EN.md)
# FacilityTools (Probe + Facility Restore Combined Plugin)

The result of 3 hours of hard work with Yuanbao AI :)

## Commands

| Command | Description |
|---|---|
| `probe` | Full probe (BlastDoor / BulkheadDoor / PryableDoor / Lift / Door), writes report to a file |
| `probe open` | Attempt to open BlastDoor (the wall at the nuke room) |
| `probe lift` | Deep scan of elevators, find the actual unlock path |
| `restorefacility` (alias `rf`) | Restore facility: open walls + unlock lifts + clear door locks + broadcast |

Both commands are **simultaneously registered** for RemoteAdmin (in-game RA, press `~`) and GameConsole (server backend window).

## Compilation & Deployment

1. Double-click `build.bat`
2. Copy `bin\Release\net48\FacilityTools.dll` to `%AppData%\EXILED\Plugins\`
3. **Restart the server**

## Configuration (Auto-generated on startup)

```yaml
facility_tools:
  is_enabled: true
  debug: false
  report_path: C:/Users/18151/AppData/Roaming/EXILED/FacilityTools_report.txt
  auto_restore: true
  restore_delay_seconds: 3
  poll_interval_seconds: 1
  unlock_lifts: true
  remove_warhead_locks: true
  broadcast_on_restore: true
  broadcast_duration: 5
  broadcast_message: 设施已恢复：电梯重新启用，通往地下区域的通道已开放。
```

Remember to **modify the path**! It is recommended to use **forward slashes** without quotes (in YAML, `\` inside double quotes is an escape character and will cause parsing failures).

## File Structure

| File | Content |
|---|---|
| `Reflect.cs` | `Safe` (prevents Ambiguous reflection) + `Rf` (shared reflection utilities) |
| `Probe.cs` | Probe logic: `RunProbe` / `TryOpenBlastDoors` / `ProbeLifts` |
| `Restore.cs` | Restore logic: `OpenBlastDoors` / `UnlockAllLifts` / `RemoveWarheadLocks` |
| `Program.cs` | Config + two RA commands + plugin main body + auto-restore monitor |

## Validation

```bat
python validate.py
python check_cs0136.py
```
