using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace FacilityTools
{
    /// <summary>
    /// 安全反射访问器。
    /// 直接 Type.GetProperty/GetField 在「派生类用 new 隐藏基类同名成员」时
    /// 会抛 AmbiguousMatchException；这里捕获后沿继承链自派生向基类找最派生的那个。
    /// </summary>
    internal static class Safe
    {
        public static PropertyInfo GetProperty(Type t, string name, BindingFlags flags)
        {
            if (t == null || string.IsNullOrEmpty(name)) return null;
            try { return t.GetProperty(name, flags); }
            catch (AmbiguousMatchException) { }

            Type cur = t;
            int guard = 0;
            while (cur != null && guard++ < 16)
            {
                foreach (PropertyInfo p in cur.GetProperties(flags | BindingFlags.DeclaredOnly))
                    if (p.Name == name) return p;
                cur = cur.BaseType;
            }
            return null;
        }

        public static FieldInfo GetField(Type t, string name, BindingFlags flags)
        {
            if (t == null || string.IsNullOrEmpty(name)) return null;
            try { return t.GetField(name, flags); }
            catch (AmbiguousMatchException) { }

            Type cur = t;
            int guard = 0;
            while (cur != null && guard++ < 16)
            {
                foreach (FieldInfo f in cur.GetFields(flags | BindingFlags.DeclaredOnly))
                    if (f.Name == name) return f;
                cur = cur.BaseType;
            }
            return null;
        }
    }

    /// <summary>
    /// 共享反射工具集。探测(Probe)与恢复(Restore)共用，
    /// 避免两份实现各自漂移。
    /// </summary>
    internal static class Rf
    {
        public const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;

        // ================= 类型查找 =================

        /// <summary>按类型名查找（三级：全名 / Assembly-CSharp 限定 / 简单名全程序集扫描）</summary>
        public static Type FindType(params string[] names)
        {
            foreach (string n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;

                Type t = null;
                try { t = Type.GetType(n); } catch { }
                if (t != null) return t;

                try { t = Type.GetType(n + ", Assembly-CSharp"); } catch { }
                if (t != null) return t;

                try
                {
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            t = asm.GetType(n);
                            if (t != null) return t;
                        }
                        catch { }
                    }
                }
                catch { }

                // 兜底：全程序集按简单名扫描
                // BlastDoor 是全局命名空间（无 namespace），这步是关键
                t = FindBySimpleName(n);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>
        /// 遍历所有已加载程序集，按类型的简单名（Name）匹配。
        /// 传入 "Ns.TypeName" 或 "TypeName" 都能命中——真实命名空间未知时唯一可靠方式。
        /// </summary>
        public static Type FindBySimpleName(string nameOrFullName)
        {
            if (string.IsNullOrEmpty(nameOrFullName)) return null;

            string shortName = nameOrFullName;
            int dot = nameOrFullName.LastIndexOf('.');
            if (dot >= 0 && dot < nameOrFullName.Length - 1)
                shortName = nameOrFullName.Substring(dot + 1);

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == null) continue;

                Type[] types = null;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }

                if (types == null) continue;
                foreach (Type t in types)
                {
                    if (t == null) continue;
                    try { if (t.Name == shortName) return t; }
                    catch { }
                }
            }
            return null;
        }

        // ================= 成员读取 =================

        public static object GetMember(object obj, Type owner, bool isStatic, params string[] names)
        {
            if (owner == null) return null;
            BindingFlags flags = isStatic ? (All & ~BindingFlags.Instance) : All;

            foreach (string n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;

                PropertyInfo p = Safe.GetProperty(owner, n, flags);
                if (p != null)
                {
                    try { return p.GetValue(isStatic ? null : obj); } catch { }
                }
                FieldInfo f = Safe.GetField(owner, n, flags);
                if (f != null)
                {
                    try { return f.GetValue(isStatic ? null : obj); } catch { }
                }
            }
            return null;
        }

        public static object CallStatic(Type t, string name, params object[] args)
        {
            if (t == null) return null;
            foreach (MethodInfo m in t.GetMethods(All & ~BindingFlags.Instance))
            {
                if (m.Name != name) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != args.Length) continue;
                try { return m.Invoke(null, args); } catch { }
            }
            return null;
        }

        public static MethodInfo GetMethod(Type t, string name, bool isStatic, int paramCount = -1)
        {
            if (t == null) return null;
            BindingFlags flags = isStatic ? (All & ~BindingFlags.Instance) : All;
            foreach (MethodInfo m in t.GetMethods(flags))
            {
                if (m.Name != name) continue;
                if (paramCount >= 0 && m.GetParameters().Length != paramCount) continue;
                return m;
            }
            return null;
        }

        // ================= 实例发现 =================

        /// <summary>取某类型的所有实例（静态集合优先，Unity FindObjectsOfType 兜底）</summary>
        public static List<object> GetInstances(Type t, out string how)
        {
            List<object> result = new List<object>();
            how = "none";
            if (t == null) return result;

            string[] names = {
                "Instances", "List", "All", "Dictionary", "Dict", "Cached",
                "Collection", "Values", "BulkheadDoors", "Doors", "Registered",
                "Spawned", "Active", "Enumerable", "Lifts"
            };

            foreach (string name in names)
            {
                object raw = GetMember(null, t, true, name);
                if (raw == null) continue;

                if (raw is IDictionary dict)
                {
                    foreach (object v in dict.Values)
                        if (v != null && t.IsInstanceOfType(v)) result.Add(v);
                    if (result.Count > 0) { how = "static." + name + " (dictionary values)"; return result; }
                }
                else if (raw is IEnumerable en && !(raw is string))
                {
                    foreach (object v in en)
                        if (v != null && t.IsInstanceOfType(v)) result.Add(v);
                    if (result.Count > 0) { how = "static." + name + " (enumerable)"; return result; }
                }
            }

            List<object> found = FindObjectsOfTypeViaUnity(t);
            if (found.Count > 0) { how = "UnityEngine FindObjectsOfType"; return found; }

            return result;
        }

        public static List<object> FindObjectsOfTypeViaUnity(Type t)
        {
            List<object> list = new List<object>();
            if (t == null) return list;
            try
            {
                Type uobj = FindType("UnityEngine.Object",
                                     "UnityEngine.Object, UnityEngine.CoreModule");
                if (uobj == null) return list;

                MethodInfo mi = null;
                foreach (MethodInfo m in uobj.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "FindObjectsOfType") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Type)) { mi = m; break; }
                }
                if (mi == null) return list;

                Array arr = (Array)mi.Invoke(null, new object[] { t });
                if (arr == null) return list;
                foreach (object o in arr) if (o != null) list.Add(o);
            }
            catch { }
            return list;
        }

        /// <summary>把静态成员（集合或字典）摊平成对象列表，不做类型过滤</summary>
        public static List<object> Enumerate(object raw)
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

        // ================= 小工具 =================

        public static string Inner(Exception ex)
        {
            Exception e = ex.InnerException ?? ex;
            return e.GetType().Name + ": " + e.Message;
        }

        public static string NameOf(object o)
        {
            return o == null ? "null" : o.GetType().Name;
        }

        public static bool IsNumeric(Type t)
        {
            return t == typeof(ushort) || t == typeof(short)
                || t == typeof(int) || t == typeof(uint)
                || t == typeof(long) || t == typeof(ulong)
                || t == typeof(float) || t == typeof(double);
        }
    }
}
