#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
FacilityTools 合并插件校验脚本。

检查项：
  1. 结构：括号配对（剥离注释与字符串后统计）
  2. API 用法：PluginPriority（非 PluginPriorityLevel）、IConfig.Debug
  3. 环境冲突：build.bat 不得含 --no-restore；restore 不得带 -f net48
  4. 合并正确性：类型不重名、Rf 引用完整、无残留旧私有定义
  5. RA 命令数量与覆盖项
"""
import re
import os
import sys

BASE = os.path.dirname(os.path.abspath(__file__))


def read(name):
    p = os.path.join(BASE, name)
    return open(p, encoding="utf-8").read() if os.path.isfile(p) else ""


def strip_noise(code):
    """剥离注释与字符串，避免其中的括号干扰统计"""
    out, i, n = [], 0, len(code)
    while i < n:
        c = code[i]
        if c == '/' and i + 1 < n and code[i + 1] == '/':
            while i < n and code[i] != '\n':
                i += 1
        elif c == '/' and i + 1 < n and code[i + 1] == '*':
            i += 2
            while i + 1 < n and not (code[i] == '*' and code[i + 1] == '/'):
                i += 1
            i += 2
        elif c == '"':
            i += 1
            while i < n:
                if code[i] == '\\':
                    i += 2
                    continue
                if code[i] == '"':
                    i += 1
                    break
                i += 1
        else:
            out.append(c)
            i += 1
    return ''.join(out)


results = []


def check(label, ok, extra=""):
    results.append((label, ok, extra))


SRC = ["Reflect.cs", "Probe.cs", "Restore.cs", "Program.cs"]

# ---------- 1. 结构 ----------
struct_ok = True
for f in SRC:
    code = strip_noise(read(f))
    if code.count('{') != code.count('}') or code.count('(') != code.count(')'):
        struct_ok = False
check("括号配对 (4 files)", struct_ok)

# ---------- 2. API 用法 ----------
prog = read("Program.cs")
# 注意：注释里会出现 "PluginPriorityLevel"（用于说明它不是正确名字），
#       必须基于剥离注释后的代码判断，否则误报。
prog_code = strip_noise(prog)
check("使用 PluginPriority (ILSpy 确认名)",
      "PluginPriority.Default" in prog_code and "PluginPriorityLevel" not in prog_code)
check("Config 含 Debug (防 CS0535)", "public bool Debug { get; set; }" in prog)
check("继承 Plugin<Config>", "Plugin<Config>" in prog)
check("OnEnabled/OnDisabled 配对",
      "public override void OnEnabled()" in prog and "public override void OnDisabled()" in prog)

# ---------- 3. 环境冲突 ----------
bat = read("build.bat")
check("build.bat 自动 restore (无 --no-restore)",
      "restore" in bat.lower() and "--no-restore" not in bat)
check("restore 不带 -f net48 (防 MSB1009)",
      not re.search(r'dotnet\s+restore[^\n]*-f\s+net48', bat))
check("build.bat 纯 ASCII (防中文乱码)",
      all(ord(ch) < 128 for ch in bat))

# ---------- 4. 合并正确性 ----------
# 4a 类型不重名
types = {}
for f in SRC:
    for m in re.finditer(r'^\s*(?:public|internal|private)\s+(?:static\s+|sealed\s+|abstract\s+)*class\s+(\w+)',
                         read(f), re.M):
        types.setdefault(m.group(1), []).append(f)
dupes = {k: v for k, v in types.items() if len(v) > 1}
check("无重名类型", not dupes, str(dupes) if dupes else f"{len(types)} 个类型")

# 4b Safe 类唯一
safe_files = [f for f in SRC if "class Safe" in read(f)]
check("Safe 类唯一 (防 Ambiguous)", len(safe_files) == 1, str(safe_files))

# 4c Rf 引用完整
rf = read("Reflect.cs")
rf_members = set(re.findall(r'public static (?:[\w<>\[\], ?]+) (\w+)\s*\(', rf))
rf_members |= set(re.findall(r'public const BindingFlags (\w+)', rf))
used = set()
for f in ["Probe.cs", "Restore.cs", "Program.cs"]:
    used |= set(re.findall(r'\bRf\.(\w+)', read(f)))
missing = used - rf_members
check("Rf 引用完整", not missing, f"缺失 {sorted(missing)}" if missing else f"{len(used)} 个成员")

# 4d 无残留旧私有定义（已移入 Rf 的不得再出现在 Probe/Restore）
residue_pat = re.compile(
    r'private (?:const BindingFlags All\s*=|'
    r'static Type FindType\(|static Type FindBySimpleName\(|'
    r'static object GetMember\(|static object CallStatic\(|'
    r'static List<object> GetInstances\(|'
    r'static List<object> FindObjectsOfType|static MethodInfo GetMethod\()')
residue = [f for f in ["Probe.cs", "Restore.cs"] if residue_pat.search(read(f))]
check("无残留旧私有定义", not residue, str(residue))

# 4e 字符串未被误替换（"Rf.All" 出现在字符串里是 bug）
strbug = []
for f in ["Probe.cs", "Restore.cs"]:
    for m in re.finditer(r'"[^"\n]*Rf\.\w+[^"\n]*"', read(f)):
        # 允许 "Rf.All" 作为 GetMember 的候选名参数是不合法的，全部视为误伤
        strbug.append(f + ": " + m.group(0)[:50])
check("字符串未误替换", not strbug, "; ".join(strbug))

# ---------- 5. 命令覆盖 ----------
cmds = re.findall(r'public string Command \{ get; \} = "(\w+)"', prog)
check("RA 命令数 = 2", len(cmds) == 2, str(cmds))
check("含 probe 命令", "probe" in cmds)
check("含 restorefacility 命令", "restorefacility" in cmds)
check("probe 子命令 open", '"open"' in read("Program.cs"))
check("probe 子命令 lift", '"lift"' in read("Program.cs"))
check("覆盖恢复项: 电梯+门锁",
      "Restore.UnlockAllLifts" in prog and "Restore.RemoveWarheadLocks" in prog)
check("覆盖恢复项: BlastDoor 墙", "Restore.OpenBlastDoors" in prog)
check("双 handler 注册 (RA+后台)",
      prog.count("RemoteAdminCommandHandler") >= 2 and prog.count("GameConsoleCommandHandler") >= 2)

# ---------- 输出 ----------
print("=" * 46)
print(" FacilityTools validate")
print("=" * 46)
width = max(len(r[0]) for r in results)
for label, ok, extra in results:
    flag = "OK  " if ok else "FAIL"
    line = f"  [{flag}] {label.ljust(width)}"
    if extra:
        line += "  " + extra
    print(line)

failed = [r for r in results if not r[1]]
print()
if failed:
    print(f"有 {len(failed)} 项未通过")
    sys.exit(1)
print("所有检查项通过（API 用法 + 环境冲突 + 合并正确性）")
