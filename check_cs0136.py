#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
CS0136 变量遮蔽检查器（已用阴/阳性对照验证有效）

C# 规则：嵌套作用域内不能声明与外层作用域同名的局部变量，
         即使外层变量在源码中声明位置更靠后，也会报 CS0136。
         典型触发：方法里既有 `catch (Exception ex)`，嵌套块里又写 `object ex = ...`。

用法：
    python check_cs0136.py <目录或 .cs 文件> [...]
退出码：0 = 无风险，1 = 发现风险
"""
import re
import sys
import glob
import os


def strip_noise(code):
    """去掉注释与字符串，避免其中的括号/变量名干扰统计"""
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


TYPES = (r'object|int|string|bool|Type|MethodInfo|FieldInfo|PropertyInfo|'
         r'List<[^>]*>|StringBuilder|IEnumerable|ParameterInfo\[\]|Array|'
         r'ushort|float|double|var|Exception')

DECL = re.compile(r'\b(?:' + TYPES + r')\s+([A-Za-z_]\w*)\s*[=;)]')
CATCH = re.compile(r'catch\s*\(\s*Exception\s+(\w+)\s*\)')
SIG = re.compile(r'^\s*(?:public|private|internal|protected)[^=;]*\)\s*$')


def analyze(lines, base_ln, label, verbose=True):
    """按字符顺序处理：catch 变量归属其后紧跟的 { 块"""
    stack = [()]
    nid = [0]
    decls = []
    pending_catch = []

    for i, line in enumerate(lines):
        ln = base_ln + i
        tokens = []
        for m in CATCH.finditer(line):
            tokens.append((m.start(), 'catch', m.group(1)))
        for idx, ch in enumerate(line):
            if ch == '{':
                tokens.append((idx, 'open', None))
            elif ch == '}':
                tokens.append((idx, 'close', None))
        tokens.sort(key=lambda t: t[0])

        plain = CATCH.sub('catch', line)
        for m in DECL.finditer(plain):
            decls.append((m.group(1), stack[-1], ln))

        for _, kind, name in tokens:
            if kind == 'open':
                nid[0] += 1
                stack.append(stack[-1] + (nid[0],))
                for cn in pending_catch:
                    decls.append((cn, stack[-1], ln))
                pending_catch = []
            elif kind == 'close':
                if len(stack) > 1:
                    stack.pop()
            else:
                pending_catch.append(name)

    bad = 0
    for i in range(len(decls)):
        for j in range(i + 1, len(decls)):
            n1, p1, l1 = decls[i]
            n2, p2, l2 = decls[j]
            if n1 != n2 or p1 == p2:
                continue
            if p2[:len(p1)] == p1:
                if verbose:
                    print(f"  [CS0136] {label}:{l2} 变量 '{n1}' 与外层 {l1} 冲突")
                bad += 1
            elif p1[:len(p2)] == p2:
                if verbose:
                    print(f"  [CS0136] {label}:{l1} 变量 '{n1}' 与外层 {l2} 冲突")
                bad += 1
    return bad


def scan_file(path, verbose=True):
    lines = strip_noise(open(path, encoding="utf-8").read()).split('\n')
    total = 0
    i = 0
    while i < len(lines):
        if SIG.match(lines[i]):
            j = i + 1
            while j < len(lines) and lines[j].strip() == '':
                j += 1
            if j < len(lines) and lines[j].strip() == '{':
                depth = 0
                k = j
                while k < len(lines):
                    depth += lines[k].count('{') - lines[k].count('}')
                    if depth == 0:
                        break
                    k += 1
                total += analyze(lines[j:k + 1], j + 1, path, verbose)
                i = k + 1
                continue
        i += 1
    return total


def self_test():
    pos = ["public static string M()", "{", " foreach (var o in inst)", " {",
           "  object v = fOpen.GetValue(o);", " }",
           " object v = panel == null ? null : GetMember();", " return v;", "}"]
    neg = ["public static string M()", "{", " foreach (var o in inst)", " {",
           "  object val = fOpen.GetValue(o);", " }",
           " object v = panel == null ? null : GetMember();", " return v;", "}"]
    return analyze(pos, 1, 'pos', False) == 1 and analyze(neg, 1, 'neg', False) == 0


def main():
    if not self_test():
        print("[FATAL] 自检未通过，扫描器不可用")
        return 2

    targets = sys.argv[1:] or ['.']
    files = []
    for t in targets:
        if os.path.isdir(t):
            files.extend(sorted(glob.glob(os.path.join(t, '**', '*.cs'), recursive=True)))
        else:
            files.append(t)
    files = [f for f in files if '/obj/' not in f and '/bin/' not in f]

    total = 0
    for f in files:
        total += scan_file(f)

    print(f"\n扫描 {len(files)} 个文件，发现 {total} 处 CS0136 风险")
    return 1 if total else 0


if __name__ == '__main__':
    sys.exit(main())
