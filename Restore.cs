using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace FacilityTools
{
    /// <summary>
    /// 设施恢复核心。
    /// 全部走反射：编译期不依赖 BlastDoor / BulkheadDoor / Lift 的确切类型与成员，
    /// 因此不受 EXILED 编译版本(8.9.11)与运行版本(9.14.2)差异影响。
    ///
    /// 依据 FacilityProbe 实测报告：
    ///   BlastDoor(全局命名空间, Assembly-CSharp)
    ///     static HashSet<BlastDoor> Instances
    ///     Boolean Network_isOpen { get; set; }   Mirror SyncVar
    ///     method  Void ServerSetTargetState(Boolean isOpen)   <- 正主
    ///   LabApi.Features.Wrappers.BulkheadDoor
    ///     static IReadOnlyCollection<BulkheadDoor> List
    ///     method  Void Lock(DoorLockReason reason, Boolean enabled)
    /// </summary>
    internal static class Restore
    {
        /// <summary>
        /// 打开所有 BlastDoor。
        /// 三级策略：
        ///   A. ServerSetTargetState(true) —— 游戏自己的服务器开门方法，最正规
        ///   B. Network_isOpen = true      —— Mirror SyncVar 网络属性，有 setter，会置 dirty bit
        ///   C. _isOpen = true + SetDoorState(false, true) —— 兜底，手动触发 hook
        /// 只用 A 若成功则不再尝试其它，避免重复触发。
        /// </summary>
        public static bool OpenBlastDoors(out int opened, out string detail)
        {
            opened = 0;
            detail = "";

            Type blast = Rf.FindType("BlastDoor", "BlastDoor, Assembly-CSharp");
            if (blast == null)
            {
                detail = "BlastDoor type not found";
                return false;
            }

            List<object> inst = Rf.GetInstances(blast, out string how);
            if (inst.Count == 0)
            {
                detail = "no BlastDoor instance found (discovery: " + how + ")";
                return false;
            }

            // 现状统计（用只读的 IsOpen 属性验证）
            int closedBefore = 0;
            foreach (object o in inst)
            {
                object v = Rf.GetMember(o, blast, false, "IsOpen");
                if (v is bool b && !b) closedBefore++;
            }

            MethodInfo serverSet = Rf.GetMethod(blast, "ServerSetTargetState", false, 1);
            PropertyInfo netProp = Safe.GetProperty(blast, "Network_isOpen", Rf.All & ~BindingFlags.Static);
            FieldInfo fOpen = Safe.GetField(blast, "_isOpen", Rf.All & ~BindingFlags.Static);
            MethodInfo hook = Rf.GetMethod(blast, "SetDoorState", false, 2);

            string used = "";
            int aOk = 0, bOk = 0, cOk = 0;

            // 策略 A：ServerSetTargetState(true)
            if (serverSet != null)
            {
                foreach (object o in inst)
                {
                    try { serverSet.Invoke(o, new object[] { true }); aOk++; }
                    catch { }
                }
                if (aOk > 0) used = "ServerSetTargetState";
            }

            // 验证策略 A 是否真的生效
            if (aOk == inst.Count && CountOpen(inst, blast) == inst.Count)
            {
                opened = aOk;
                detail = "opened " + opened + "/" + inst.Count
                       + " BlastDoor via " + used
                       + " (was closed: " + closedBefore + ", discovery: " + how + ")";
                return true;
            }

            // 策略 B：写 Network_isOpen（Mirror SyncVar 网络属性，有 setter）
            if (netProp != null && netProp.CanWrite)
            {
                foreach (object o in inst)
                {
                    try { netProp.SetValue(o, true); bOk++; }
                    catch { }
                }
                if (bOk > 0 && used == "") used = "Network_isOpen setter";
            }

            if (CountOpen(inst, blast) == inst.Count)
            {
                opened = inst.Count;
                detail = "opened " + opened + "/" + inst.Count
                       + " via " + used
                       + " (A=" + aOk + " B=" + bOk + ")";
                return true;
            }

            // 策略 C：写字段 + 手动触发 hook
            foreach (object o in inst)
            {
                try
                {
                    if (fOpen != null) fOpen.SetValue(o, true);
                    if (hook != null) hook.Invoke(o, new object[] { false, true });
                    cOk++;
                }
                catch { }
            }
            if (cOk > 0 && used == "") used = "_isOpen + SetDoorState";

            int finalOpen = CountOpen(inst, blast);
            opened = finalOpen;
            bool ok = finalOpen == inst.Count;
            detail = "opened " + finalOpen + "/" + inst.Count
                   + " via " + (used == "" ? "none" : used)
                   + " (A=" + aOk + " B=" + bOk + " C=" + cOk + ")"
                   + (ok ? "" : "  << NOT ALL OPENED - wall may persist");
            return ok;
        }

        /// <summary>统计当前 IsOpen == true 的数量</summary>
        private static int CountOpen(List<object> inst, Type blast)
        {
            int n = 0;
            foreach (object o in inst)
            {
                object v = Rf.GetMember(o, blast, false, "IsOpen", "_isOpen");
                if (v is bool b && b) n++;
            }
            return n;
        }

        // ================= 2. 解锁电梯 =================

        /// <summary>
        /// 解锁所有电梯。
        /// 实测报告：11 台电梯 locked=true、operative=false 为 0。
        /// 所以问题在 locked，不在 operative —— 这里只做解锁。
        /// </summary>
        /// <summary>
        /// 解锁所有电梯。
        ///
        /// 关键教训：实测 EXILED 9.14.2 下 Door.List 不可访问，而 LabAPI 的
        /// BulkheadDoor.List 完全可用（5 扇门全部成功清锁+打开）。
        /// 因此电梯也优先走 LabAPI 的 Lift 包装，EXILED 的 Lift 仅作兜底。
        ///
        /// 上一版 A=B=C=0 的原因：三个策略的候选名全部没命中，且无异常抛出
        /// （成员根本不存在 -> 不会抛），所以既没解锁也没报错。
        /// 本版新增：候选名大幅扩充 + 全部失败时 dump 真实成员名。
        /// </summary>
        public static bool UnlockAllLifts(out int unlocked, out string detail)
        {
            unlocked = 0;
            detail = "";

            List<object> lifts = new List<object>();
            string source = "";

            // ---- 优先 LabAPI Lift ----
            Type labLift = Rf.FindType("LabApi.Features.Wrappers.Lift");
            if (labLift != null)
            {
                object raw = Rf.GetMember(null, labLift, true, "List", "Lifts", "Dictionary", "All");
                lifts.AddRange(Enumerate(raw));
                if (lifts.Count > 0) source = "LabApi.Lift";
            }

            // ---- 兜底 EXILED Lift ----
            if (lifts.Count == 0)
            {
                Type exLift = Rf.FindType("Exiled.API.Features.Lift");
                if (exLift != null)
                {
                    object raw = Rf.GetMember(null, exLift, true, "List", "Lifts");
                    lifts.AddRange(Enumerate(raw));
                    if (lifts.Count > 0) source = "Exiled.Lift";
                }
            }

            if (lifts.Count == 0)
            {
                detail = "no lift source accessible (tried LabApi.Lift.List and Exiled.Lift.List)";
                return false;
            }

            int total = lifts.Count;
            int alreadyUnlocked = 0;
            int aCount = 0, bCount = 0, cCount = 0;
            int stillLocked = 0;
            string sample = "";
            string lastErr = "";
            string diag = "";

            foreach (object lift in lifts)
            {
                if (lift == null) continue;

                bool? before = ReadLiftLocked(lift);
                if (before == false) { alreadyUnlocked++; continue; }

                string used = "";
                string err = "";
                char how = ' ';

                List<object> targets = CollectTargets(lift);

                // A. 方法（服务器方法最正规，与 BlastDoor 的 ServerSetTargetState 同理）
                foreach (object target in targets)
                {
                    if (used != "") break;
                    Type tt = target.GetType();
                    foreach (string name in UnlockMethodNames)
                    {
                        foreach (MethodInfo m in tt.GetMethods(Rf.All & ~BindingFlags.Static))
                        {
                            if (m.Name != name) continue;
                            ParameterInfo[] ps = m.GetParameters();
                            object[] args;
                            if (ps.Length == 0)
                                args = new object[0];
                            else if (ps.Length == 1 && ps[0].ParameterType == typeof(bool))
                                args = new object[] { false };
                            else if (ps.Length == 2
                                  && ps[0].ParameterType.IsEnum
                                  && ps[1].ParameterType == typeof(bool))
                                // ServerLockAllDoors(DoorLockReason reason, bool locked)
                                // 传当前锁原因 + false；取不到就用枚举 0（通常是 None）
                                args = new object[] { CurrentLockReason(target) ?? EnumZero(ps[0].ParameterType), false };
                            else continue;

                            try { m.Invoke(target, args); }
                            catch (Exception ex) { err = Inner(ex); continue; }

                            RefreshLiftLock(target);

                            if (ReadLiftLocked(lift) == false)
                            {
                                used = "A:" + m.Name + "@" + tt.Name;
                                how = 'A';
                                break;
                            }
                        }
                        if (used != "") break;
                    }
                }

                // B. 属性 setter
                if (used == "")
                {
                    foreach (object target in targets)
                    {
                        foreach (string pn in LockPropertyNames)
                        {
                            PropertyInfo p = Safe.GetProperty(target.GetType(), pn,
                                                              Rf.All & ~BindingFlags.Static);
                            if (p == null || !p.CanWrite) continue;
                            try { p.SetValue(target, false); }
                            catch (Exception ex) { err = Inner(ex); continue; }
                            RefreshLiftLock(target);
                            if (ReadLiftLocked(lift) == false)
                            {
                                used = "B:" + pn + "@" + target.GetType().Name;
                                how = 'B';
                                break;
                            }
                        }
                        if (used != "") break;
                    }
                }

                // C. 底层 bool 字段（target 必须是字段所属对象）
                if (used == "")
                {
                    foreach (object target in targets)
                    {
                        foreach (string fn in LockFieldNames)
                        {
                            FieldInfo f = Safe.GetField(target.GetType(), fn,
                                                        Rf.All & ~BindingFlags.Static);
                            if (f == null || f.FieldType != typeof(bool)) continue;
                            try { f.SetValue(f.IsStatic ? null : target, false); }
                            catch (Exception ex) { err = Inner(ex); continue; }
                            RefreshLiftLock(target);
                            if (ReadLiftLocked(lift) == false)
                            {
                                used = "C:" + fn + "@" + target.GetType().Name;
                                how = 'C';
                                break;
                            }
                        }
                        if (used != "") break;
                    }
                }

                if (used != "")
                {
                    unlocked++;
                    if (how == 'A') aCount++;
                    else if (how == 'B') bCount++;
                    else cCount++;
                    if (sample == "") sample = used;
                }
                else
                {
                    stillLocked++;
                    if (err != "") lastErr = err;
                }
            }

            detail = "source=" + source
                   + " unlocked " + unlocked + "/" + total
                   + " (already=" + alreadyUnlocked + ", still=" + stillLocked + ")"
                   + " A=" + aCount + " B=" + bCount + " C=" + cCount
                   + (sample == "" ? "" : ("  e.g. " + sample))
                   + (lastErr == "" ? "" : ("  lastErr=" + lastErr));

            // 全部失败时 dump 真实成员名，便于一次性定位
            if (unlocked + alreadyUnlocked < total)
            {
                diag = DumpLiftMembers(lifts[0]);
                if (diag != "")
                {
                    detail += "\n[DIAG] available members on first lift:\n" + diag;
                    try { Exiled.API.Features.Log.Warn("[FacilityRestore] lift diag:\n" + diag); }
                    catch { }
                }
            }

            return unlocked + alreadyUnlocked >= total && total > 0;
        }

        /// <summary>把静态成员（集合或字典）摊平成对象列表</summary>
        private static List<object> Enumerate(object raw)
        {
            List<object> list = new List<object>();
            if (raw == null) return list;

            if (raw is IDictionary dict)
            {
                foreach (object v in dict.Values) if (v != null) list.Add(v);
                return list;
            }
            IEnumerable en = raw as IEnumerable;
            if (en != null && !(raw is string))
            {
                foreach (object v in en) if (v != null) list.Add(v);
            }
            return list;
        }

        /// <summary>包装对象 + 其底层对象（Base 等），作为反射候选目标</summary>
        private static List<object> CollectTargets(object wrapper)
        {
            List<object> list = new List<object>();
            if (wrapper == null) return list;
            list.Add(wrapper);

            Type t = wrapper.GetType();
            foreach (string hop in BaseHopNames)
            {
                object next = null;
                try { next = Rf.GetMember(wrapper, t, false, hop); } catch { }
                if (next == null) continue;
                bool dup = false;
                foreach (object o in list) if (ReferenceEquals(o, next)) { dup = true; break; }
                if (!dup) list.Add(next);
            }
            return list;
        }

        /// <summary>读取电梯锁定状态（包装对象 + 底层都试）</summary>
        private static bool? ReadLiftLocked(object lift)
        {
            if (lift == null) return null;
            foreach (object target in CollectTargets(lift))
            {
                object v = Rf.GetMember(target, target.GetType(), false, LockPropertyNames);
                if (v is bool b) return b;
            }

            // 兜底：ActiveLocksAnyDoors / ActiveLocksAllDoors 是非零位标记，
            // 报告显示它们只有 getter，但可用来判断"是否还锁着"。
            foreach (object target in CollectTargets(lift))
            {
                object any = Rf.GetMember(target, target.GetType(), false,
                                          "ActiveLocksAnyDoors", "ActiveLocksAllDoors");
                if (any == null) continue;
                try
                {
                    int bits = (int)Convert.ChangeType(any, typeof(int));
                    return bits != 0;
                }
                catch { }
            }
            return null;
        }

        /// <summary>取当前锁原因枚举值，供 2 参的 ServerLockAllDoors(reason, false) 使用</summary>
        private static object CurrentLockReason(object target)
        {
            object any = Rf.GetMember(target, target.GetType(), false,
                                      "ActiveLocksAnyDoors", "ActiveLocksAllDoors",
                                      "LockReason", "ActiveLocks");
            return any;
        }

        /// <summary>枚举零值（通常是 None）</summary>
        private static object EnumZero(Type enumType)
        {
            try { return Enum.ToObject(enumType, 0); }
            catch { return Activator.CreateInstance(enumType); }
        }

        /// <summary>改完锁定标记后调用，让游戏内部状态同步（报告：UpdateDynamicLock）</summary>
        private static void RefreshLiftLock(object target)
        {
            Type tt = target.GetType();
            foreach (string name in RefreshMethodNames)
            {
                foreach (MethodInfo m in tt.GetMethods(Rf.All & ~BindingFlags.Static))
                {
                    if (m.Name != name) continue;
                    if (m.GetParameters().Length != 0) continue;
                    try { m.Invoke(target, new object[0]); } catch { }
                    return;
                }
            }
        }

        /// <summary>列出一台电梯上所有与锁定相关的成员（诊断用）</summary>
        private static string DumpLiftMembers(object lift)
        {
            if (lift == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (object target in CollectTargets(lift))
            {
                Type t = target.GetType();
                sb.Append("  -- ").Append(t.FullName).Append(" --\n");
                sb.Append("  [bool props]\n");
                foreach (PropertyInfo p in t.GetProperties(Rf.All & ~BindingFlags.Static))
                {
                    if (p.PropertyType != typeof(bool)) continue;
                    sb.Append("    ").Append(p.Name)
                      .Append("  get=").Append(p.CanRead ? "Y" : "N")
                      .Append(" set=").Append(p.CanWrite ? "Y" : "N").Append("\n");
                }
                sb.Append("  [bool fields]\n");
                foreach (FieldInfo f in t.GetFields(Rf.All & ~BindingFlags.Static))
                    if (f.FieldType == typeof(bool))
                        sb.Append("    ").Append(f.Name).Append("\n");
                sb.Append("  [lock-ish methods]\n");
                foreach (MethodInfo m in t.GetMethods(Rf.All & ~BindingFlags.Static))
                {
                    if (m.IsSpecialName) continue;
                    string mn = m.Name;
                    if (mn.IndexOf("Lock", StringComparison.OrdinalIgnoreCase) < 0
                     && mn.IndexOf("Unlock", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sb.Append("    ").Append(m.Name).Append("(");
                    ParameterInfo[] ps = m.GetParameters();
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(ps[i].ParameterType.Name);
                    }
                    sb.Append(")\n");
                }
            }
            return sb.ToString();
        }

        private static string Inner(Exception ex)
        {
            Exception e = ex.InnerException ?? ex;
            return e.GetType().Name + ": " + e.Message;
        }

        /// <summary>底层对象候选成员名</summary>
        private static readonly string[] BaseHopNames =
        {
            "Base", "_base", "Elevator", "Chamber", "ElevatorChamber", "_chamber", "Target"
        };

        /// <summary>
        /// 解锁方法候选名。
        /// 报告实证（ElevatorChamber）：ServerLockAllDoors(DoorLockReason, Boolean)
        /// —— 两个参数，第二个 bool 即"是否锁定"，传 false 解锁。
        /// 上一版只接受 0 参或单 bool 参，2 参的直接 continue 跳过 -> A=0。
        /// </summary>
        private static readonly string[] UnlockMethodNames =
        {
            "ServerLockAllDoors", "ServerSetLocked", "ServerSetLock", "ServerSetLockState",
            "ServerLock", "SetLocked", "SetLock", "SetLockState",
            "ChangeLock", "ChangeLockState", "Unlock", "UnlockLift", "Lock", "LockLift"
        };

        /// <summary>锁定属性候选名（报告：DynamicAdminLock get=Y set=Y，可写）</summary>
        private static readonly string[] LockPropertyNames =
        {
            "DynamicAdminLock", "IsLocked", "Network_locked", "NetworkisLocked",
            "Locked", "IsBlocked", "NetworkisBlocked", "NetworkLocked"
        };

        /// <summary>锁定字段候选名（报告：ElevatorChamber 的 bool 字段是 _dynamicAdminLock）</summary>
        private static readonly string[] LockFieldNames =
        {
            "_dynamicAdminLock", "dynamicAdminLock", "_locked", "locked",
            "_isLocked", "isLocked", "Network_locked", "_networkLocked",
            "_isBlocked", "isBlocked"
        };

        /// <summary>改完锁定状态后调用，让游戏刷新锁定（报告：ElevatorChamber.UpdateDynamicLock）</summary>
        private static readonly string[] RefreshMethodNames =
        {
            "UpdateDynamicLock", "UpdateLock", "RefreshLock", "UpdateLocks"
        };

        private static string NameOf(object o)
        {
            return o == null ? "null" : o.GetType().Name;
        }

        private static bool IsNumeric(Type t)
        {
            return t == typeof(ushort) || t == typeof(short)
                || t == typeof(int) || t == typeof(uint)
                || t == typeof(long) || t == typeof(ulong)
                || t == typeof(float) || t == typeof(double);
        }


        // ================= 3. 清除重型门的 Warhead 锁 =================

        /// <summary>
        /// 清除门上由核弹施加的 Warhead 锁定标记。
        /// 实测：EXILED 9.14.2 下 Door.List 不可访问，改用 LabAPI 的 BulkheadDoor.List（5 个）。
        /// 对重型门：IsOpened=true 并解除 Warhead 锁。
        /// </summary>
        public static bool RemoveWarheadLocks(out int affected, out string detail)
        {
            affected = 0;
            detail = "";

            // 优先走 LabAPI（报告中 BulkheadDoor.List 可正常读取）
            Type bulk = Rf.FindType("LabApi.Features.Wrappers.BulkheadDoor");
            if (bulk != null)
            {
                List<object> doors = Rf.GetInstances(bulk, out string how);
                if (doors.Count > 0)
                {
                    Type reasonEnum = Rf.FindType("Interactables.Interobjects.DoorUtils.DoorLockReason",
                                               "DoorLockReason");
                    object warheadFlag = null;
                    if (reasonEnum != null && reasonEnum.IsEnum)
                    {
                        try { warheadFlag = Enum.Parse(reasonEnum, "Warhead"); }
                        catch { }
                    }

                    MethodInfo lockMi = Rf.GetMethod(bulk, "Lock", false, 2);
                    PropertyInfo openedProp = Safe.GetProperty(bulk, "IsOpened", Rf.All & ~BindingFlags.Static);

                    int total = 0, lockCleared = 0, openedSet = 0;
                    foreach (object d in doors)
                    {
                        total++;
                        Type dt = d.GetType();

                        // 清 Warhead 锁
                        object reason = Rf.GetMember(d, dt, false, "LockReason");
                        if (reason != null && lockMi != null)
                        {
                            try { lockMi.Invoke(d, new[] { reason, (object)false }); lockCleared++; }
                            catch { }
                        }
                        else if (warheadFlag != null && lockMi != null)
                        {
                            try { lockMi.Invoke(d, new[] { warheadFlag, (object)false }); lockCleared++; }
                            catch { }
                        }

                        // 打开门
                        if (openedProp != null && openedProp.CanWrite)
                        {
                            try { openedProp.SetValue(d, true); openedSet++; }
                            catch { }
                        }
                    }

                    affected = lockCleared;
                    detail = "LabAPI BulkheadDoor: " + total + " doors, lock cleared=" + lockCleared
                           + ", opened=" + openedSet + " (discovery: " + how + ")";
                    return lockCleared > 0 || openedSet > 0;
                }
            }

            // 回退：EXILED Door 集合（8.x 可用，9.14.2 可能不可访问）
            Type doorType = Rf.FindType("Exiled.API.Features.Door");
            Type lockEnum = Rf.FindType("Exiled.API.Enums.DoorLockType", "DoorLockType");
            if (doorType == null || lockEnum == null)
            {
                if (detail == "")
                    detail = "neither LabAPI BulkheadDoor nor EXILED Door accessible";
                return false;
            }

            object rawDoors = Rf.GetMember(null, doorType, true, "List", "Doors");
            IEnumerable doorsEnum = rawDoors as IEnumerable;
            if (doorsEnum == null)
            {
                detail += " | EXILED Door.List not accessible (expected on 9.14.2)";
                return false;
            }

            int total2 = 0, cleared2 = 0;
            int whVal = 0;
            try
            {
                object flag = Enum.Parse(lockEnum, "Warhead");
                whVal = (int)Convert.ChangeType(flag, typeof(int));
            }
            catch { }

            foreach (object d in doorsEnum)
            {
                if (d == null) continue;
                total2++;
                Type dt = d.GetType();
                PropertyInfo lp = Safe.GetProperty(dt, "LockType", Rf.All & ~BindingFlags.Static)
                               ?? Safe.GetProperty(dt, "ActiveLocks", Rf.All & ~BindingFlags.Static);
                if (lp == null || !lp.CanWrite) continue;
                try
                {
                    object cur = lp.GetValue(d);
                    if (cur == null) continue;
                    int cv = (int)Convert.ChangeType(cur, typeof(int));
                    if ((cv & whVal) == 0) continue;
                    int nv = cv & ~whVal;
                    lp.SetValue(d, Enum.ToObject(lockEnum, nv));
                    cleared2++;
                }
                catch { }
            }

            affected = cleared2;
            detail += " | EXILED Door: " + total2 + " doors, warhead lock cleared=" + cleared2;
            return cleared2 > 0;
        }

        // ================= 4. 核弹状态 =================

        public static bool IsWarheadDetonated(out string detail)
        {
            detail = "";
            Type wh = Rf.FindType("Exiled.API.Features.Warhead");
            if (wh == null)
            {
                detail = "Warhead type not found";
                return false;
            }

            object v = Rf.GetMember(null, wh, true, "IsDetonated", "HasDetonated", "Detonated");
            if (v is bool b)
            {
                detail = "IsDetonated=" + b;
                return b;
            }

            object st = Rf.GetMember(null, wh, true, "Status");
            detail = "Status=" + (st == null ? "null" : st.ToString());
            return st != null &&
                   st.ToString().IndexOf("Detonated", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ================= 5. 广播 =================

        public static bool TryBroadcast(string message, ushort duration, out string detail)
        {
            detail = "";
            if (string.IsNullOrWhiteSpace(message))
            {
                detail = "empty message";
                return false;
            }

            Type map = Rf.FindType("Exiled.API.Features.Map");
            if (map == null)
            {
                detail = "Map type not found";
                return false;
            }

            // 选重载：必须含 string 参数，优先参数最少、含数值参数的那个。
            // 上一版取第一个 ps.Length>=2 的重载，若它是带 BroadcastFlags 等额外参数的重载，
            // 其余参数会被填成 null —— 方法内部解引用 null 即抛 NullReferenceException。
            MethodInfo mi = null;
            int bestScore = int.MaxValue;
            foreach (MethodInfo m in map.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "Broadcast" && m.Name != "ShowBroadcast") continue;
                ParameterInfo[] ps = m.GetParameters();

                bool hasString = false, hasNumeric = false;
                foreach (ParameterInfo p in ps)
                {
                    if (p.ParameterType == typeof(string)) hasString = true;
                    if (IsNumeric(p.ParameterType)) hasNumeric = true;
                }
                if (!hasString || !hasNumeric) continue;

                if (ps.Length < bestScore) { bestScore = ps.Length; mi = m; }
            }
            if (mi == null)
            {
                detail = "Broadcast method not found (no overload with string+numeric)";
                return false;
            }

            try
            {
                ParameterInfo[] ps = mi.GetParameters();
                object[] args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;
                    if (pt == typeof(string)) args[i] = message;
                    else if (pt == typeof(ushort)) args[i] = duration;
                    else if (pt == typeof(short)) args[i] = (short)duration;
                    else if (pt == typeof(int)) args[i] = (int)duration;
                    else if (pt == typeof(float)) args[i] = (float)duration;
                    else if (pt == typeof(bool)) args[i] = true;
                    else if (pt.IsEnum) args[i] = Enum.GetValues(pt).GetValue(0);
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                    else args[i] = null;
                }
                mi.Invoke(null, args);
                detail = "broadcast sent (" + duration + "s, overload " + ps.Length + " args)";
                return true;
            }
            catch (Exception ex)
            {
                detail = "broadcast failed: " + (ex.InnerException?.Message ?? ex.Message);
                return false;
            }
        }
    }
}
