using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace FacilityTools
{
    /// <summary>
    /// 纯反射探测：不依赖任何易变类型名，编译期不会因 BlastDoor / BulkheadDoor
    /// 的命名空间或成员差异而报错。
    /// </summary>
    internal static class Probe
    {
        private static void DescribeType(StringBuilder sb, Type t, string label)
        {
            sb.Append("---- ").Append(label).Append(" ----\n");

            if (t == null)
            {
                sb.Append("  NOT FOUND\n\n");
                return;
            }

            sb.Append("  FullName   : ").Append(t.FullName).Append("\n");
            sb.Append("  Namespace  : ").Append(string.IsNullOrEmpty(t.Namespace)
                                                ? "(global - no namespace)"
                                                : t.Namespace).Append("\n");
            sb.Append("  Assembly   : ").Append(t.Assembly.GetName().Name).Append("\n");

            StringBuilder chain = new StringBuilder();
            Type cur = t.BaseType;
            int depth = 0;
            while (cur != null && depth < 8)
            {
                chain.Append(cur.Name);
                cur = cur.BaseType;
                if (cur != null) chain.Append(" -> ");
                depth++;
            }
            sb.Append("  Base chain : ").Append(t.Name).Append(" -> ")
              .Append(chain.Length > 0 ? chain.ToString() : "(none)").Append("\n\n");

            // 静态成员
            sb.Append("  [STATIC members - find how to enumerate all instances]\n");
            int sn = 0;
            foreach (PropertyInfo p in t.GetProperties(Rf.All & ~BindingFlags.Instance))
            {
                if (++sn > 20) break;
                sb.Append("    prop ").Append(Short(p.PropertyType)).Append(" ").Append(p.Name)
                  .Append(" get=").Append(p.CanRead ? "Y" : "N")
                  .Append(" set=").Append(p.CanWrite ? "Y" : "N").Append("\n");
            }
            foreach (FieldInfo f in t.GetFields(Rf.All & ~BindingFlags.Instance))
            {
                if (++sn > 20) break;
                sb.Append("    field ").Append(Short(f.FieldType)).Append(" ").Append(f.Name).Append("\n");
            }
            if (sn == 0) sb.Append("    (none)\n");

            sb.Append("\n  [STATIC methods]\n");
            int sm = 0;
            foreach (MethodInfo m in t.GetMethods(Rf.All & ~BindingFlags.Instance))
            {
                if (m.IsSpecialName) continue;
                if (++sm > 20) break;
                sb.Append("    ").Append(Short(m.ReturnType)).Append(" ").Append(m.Name).Append("(");
                ParameterInfo[] ps = m.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Short(ps[i].ParameterType));
                }
                sb.Append(")\n");
            }
            if (sm == 0) sb.Append("    (none)\n");

            // 实例属性
            sb.Append("\n  [INSTANCE properties - look for IsOpen]\n");
            int ip = 0;
            foreach (PropertyInfo p in t.GetProperties(Rf.All & ~BindingFlags.Static))
            {
                if (++ip > 30) break;
                sb.Append("    ").Append(Short(p.PropertyType)).Append(" ").Append(p.Name)
                  .Append(" get=").Append(p.CanRead ? "Y" : "N")
                  .Append(" set=").Append(p.CanWrite ? "Y" : "N").Append("\n");
            }
            if (ip == 0) sb.Append("    (none)\n");

            // 实例字段
            sb.Append("\n  [INSTANCE fields - look for _isOpen]\n");
            int iff = 0;
            foreach (FieldInfo f in t.GetFields(Rf.All & ~BindingFlags.Static))
            {
                if (++iff > 30) break;
                sb.Append("    ").Append(Short(f.FieldType)).Append(" ").Append(f.Name)
                  .Append(f.IsPublic ? "" : "  (private)").Append("\n");
            }
            if (iff == 0) sb.Append("    (none)\n");

            // 关键方法
            sb.Append("\n  [INSTANCE methods - Open/Close/Lock/State]\n");
            int im = 0;
            foreach (MethodInfo m in t.GetMethods(Rf.All & ~BindingFlags.Static))
            {
                if (m.IsSpecialName) continue;
                string mn = m.Name;
                bool hit = mn.IndexOf("Open", StringComparison.OrdinalIgnoreCase) >= 0
                        || mn.IndexOf("Clos", StringComparison.OrdinalIgnoreCase) >= 0
                        || mn.IndexOf("Lock", StringComparison.OrdinalIgnoreCase) >= 0
                        || mn.IndexOf("State", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hit) continue;
                if (++im > 25) break;
                sb.Append("    ").Append(Short(m.ReturnType)).Append(" ").Append(m.Name).Append("(");
                ParameterInfo[] ps = m.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Short(ps[i].ParameterType)).Append(" ").Append(ps[i].Name);
                }
                sb.Append(")\n");
            }
            if (im == 0) sb.Append("    (none matched)\n");

            sb.Append("\n");
        }

        private static string Short(Type t)
        {
            if (t == null) return "?";
            if (t.IsGenericType)
            {
                string n = t.Name;
                int i = n.IndexOf('`');
                if (i > 0) n = n.Substring(0, i);
                n += "<";
                Type[] a = t.GetGenericArguments();
                for (int k = 0; k < a.Length; k++)
                {
                    if (k > 0) n += ",";
                    n += a[k].Name;
                }
                return n + ">";
            }
            return t.Name;
        }

        // ---------- 现场状态 ----------

        private static string DumpState(Type t, string[] members)
        {
            if (t == null) return "  (type missing)\n";

            List<object> inst = Rf.GetInstances(t, out string how);
            StringBuilder sb = new StringBuilder();
            sb.Append("  discovery      : ").Append(how).Append("\n");
            sb.Append("  instance count : ").Append(inst.Count).Append("\n");

            int shown = 0;
            foreach (object o in inst)
            {
                if (shown >= 10) break;
                shown++;
                Type ot = o.GetType();
                sb.Append("   #").Append(shown).Append(": ");
                bool first = true;
                foreach (string mem in members)
                {
                    object v = Rf.GetMember(o, ot, false, mem);
                    if (!first) sb.Append("  ");
                    sb.Append(mem).Append("=").Append(v == null ? "null" : v.ToString());
                    first = false;
                }
                sb.Append("\n");
            }
            if (inst.Count > shown) sb.Append("   ... ").Append(inst.Count - shown).Append(" more\n");
            if (inst.Count == 0) sb.Append("   (none found - round not started, or wrong discovery)\n");

            return sb.ToString();
        }

        // ---------- 主探测 ----------

        /// <summary>分段容错：某一段崩了不影响其它段继续出报告</summary>
        private static void SafeDescribe(StringBuilder sb, Type t, string label)
        {
            try { DescribeType(sb, t, label); }
            catch (Exception ex)
            {
                sb.Append("---- ").Append(label).Append(" ----\n")
                  .Append("  PROBE ERROR: ").Append(ex.GetType().Name).Append(": ")
                  .Append(ex.Message).Append("\n\n");
            }
        }

        /// <summary>分段容错：DumpState 版本</summary>
        private static void SafeDump(StringBuilder sb, Type t, string[] members)
        {
            try { sb.Append(DumpState(t, members)); }
            catch (Exception ex)
            {
                sb.Append("  PROBE ERROR: ").Append(ex.GetType().Name).Append(": ")
                  .Append(ex.Message).Append("\n");
            }
        }

        public static string RunProbe(out string summary)
        {
            StringBuilder f = new StringBuilder();
            StringBuilder s = new StringBuilder();

            f.Append("========================================\n");
            f.Append(" FacilityProbe runtime diagnostics\n");
            f.Append(" ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\n");
            f.Append("========================================\n\n");

            // 0 程序集
            f.Append("[0] Assemblies\n");
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string n = asm.GetName().Name;
                    if (n == null) continue;
                    if (n == "Assembly-CSharp" || n.StartsWith("Exiled") || n.StartsWith("LabApi"))
                        f.Append("    ").Append(n).Append(" v").Append(asm.GetName().Version).Append("\n");
                }
            }
            catch { }
            f.Append("\n");

            // 1 BlastDoor
            Type blast = Rf.FindType("BlastDoor", "BlastDoor, Assembly-CSharp");
            f.Append("[1] NATIVE BlastDoor\n");
            f.Append("    = 核爆瞬间降下的防爆挡板，就是挡住电梯、穿不过去的那层'墙'\n");
            f.Append("    默认 _isOpen=true(敞开)，核爆时设为 false 才关上\n");
            f.Append("    >>> 这是恢复地下的核心目标 <<<\n");
            SafeDescribe(f, blast, "BlastDoor");
            if (blast != null)
            {
                f.Append("[1b] BlastDoor LIVE STATE\n");
                SafeDump(f, blast, new[] { "_isOpen", "IsOpen", "name" });
                f.Append("\n");
            }

            // 2 BulkheadDoor
            Type bulk = Rf.FindType("LabApi.Features.Wrappers.BulkheadDoor");
            f.Append("[2] LABAPI BulkheadDoor\n");
            f.Append("    = Heavy Containment Zone 的重型门(常规设施，地图本来就有)\n");
            f.Append("    注意：这不是核爆那道墙，别和 BlastDoor 混淆\n");
            f.Append("    核爆时它会被上 Warhead 锁，需要解锁而非'打开'\n");
            SafeDescribe(f, bulk, "BulkheadDoor");
            if (bulk != null)
            {
                f.Append("[2b] BulkheadDoor LIVE STATE\n");
                SafeDump(f, bulk, new[] { "IsOpen", "Base", "Type" });
                f.Append("\n");
            }

            // 3 PryableDoor
            Type pry = Rf.FindType("PryableDoor", "PryableDoor, Assembly-CSharp");
            f.Append("[3] NATIVE PryableDoor (BulkheadDoor.Base)\n");
            f.Append("    = 重型门的原生基类(可撬门)，带碰撞体\n");
            if (pry == null)
            {
                f.Append("  NOT FOUND\n\n");
            }
            else
            {
                f.Append("  FullName : ").Append(pry.FullName)
                 .Append("  ns=").Append(string.IsNullOrEmpty(pry.Namespace) ? "(global)" : pry.Namespace)
                 .Append("\n\n");
                SafeDump(f, pry, new[] { "IsOpen", "IsLocked", "TargetState" });
                f.Append("\n");
            }

            // 4 验证器
            f.Append("[4] VALIDATOR AnyBlastdoorClosed\n");
            f.Append("    = '是否有防爆门处于关闭状态'，用来验证恢复是否成功\n");
            f.Append("    恢复成功应返回 False\n");
            Type panel = Rf.FindType("AlphaWarheadNukesitePanel", "AlphaWarheadNukesitePanel, Assembly-CSharp");
            string v4;
            if (panel == null)
            {
                v4 = "TYPE NOT FOUND";
                f.Append("  ").Append(v4).Append("\n\n");
            }
            else
            {
                object v = Rf.GetMember(null, panel, true, "AnyBlastdoorClosed");
                v4 = v == null ? "null" : v.ToString();
                f.Append("  AnyBlastdoorClosed = ").Append(v4).Append("\n\n");
            }
            s.Append("AnyBlastdoorClosed : ").Append(v4).Append("\n");

            // 5 核弹
            f.Append("[5] WARHEAD STATE\n");
            Type wh = Rf.FindType("Exiled.API.Features.Warhead");
            string whState = "n/a";
            if (wh == null)
            {
                f.Append("  Warhead type not found\n");
            }
            else
            {
                object v = Rf.GetMember(null, wh, true, "IsDetonated", "HasDetonated", "Detonated");
                if (v is bool bb)
                {
                    whState = bb.ToString();
                    f.Append("  IsDetonated = ").Append(bb).Append("\n");
                }
                object st = Rf.GetMember(null, wh, true, "Status");
                if (st != null)
                {
                    f.Append("  Status = ").Append(st).Append("\n");
                    if (!(v is bool)) whState = st.ToString();
                }
            }
            f.Append("\n");
            s.Append("Warhead state      : ").Append(whState).Append("\n");

            // 6 电梯
            f.Append("[6] LIFTS (operative = the flag blocking interaction)\n");
            Type liftType = Rf.FindType("Exiled.API.Features.Lift");
            string liftInfo = "n/a";
            if (liftType == null)
            {
                f.Append("  Lift type not found\n");
            }
            else
            {
                object raw = Rf.GetMember(null, liftType, true, "List", "Lifts");
                IEnumerable lifts = raw as IEnumerable;
                if (lifts == null)
                {
                    f.Append("  Lift.List not accessible\n");
                }
                else
                {
                    int n = 0, opFalse = 0, lkTrue = 0;
                    StringBuilder lb = new StringBuilder();
                    foreach (object lift in lifts)
                    {
                        if (lift == null) continue;
                        n++;
                        Type lt = lift.GetType();
                        object op = Rf.GetMember(lift, lt, false, "IsOperative");
                        object lk = Rf.GetMember(lift, lt, false, "IsLocked");
                        object ty = Rf.GetMember(lift, lt, false, "Type");
                        if (op is bool ob && !ob) opFalse++;
                        if (lk is bool lbv && lbv) lkTrue++;
                        if (n <= 10)
                            lb.Append("   [").Append(ty).Append("] operative=").Append(op)
                              .Append(" locked=").Append(lk).Append("\n");
                    }
                    f.Append("  total=").Append(n).Append("  operative=false: ").Append(opFalse)
                     .Append("  locked=true: ").Append(lkTrue).Append("\n");
                    f.Append(lb);
                    liftInfo = "total=" + n + " operative_false=" + opFalse;
                }
            }
            f.Append("\n");
            s.Append("Lifts              : ").Append(liftInfo).Append("\n");

            // 7 EXILED 门
            f.Append("[7] EXILED DOORS with Warhead lock\n");
            Type doorType = Rf.FindType("Exiled.API.Features.Door");
            Type lockEnum = Rf.FindType("Exiled.API.Enums.DoorLockType");
            string doorInfo = "n/a";
            if (doorType == null || lockEnum == null)
            {
                f.Append("  Door type or DoorLockType not found\n");
            }
            else
            {
                object raw = Rf.GetMember(null, doorType, true, "List", "Doors");
                IEnumerable doors = raw as IEnumerable;
                if (doors == null)
                {
                    f.Append("  Door.List not accessible\n");
                }
                else
                {
                    int total = 0, whCount = 0, whVal = 0;
                    try
                    {
                        object flag = Enum.Parse(lockEnum, "Warhead");
                        whVal = (int)Convert.ChangeType(flag, typeof(int));
                    }
                    catch { }
                    foreach (object d in doors)
                    {
                        if (d == null) continue;
                        total++;
                        Type dt = d.GetType();
                        object cur = Rf.GetMember(d, dt, false, "LockType", "DoorLockType", "ActiveLocks");
                        if (cur == null || cur.GetType() != lockEnum) continue;
                        try
                        {
                            int cv = (int)Convert.ChangeType(cur, typeof(int));
                            if ((cv & whVal) != 0) whCount++;
                        }
                        catch { }
                    }
                    f.Append("  doors=").Append(total).Append("  Warhead locked=").Append(whCount).Append("\n");
                    doorInfo = "total=" + total + " warhead_locked=" + whCount;
                }
            }
            f.Append("\n");
            s.Append("EXILED doors       : ").Append(doorInfo).Append("\n");

            // 摘要
            StringBuilder head = new StringBuilder();
            head.Append("=== FacilityProbe SUMMARY ===\n");
            head.Append("BlastDoor native   : ").Append(blast == null ? "NOT FOUND" : "FOUND")
                .Append("   <- 核爆防爆门(那道墙)\n");
            if (blast != null)
            {
                List<object> bi = Rf.GetInstances(blast, out string bh);
                head.Append("  instances        : ").Append(bi.Count).Append(" (via ").Append(bh).Append(")\n");
                FieldInfo fi = Safe.GetField(blast, "_isOpen", Rf.All & ~BindingFlags.Static);
                head.Append("  _isOpen field    : ").Append(fi == null ? "NOT FOUND" : "writable").Append("\n");
            }
            head.Append("BulkheadDoor api   : ").Append(bulk == null ? "NOT FOUND" : "FOUND")
                .Append("   <- HCZ重型门(非那道墙)\n");
            if (bulk != null)
            {
                List<object> bi = Rf.GetInstances(bulk, out string bh);
                head.Append("  instances        : ").Append(bi.Count).Append(" (via ").Append(bh).Append(")\n");
                PropertyInfo pi = Safe.GetProperty(bulk, "IsOpen", Rf.All & ~BindingFlags.Static);
                head.Append("  IsOpen setter    : ")
                    .Append(pi == null ? "no property" : (pi.CanWrite ? "YES (direct)" : "read only")).Append("\n");
            }
            head.Append("PryableDoor        : ").Append(pry == null ? "NOT FOUND" : "FOUND")
                .Append("   <- 重型门原生基类\n");
            head.Append("\nSend the FULL report back for exact wiring.\n\n");

            summary = head.ToString() + s.ToString();
            return f.ToString();
        }

        /// <summary>
        /// 多策略开门诊断。
        /// BlastDoor._isOpen 是 [SyncVar(hook="SetDoorState")]，反射写字段不会触发 hook，
        /// 客户端不同步、碰撞体不动 —— 所以必须逐个尝试真正的状态切换通道。
        /// </summary>
        public static string TryOpenBlastDoors()
        {
            StringBuilder sb = new StringBuilder();
            Type blast = Rf.FindType("BlastDoor", "BlastDoor, Assembly-CSharp");

            if (blast == null)
            {
                sb.Append("BlastDoor type NOT FOUND. Cannot proceed.\n");
                return sb.ToString();
            }

            sb.Append("Type: ").Append(blast.FullName)
              .Append("  ns=").Append(string.IsNullOrEmpty(blast.Namespace) ? "(global)" : blast.Namespace)
              .Append("\n");

            List<object> inst = Rf.GetInstances(blast, out string how);
            sb.Append("Instances: ").Append(inst.Count).Append("  (discovery: ").Append(how).Append(")\n");

            if (inst.Count == 0)
            {
                sb.Append(">> NO INSTANCE FOUND - this is why nothing happened.\n");
                sb.Append("   BlastDoor may not be the blocking wall, or it is not spawned yet.\n");
                return sb.ToString();
            }

            // ---- 现状 ----
            FieldInfo fOpen = Safe.GetField(blast, "_isOpen", Rf.All & ~BindingFlags.Static);
            if (fOpen != null)
            {
                int closed = 0;
                foreach (object o in inst)
                {
                    try
                    {
                        object val = fOpen.GetValue(o);
                        if (val is bool b && !b) closed++;
                    }
                    catch { }
                }
                sb.Append("_isOpen field found. Currently CLOSED(false): ")
                  .Append(closed).Append("/").Append(inst.Count).Append("\n");
                if (closed == 0)
                    sb.Append(">> All already open(true) - BlastDoor is probably NOT the blocking wall!\n");
            }
            else
            {
                sb.Append("_isOpen field NOT FOUND on BlastDoor.\n");
            }

            // ---- 列出候选方法 ----
            sb.Append("\n[Methods on BlastDoor]\n");
            foreach (MethodInfo m in blast.GetMethods(Rf.All & ~BindingFlags.Static))
            {
                if (m.IsSpecialName) continue;
                string mn = m.Name;
                bool hit = mn.IndexOf("SetDoor", StringComparison.OrdinalIgnoreCase) >= 0
                        || mn.IndexOf("Open", StringComparison.OrdinalIgnoreCase) >= 0
                        || mn.IndexOf("Clos", StringComparison.OrdinalIgnoreCase) >= 0
                        || mn.IndexOf("State", StringComparison.OrdinalIgnoreCase) >= 0
                        || mn.IndexOf("Sync", StringComparison.OrdinalIgnoreCase) >= 0
                        || mn.IndexOf("Rpc", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hit) continue;
                sb.Append("  ").Append(m.ReturnType.Name).Append(" ").Append(m.Name).Append("(");
                ParameterInfo[] ps = m.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(ps[i].ParameterType.Name);
                }
                sb.Append(")\n");
            }

            // ---- 策略 A: 写字段 ----
            sb.Append("\n[Strategy A] write _isOpen = true\n");
            int aOk = 0;
            if (fOpen != null)
            {
                foreach (object o in inst)
                {
                    try { fOpen.SetValue(o, true); aOk++; } catch { }
                }
            }
            sb.Append("  -> set on ").Append(aOk).Append("/").Append(inst.Count).Append("\n");

            // ---- 策略 B: 调 SetDoorState hook ----
            sb.Append("\n[Strategy B] invoke SetDoorState hook\n");
            int bOk = 0;
            string bErr = "";
            MethodInfo hook = null;
            foreach (MethodInfo m in blast.GetMethods(Rf.All & ~BindingFlags.Static))
            {
                if (m.Name == "SetDoorState") { hook = m; break; }
            }
            if (hook == null)
            {
                sb.Append("  -> SetDoorState method NOT FOUND\n");
            }
            else
            {
                ParameterInfo[] ps = hook.GetParameters();
                sb.Append("  -> found SetDoorState(");
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(ps[i].ParameterType.Name);
                }
                sb.Append(")\n");

                foreach (object o in inst)
                {
                    try
                    {
                        object[] args;
                        if (ps.Length == 2) args = new object[] { false, true };
                        else if (ps.Length == 1) args = new object[] { true };
                        else args = new object[ps.Length];
                        hook.Invoke(o, args);
                        bOk++;
                    }
                    catch (Exception ex)
                    {
                        Exception inner = ex.InnerException ?? ex;
                        if (bErr == "") bErr = inner.GetType().Name + ": " + inner.Message;
                    }
                }
                sb.Append("  -> invoked on ").Append(bOk).Append("/").Append(inst.Count)
                  .Append(bErr == "" ? "" : ("  err=" + bErr)).Append("\n");
            }

            // ---- 策略 C: 公开开关方法 ----
            sb.Append("\n[Strategy C] public open/set methods\n");
            int cOk = 0;
            string cName = "";
            foreach (string mname in new[] { "SetDoor", "ServerSetDoor", "Open", "OpenDoor", "SetOpen" })
            {
                MethodInfo mi = blast.GetMethod(mname, Rf.All & ~BindingFlags.Static);
                if (mi == null) continue;
                ParameterInfo[] ps = mi.GetParameters();
                foreach (object o in inst)
                {
                    try
                    {
                        object[] args = new object[ps.Length];
                        for (int i = 0; i < ps.Length; i++)
                            args[i] = ps[i].ParameterType == typeof(bool) ? (object)true : null;
                        mi.Invoke(o, args);
                        cOk++;
                    }
                    catch { }
                }
                if (cOk > 0) { cName = mname; break; }
            }
            sb.Append(cOk > 0
                ? ("  -> " + cName + " invoked on " + cOk + " instance(s)\n")
                : "  -> no usable public method found\n");

            // ---- 策略 D: GameObject / collider ----
            sb.Append("\n[Strategy D] object active state\n");
            try
            {
                int activeCnt = 0, total = 0;
                foreach (object o in inst)
                {
                    total++;
                    object active = Rf.GetMember(o, blast, false, "isActiveAndEnabled", "enabled");
                    if (active is bool ab && ab) activeCnt++;
                }
                sb.Append("  -> active ").Append(activeCnt).Append("/").Append(total)
                  .Append(" (BlastDoor is NetworkBehaviour; the wall collider may be a child object)\n");
            }
            catch (Exception ex) { sb.Append("  -> check failed: ").Append(ex.Message).Append("\n"); }

            // ---- 验证 ----
            Type panel = Rf.FindType("AlphaWarheadNukesitePanel", "AlphaWarheadNukesitePanel, Assembly-CSharp");
            object v = panel == null ? null : Rf.GetMember(null, panel, true, "AnyBlastdoorClosed");
            sb.Append("\n[Verify] AnyBlastdoorClosed = ")
              .Append(v == null ? "unknown" : v.ToString()).Append("\n");
            sb.Append("         (False = blast doors open / wall gone)\n");

            return sb.ToString();
        }

        /// <summary>
        /// 电梯深度扫描。
        /// 上一份报告只扫到 IsOperative / IsLocked / Type，不足以确定解锁通道，
        /// 这里把 wrapper 与底层对象的属性(setter有无)、字段、锁定相关方法全部列出。
        /// </summary>
        public static string ProbeLifts()
        {
            StringBuilder sb = new StringBuilder();

            Type liftType = Rf.FindType("Exiled.API.Features.Lift");
            if (liftType == null) return "Lift type not found.";

            sb.Append("Lift type: ").Append(liftType.FullName).Append("\n");

            object raw = Rf.GetMember(null, liftType, true, "List", "Lifts");
            IEnumerable lifts = raw as IEnumerable;
            if (lifts == null) return "Lift.List not accessible.";

            List<object> list = new List<object>();
            foreach (object o in lifts) if (o != null) list.Add(o);
            sb.Append("Instances: ").Append(list.Count).Append("\n\n");

            if (list.Count == 0) return sb.ToString();

            object first = list[0];
            Type ft = first.GetType();

            sb.Append("--- EXILED wrapper: ").Append(ft.FullName).Append(" ---\n");
            sb.Append("[properties]\n");
            foreach (PropertyInfo p in ft.GetProperties(Rf.All & ~BindingFlags.Static))
                sb.Append("  ").Append(p.PropertyType.Name).Append(" ").Append(p.Name)
                  .Append("  get=").Append(p.CanRead ? "Y" : "N")
                  .Append(" set=").Append(p.CanWrite ? "Y" : "N").Append("\n");

            sb.Append("\n[methods matching Lock/Unlock/Set]\n");
            int mm = 0;
            foreach (MethodInfo m in ft.GetMethods(Rf.All & ~BindingFlags.Static))
            {
                if (m.IsSpecialName) continue;
                string mn = m.Name;
                if (mn.IndexOf("Lock", StringComparison.OrdinalIgnoreCase) < 0
                 && mn.IndexOf("Unlock", StringComparison.OrdinalIgnoreCase) < 0
                 && mn.IndexOf("Set", StringComparison.OrdinalIgnoreCase) < 0) continue;
                mm++;
                sb.Append("  ").Append(m.ReturnType.Name).Append(" ").Append(m.Name).Append("(");
                ParameterInfo[] ps = m.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(ps[i].ParameterType.Name);
                }
                sb.Append(")\n");
            }
            if (mm == 0) sb.Append("  (none)\n");

            sb.Append("\n[fields]\n");
            foreach (FieldInfo f in ft.GetFields(Rf.All & ~BindingFlags.Static))
                sb.Append("  ").Append(f.FieldType.Name).Append(" ").Append(f.Name).Append("\n");

            // 底层对象
            sb.Append("\n--- underlying object (Base) ---\n");
            object baseObj = null;
            string via = "";
            foreach (string hop in new[] { "Base", "_base", "Elevator", "Chamber", "Target" })
            {
                baseObj = Rf.GetMember(first, ft, false, hop);
                if (baseObj != null) { via = hop; break; }
            }
            if (baseObj == null)
            {
                sb.Append("  (no Base-like member found)\n");
            }
            else
            {
                Type bt = baseObj.GetType();
                sb.Append("  via '").Append(via).Append("' -> ").Append(bt.FullName).Append("\n");

                sb.Append("  [bool fields]\n");
                foreach (FieldInfo f in bt.GetFields(Rf.All & ~BindingFlags.Static))
                    if (f.FieldType == typeof(bool))
                        sb.Append("    ").Append(f.Name).Append("\n");

                sb.Append("  [properties]\n");
                foreach (PropertyInfo p in bt.GetProperties(Rf.All & ~BindingFlags.Static))
                    sb.Append("    ").Append(p.PropertyType.Name).Append(" ").Append(p.Name)
                      .Append("  get=").Append(p.CanRead ? "Y" : "N")
                      .Append(" set=").Append(p.CanWrite ? "Y" : "N").Append("\n");

                sb.Append("  [methods matching Lock/Set]\n");
                int bm = 0;
                foreach (MethodInfo m in bt.GetMethods(Rf.All & ~BindingFlags.Static))
                {
                    if (m.IsSpecialName) continue;
                    string mn = m.Name;
                    if (mn.IndexOf("Lock", StringComparison.OrdinalIgnoreCase) < 0
                     && mn.IndexOf("Set", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bm++;
                    sb.Append("    ").Append(m.ReturnType.Name).Append(" ").Append(m.Name).Append("(");
                    ParameterInfo[] ps = m.GetParameters();
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(ps[i].ParameterType.Name);
                    }
                    sb.Append(")\n");
                }
                if (bm == 0) sb.Append("    (none)\n");
            }

            // LabAPI Lift（实测 EXILED Door.List 不可访问，LabAPI 可用 -> 电梯优先走这里）
            sb.Append("\n--- LabAPI Lift wrapper ---\n");
            Type labLift = Rf.FindType("LabApi.Features.Wrappers.Lift");
            if (labLift == null)
            {
                sb.Append("  LabApi.Features.Wrappers.Lift NOT FOUND\n");
            }
            else
            {
                sb.Append("  ").Append(labLift.FullName).Append("\n");
                object labRaw = Rf.GetMember(null, labLift, true, "List", "Lifts", "Dictionary", "All");
                List<object> labList = new List<object>();
                if (labRaw is IDictionary ld)
                {
                    foreach (object vd in ld.Values) if (vd != null) labList.Add(vd);
                }
                else if (labRaw is IEnumerable le && !(labRaw is string))
                {
                    foreach (object ve in le) if (ve != null) labList.Add(ve);
                }
                sb.Append("  instances: ").Append(labList.Count).Append("\n");

                if (labList.Count > 0)
                {
                    object l0 = labList[0];
                    Type lt0 = l0.GetType();
                    sb.Append("  [bool props]\n");
                    foreach (PropertyInfo p in lt0.GetProperties(Rf.All & ~BindingFlags.Static))
                        if (p.PropertyType == typeof(bool))
                            sb.Append("    ").Append(p.Name)
                              .Append("  get=").Append(p.CanRead ? "Y" : "N")
                              .Append(" set=").Append(p.CanWrite ? "Y" : "N").Append("\n");
                    sb.Append("  [lock-ish methods]\n");
                    int lm = 0;
                    foreach (MethodInfo m in lt0.GetMethods(Rf.All & ~BindingFlags.Static))
                    {
                        if (m.IsSpecialName) continue;
                        string mn = m.Name;
                        if (mn.IndexOf("Lock", StringComparison.OrdinalIgnoreCase) < 0
                         && mn.IndexOf("Unlock", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        lm++;
                        sb.Append("    ").Append(m.ReturnType.Name).Append(" ").Append(m.Name).Append("(");
                        ParameterInfo[] ps = m.GetParameters();
                        for (int i = 0; i < ps.Length; i++)
                        {
                            if (i > 0) sb.Append(", ");
                            sb.Append(ps[i].ParameterType.Name);
                        }
                        sb.Append(")\n");
                    }
                    if (lm == 0) sb.Append("    (none)\n");

                    // 底层对象
                    object lb = null; string viaLab = "";
                    foreach (string hop in new[] { "Base", "_base", "Elevator", "Chamber", "Target" })
                    {
                        lb = Rf.GetMember(l0, lt0, false, hop);
                        if (lb != null) { viaLab = hop; break; }
                    }
                    if (lb == null) sb.Append("  (no Base-like member)\n");
                    else
                    {
                        Type bt = lb.GetType();
                        sb.Append("  Base via '").Append(viaLab).Append("' -> ").Append(bt.FullName).Append("\n");
                        sb.Append("    [bool props]\n");
                        foreach (PropertyInfo p in bt.GetProperties(Rf.All & ~BindingFlags.Static))
                            if (p.PropertyType == typeof(bool))
                                sb.Append("      ").Append(p.Name)
                                  .Append("  get=").Append(p.CanRead ? "Y" : "N")
                                  .Append(" set=").Append(p.CanWrite ? "Y" : "N").Append("\n");
                        sb.Append("    [bool fields]\n");
                        foreach (FieldInfo f in bt.GetFields(Rf.All & ~BindingFlags.Static))
                            if (f.FieldType == typeof(bool)) sb.Append("      ").Append(f.Name).Append("\n");
                        sb.Append("    [lock-ish methods]\n");
                        int bm = 0;
                        foreach (MethodInfo m in bt.GetMethods(Rf.All & ~BindingFlags.Static))
                        {
                            if (m.IsSpecialName) continue;
                            string mn = m.Name;
                            if (mn.IndexOf("Lock", StringComparison.OrdinalIgnoreCase) < 0
                             && mn.IndexOf("Unlock", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            bm++;
                            sb.Append("      ").Append(m.ReturnType.Name).Append(" ").Append(m.Name).Append("(");
                            ParameterInfo[] ps = m.GetParameters();
                            for (int i = 0; i < ps.Length; i++)
                            {
                                if (i > 0) sb.Append(", ");
                                sb.Append(ps[i].ParameterType.Name);
                            }
                            sb.Append(")\n");
                        }
                        if (bm == 0) sb.Append("      (none)\n");
                    }

                    sb.Append("  [state first 5]\n");
                    int ln = 0;
                    foreach (object o in labList)
                    {
                        if (ln++ >= 5) break;
                        Type ot = o.GetType();
                        object lk = Rf.GetMember(o, ot, false, "IsLocked", "Network_locked", "Locked");
                        object op = Rf.GetMember(o, ot, false, "IsOperative", "operative");
                        sb.Append("    IsLocked=").Append(lk).Append(" IsOperative=").Append(op).Append("\n");
                    }
                }
            }

            // 当前状态
            sb.Append("\n--- current state (first 5) ---\n");
            int n = 0;
            foreach (object o in list)
            {
                if (n++ >= 5) break;
                Type ot = o.GetType();
                object ty = Rf.GetMember(o, ot, false, "Type");
                object lk = Rf.GetMember(o, ot, false, "IsLocked");
                object op = Rf.GetMember(o, ot, false, "IsOperative");
                sb.Append("  [").Append(ty).Append("] IsLocked=").Append(lk)
                  .Append(" IsOperative=").Append(op).Append("\n");
            }

            return sb.ToString();
        }
    }
}
