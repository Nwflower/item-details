using System;
using System.Collections.Generic;
using System.Diagnostics;
using ItemDetails;

// 极限压测：1024 个容器 × 每箱 2500 件物品。
// 直接编译发布版里的 ContentsRollup / ContentsRefresh 纯逻辑，
// 对比「旧实现每帧全量比对」与「新实现按 0.2 秒刷新一次快照」的实际工作量。

const int ContainerCount = 1024;
const int ItemsPerContainer = 2500;
const int MaxCells = 15;
const int Frames = 600;              // 10 秒 @ 60 fps
const float FrameSeconds = 1f / 60f;

int failures = 0;

string[] names = new string[64];
for (int i = 0; i < names.Length; i++) names[i] = "item_" + i;

Console.WriteLine($"语料：{ContainerCount} 个容器 × {ItemsPerContainer} 件 = {(long)ContainerCount * ItemsPerContainer:N0} 条事实");

// ---------- 语料 ----------
var corpus = new ContentsFact[ContainerCount][];
var buildWatch = Stopwatch.StartNew();
for (int c = 0; c < ContainerCount; c++)
{
    var facts = new ContentsFact[ItemsPerContainer];
    for (int i = 0; i < ItemsPerContainer; i++)
    {
        // 每个容器名字分布略有偏移，堆叠数 1/1/3 混合，覆盖 64 种名字
        string name = names[(c * 7 + i) % names.Length];
        int stack = (i % 5 == 0) ? 3 : 1;
        facts[i] = new ContentsFact(name, c * 1_000_000 + i, stack);
    }
    corpus[c] = facts;
}
buildWatch.Stop();
Console.WriteLine($"语料生成：{buildWatch.Elapsed.TotalMilliseconds:0.0} ms");

// ---------- 1) 冷启动：1024 个容器各悬停一次（扫描 + 归组 + 签名比对） ----------
var cold = new ContentsRollup[ContainerCount];
var sw = Stopwatch.StartNew();
for (int c = 0; c < ContainerCount; c++)
{
    cold[c] = ContentsRollup.Build(corpus[c], MaxCells);
    if (!ContentsSignature.Matches(corpus[c], corpus[c]))
        throw new Exception($"container {c}: 相同事实的签名比对失败");
}
sw.Stop();
double coldMs = sw.Elapsed.TotalMilliseconds;
Console.WriteLine($"冷启动 1024 次悬停：{coldMs:0.0} ms，均次 {coldMs / ContainerCount:0.000} ms");

Check("冷启动 1024 次悬停 < 5000 ms", coldMs < 5000.0);

// ---------- 1b) 标签页：5 页 × 500 件拼成一个容器 ----------
var tabbed = new List<ContentsFact>(ItemsPerContainer);
for (int t = 0; t < 5; t++)
    tabbed.AddRange(new ArraySegment<ContentsFact>(corpus[0], t * 500, 500));
var tabRollup = ContentsRollup.Build(tabbed, MaxCells);
Check("5 个标签页拼接后件数一致", tabRollup.Total == cold[0].Total);
Check("5 个标签页拼接后组数一致", tabRollup.Groups.Count == cold[0].Groups.Count);

// ---------- 2) 稳态：在同一容器上连续悬停 10 秒（600 帧） ----------
float lastScan = -1f;
ContentsRollup? current = null;
int scans = 0;
var frameTicks = new long[Frames];
long totalTicks = 0;
for (int f = 0; f < Frames; f++)
{
    long t0 = Stopwatch.GetTimestamp();
    float now = f * FrameSeconds;
    if (ContentsRefresh.Due(now, lastScan))
    {
        lastScan = now;
        scans++;
        current = ContentsRollup.Build(corpus[0], MaxCells);
    }
    // 生产代码在未到刷新时只做一次引用比较；这里读一下字段模拟它
    if (current == null) throw new Exception("first frame must scan");
    long dt = Stopwatch.GetTimestamp() - t0;
    frameTicks[f] = dt;
    totalTicks += dt;
}
Array.Sort(frameTicks);
double medianMs = TicksToMs(frameTicks[Frames / 2]);
double avgMs = TicksToMs((double)totalTicks / Frames);
double maxMs = TicksToMs(frameTicks[Frames - 1]);
double newTotalMs = TicksToMs(totalTicks);

Console.WriteLine($"稳态 600 帧：扫描 {scans} 次，帧均 {avgMs:0.0000} ms，中位 {medianMs:0.0000} ms，峰值 {maxMs:0.000} ms");

Check("稳态扫描次数在 45–60 次之间（0.2 秒节流）", scans >= 45 && scans <= 60);
Check("稳态帧中位 < 0.005 ms", medianMs < 0.005);

// ---------- 3) 旧实现模型：每帧复制 childItems + 两次全量签名比对 ----------
var oldWatch = Stopwatch.StartNew();
for (int f = 0; f < Frames; f++)
{
    var copy = new ContentsFact[ItemsPerContainer];
    Array.Copy(corpus[0], copy, ItemsPerContainer);      // GameGridInventory.childItems 的副本
    if (!ContentsSignature.Matches(corpus[0], copy)) throw new Exception("旧模型比对失败");
    if (!ContentsSignature.Matches(corpus[0], copy)) throw new Exception("旧模型比对失败");
}
oldWatch.Stop();
double oldTotalMs = oldWatch.Elapsed.TotalMilliseconds;
Console.WriteLine($"旧实现模型 600 帧：{oldTotalMs:0.0} ms，帧均 {oldTotalMs / Frames:0.0000} ms");
Console.WriteLine($"每帧工作量下降：{oldTotalMs / Math.Max(0.0001, newTotalMs):0.0}×");

Check("新实现 10 秒总耗时至少比旧模型少 5×", oldTotalMs > newTotalMs * 5.0);

// ---------- 4) 归组正确性 ----------
Check("总数与堆叠数一致", cold[0].Total == ExpectedTotal(corpus[0]));
Check("组数与名字数一致", cold[0].Groups.Count == names.Length);
Check("超过 15 组时溢出一格且只画 14 格", OverflowCase());

Check("内容签名能发现同数量替换", Replaced());
Check("内容签名能发现堆叠变化", Restacked());
Check("刷新节流：0.2 秒内不重复扫描", !ContentsRefresh.Due(0.199f, 0f));
Check("刷新节流：到 0.2 秒重新扫描", ContentsRefresh.Due(0.2f, 0f));

Console.WriteLine(failures == 0 ? "RESULT PASS" : $"RESULT FAIL ({failures})");
return failures == 0 ? 0 : 1;

// ---------- helpers ----------

static double TicksToMs(double ticks) => ticks * 1000.0 / Stopwatch.Frequency;

static long ExpectedTotal(ContentsFact[] facts)
{
    long n = 0;
    foreach (var f in facts) n += f.Stack;
    return n;
}

bool OverflowCase()
{
    var facts = new ContentsFact[20];
    for (int i = 0; i < facts.Length; i++) facts[i] = new ContentsFact("g" + i, i, 2);
    var rollup = ContentsRollup.Build(facts, MaxCells);
    if (rollup.Groups.Count != 20) return false;
    if (rollup.ShownCount != MaxCells - 1) return false;
    if (rollup.Overflow != (20 - (MaxCells - 1)) * 2) return false;
    if (rollup.Total != 40) return false;
    return true;
}

bool Replaced()
{
    var a = new[] { new ContentsFact("a", 1, 1), new ContentsFact("b", 2, 1) };
    var b = new[] { new ContentsFact("a", 1, 1), new ContentsFact("c", 3, 1) };
    return !ContentsSignature.Matches(a, b) && ContentsSignature.Matches(a, a);
}

bool Restacked()
{
    var a = new[] { new ContentsFact("a", 1, 1) };
    var b = new[] { new ContentsFact("a", 1, 2) };
    return !ContentsSignature.Matches(a, b);
}

void Check(string name, bool ok)
{
    if (ok)
    {
        Console.WriteLine("PASS " + name);
        return;
    }
    failures++;
    Console.Error.WriteLine("FAIL " + name);
}
