# FacilityTools（探测 + 设施恢复 合并插件）

元宝AI和我一起奋战三小时的成果 :)

## 命令

| 命令 | 作用 |
|---|---|
| `probe` | 完整探测（BlastDoor / BulkheadDoor / PryableDoor / Lift / Door），报告写文件 |
| `probe open` | 尝试打开 BlastDoor（核爆那道墙） |
| `probe lift` | 电梯深度扫描，找真正的解锁通道 |
| `restorefacility`（别名 `rf`） | 恢复设施：开墙 + 解锁电梯 + 清门锁 + 广播 |

两条命令**同时注册** RemoteAdmin（游戏内 RA，按 `~`）与 GameConsole（服务器后台窗口）。

## 编译部署

1. 双击 `build.bat`
2. 把 `bin\Release\net48\FacilityTools.dll` 复制到 `%AppData%\EXILED\Plugins\`
3. **重启服务器**

## 配置（启动后自动生成）

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

路径建议用**正斜杠**且不加引号（YAML 双引号里 `\` 是转义字符，会解析失败）。

## 文件结构

| 文件 | 内容 |
|---|---|
| `Reflect.cs` | `Safe`（防 Ambiguous 反射）+ `Rf`（共享反射工具） |
| `Probe.cs` | 探测逻辑：`RunProbe` / `TryOpenBlastDoors` / `ProbeLifts` |
| `Restore.cs` | 恢复逻辑：`OpenBlastDoors` / `UnlockAllLifts` / `RemoveWarheadLocks` |
| `Program.cs` | Config + 两条 RA 命令 + 插件主体 + 自动恢复监视器 |

## 校验

```bat
python validate.py
python check_cs0136.py .
```
