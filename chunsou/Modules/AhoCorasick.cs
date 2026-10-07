// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：（移植版新增，用于替代逐条 substring 扫描）
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
// ─────────────────────────────────────────────────────────────

namespace Chunsou;

/// <summary>
/// Aho-Corasick 多模式串自动机：一次扫描文本同时匹配上千个关键词。
/// 用于把"逐条关键词 contains 全页搜索"从 O(规则数 × 页面大小) 降到 O(页面大小)。
/// </summary>
public sealed class AhoCorasick
{
    private sealed class Node
    {
        public Dictionary<char, int> Next = [];
        public int Fail;
        /// <summary>该节点上结束的关键词 ID 列表</summary>
        public List<int>? Outputs;
    }

    private readonly List<Node> _nodes = [new()];
    private readonly int _patternCount;

    public AhoCorasick(IEnumerable<string> patterns)
    {
        var id = 0;
        foreach (var p in patterns)
        {
            if (string.IsNullOrEmpty(p)) continue;
            AddPattern(p, id++);
        }
        _patternCount = id;
        Build();
    }

    public int PatternCount => _patternCount;

    private void AddPattern(string pattern, int id)
    {
        var cur = 0;
        foreach (var c in pattern)
        {
            if (!_nodes[cur].Next.TryGetValue(c, out var next))
            {
                next = _nodes.Count;
                _nodes.Add(new Node());
                _nodes[cur].Next[c] = next;
            }
            cur = next;
        }

        (_nodes[cur].Outputs ??= []).Add(id);
    }

    private void Build()
    {
        var queue = new Queue<int>();

        foreach (var child in _nodes[0].Next.Values)
        {
            _nodes[child].Fail = 0;
            queue.Enqueue(child);
        }

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();

            foreach (var (c, child) in _nodes[cur].Next)
            {
                queue.Enqueue(child);

                var fail = _nodes[cur].Fail;
                while (fail != 0 && !_nodes[fail].Next.ContainsKey(c))
                    fail = _nodes[fail].Fail;

                _nodes[child].Fail = _nodes[fail].Next.TryGetValue(c, out var f) ? f : 0;

                // 合并 fail 链上的输出，保证长模式的后缀短模式也能被检出
                var failOut = _nodes[_nodes[child].Fail].Outputs;
                if (failOut is not null)
                {
                    (_nodes[child].Outputs ??= []).AddRange(failOut);
                }
            }
        }
    }

    /// <summary>
    /// 在 text 中查找所有命中的关键词 ID，写入 hits（去重）。
    /// foundIds 用于把 ID 映射回规则。
    /// </summary>
    public void Scan(string text, HashSet<int> hits)
    {
        hits.Clear();
        if (string.IsNullOrEmpty(text)) return;

        var cur = 0;
        foreach (var c in text)
        {
            while (cur != 0 && !_nodes[cur].Next.ContainsKey(c))
                cur = _nodes[cur].Fail;

            if (_nodes[cur].Next.TryGetValue(c, out var next))
                cur = next;

            var outputs = _nodes[cur].Outputs;
            if (outputs is null) continue;

            // 直接写入 hits（HashSet 自身去重）
            foreach (var id in outputs)
                hits.Add(id);
        }
    }
}
