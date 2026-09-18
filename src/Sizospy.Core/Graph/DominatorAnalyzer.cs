namespace Sizospy.Graph;

/// <summary>Computes immediate dominators and retained-size estimates for a dependency graph.</summary>
public static class DominatorAnalyzer
{
    /// <summary>
    /// Analyzes directed source-depends-on-target edges using a synthetic super-root and
    /// Lengauer-Tarjan immediate dominators.
    /// </summary>
    /// <param name="nodes">Nodes whose self sizes are non-overlapping physical weights.</param>
    /// <param name="edges">Directed dependency edges from depender to dependency.</param>
    /// <param name="cancellationToken">Token used to cancel graph traversal.</param>
    /// <returns>One dominator metric for each input node, keyed by node identifier.</returns>
    public static IReadOnlyDictionary<long, DominatorMetric> Analyze(
        IReadOnlyList<SizeNode> nodes,
        IReadOnlyList<DependencyEdge> edges,
        CancellationToken cancellationToken = default)
    {
        if (nodes.Count == 0)
        {
            return new Dictionary<long, DominatorMetric>();
        }

        var ids = nodes.Select(n => n.Id).ToArray();
        var indexById = ids.Select((id, index) => (id, index: index + 1))
            .ToDictionary(x => x.id, x => x.index);
        var count = nodes.Count + 1;
        var successors = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        var predecessors = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();

        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!indexById.TryGetValue(edge.SourceNodeId, out var source) ||
                !indexById.TryGetValue(edge.TargetNodeId, out var target))
            {
                continue;
            }

            successors[source].Add(target);
            predecessors[target].Add(source);
        }

        var roots = nodes.Where(n => n.IsRoot).Select(n => indexById[n.Id]).ToList();
        if (roots.Count == 0)
        {
            roots.AddRange(Enumerable.Range(1, nodes.Count).Where(i => predecessors[i].Count == 0));
        }

        if (roots.Count == 0)
        {
            roots.Add(1);
        }

        foreach (var root in roots.Distinct())
        {
            successors[0].Add(root);
            predecessors[root].Add(0);
        }

        var reachable = MarkReachable(successors, cancellationToken);
        foreach (var nodeIndex in Enumerable.Range(1, nodes.Count).Where(i => !reachable[i]))
        {
            successors[0].Add(nodeIndex);
            predecessors[nodeIndex].Add(0);
        }

        var idom = ComputeImmediateDominators(successors, predecessors, cancellationToken);
        var children = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        for (var i = 1; i < count; i++)
        {
            if (idom[i] >= 0)
            {
                children[idom[i]].Add(i);
            }
        }

        var retained = new long[count];
        var dominated = new int[count];
        var distances = Enumerable.Repeat(-1, count).ToArray();
        distances[0] = -1;
        var stack = new Stack<(int Node, bool Exit)>();
        stack.Push((0, false));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, exit) = stack.Pop();
            if (!exit)
            {
                stack.Push((current, true));
                foreach (var child in children[current])
                {
                    distances[child] = distances[current] + 1;
                    stack.Push((child, false));
                }
            }
            else
            {
                retained[current] += current == 0 ? 0 : nodes[current - 1].SelfSize;
                foreach (var child in children[current])
                {
                    retained[current] += retained[child];
                    dominated[current] += dominated[child] + 1;
                }
            }
        }

        var result = new Dictionary<long, DominatorMetric>(nodes.Count);
        for (var i = 1; i < count; i++)
        {
            var self = nodes[i - 1].SelfSize;
            result[nodes[i - 1].Id] = new DominatorMetric(
                nodes[i - 1].Id,
                idom[i] <= 0 ? null : nodes[idom[i] - 1].Id,
                retained[i],
                retained[i] - self,
                self > 0 ? (double)retained[i] / self : null,
                dominated[i],
                distances[i],
                reachable[i]);
        }

        return result;
    }

    private static bool[] MarkReachable(IReadOnlyList<List<int>> successors, CancellationToken cancellationToken)
    {
        var reachable = new bool[successors.Count];
        var stack = new Stack<int>();
        stack.Push(0);
        reachable[0] = true;
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            foreach (var target in successors[current])
            {
                if (!reachable[target])
                {
                    reachable[target] = true;
                    stack.Push(target);
                }
            }
        }

        return reachable;
    }

    private static int[] ComputeImmediateDominators(
        IReadOnlyList<List<int>> successors,
        IReadOnlyList<List<int>> predecessors,
        CancellationToken cancellationToken)
    {
        var capacity = successors.Count;
        var semi = new int[capacity];
        var parent = Enumerable.Repeat(-1, capacity).ToArray();
        var ancestor = Enumerable.Repeat(-1, capacity).ToArray();
        var label = new int[capacity];
        var idom = Enumerable.Repeat(-1, capacity).ToArray();
        var dfsNumber = Enumerable.Repeat(-1, capacity).ToArray();
        var vertex = new int[capacity];
        var buckets = Enumerable.Range(0, capacity).Select(_ => new List<int>()).ToArray();
        var time = 0;

        var frames = new Stack<(int Node, int NextChild)>();
        dfsNumber[0] = time;
        vertex[time] = 0;
        semi[0] = time;
        label[0] = 0;
        time++;
        frames.Push((0, 0));

        while (frames.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (node, nextChild) = frames.Pop();
            if (nextChild >= successors[node].Count)
            {
                continue;
            }

            frames.Push((node, nextChild + 1));
            var child = successors[node][nextChild];
            if (dfsNumber[child] >= 0)
            {
                continue;
            }

            parent[child] = node;
            dfsNumber[child] = time;
            vertex[time] = child;
            semi[child] = time;
            label[child] = child;
            time++;
            frames.Push((child, 0));
        }

        for (var i = time - 1; i >= 1; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var w = vertex[i];
            foreach (var v in predecessors[w])
            {
                if (dfsNumber[v] < 0)
                {
                    continue;
                }

                var u = Evaluate(v, ancestor, label, semi);
                semi[w] = Math.Min(semi[w], semi[u]);
            }

            buckets[vertex[semi[w]]].Add(w);
            Link(parent[w], w, ancestor);
            foreach (var v in buckets[parent[w]])
            {
                var u = Evaluate(v, ancestor, label, semi);
                idom[v] = semi[u] < semi[v] ? u : parent[w];
            }

            buckets[parent[w]].Clear();
        }

        for (var i = 1; i < time; i++)
        {
            var w = vertex[i];
            if (idom[w] != vertex[semi[w]])
            {
                idom[w] = idom[idom[w]];
            }
        }

        idom[0] = -1;
        return idom;
    }

    private static void Link(int parent, int child, int[] ancestor) => ancestor[child] = parent;

    private static int Evaluate(int node, int[] ancestor, int[] label, int[] semi)
    {
        if (ancestor[node] < 0)
        {
            return label[node];
        }

        Compress(node, ancestor, label, semi);
        return semi[label[ancestor[node]]] >= semi[label[node]] ? label[node] : label[ancestor[node]];
    }

    private static void Compress(int node, int[] ancestor, int[] label, int[] semi)
    {
        var path = new Stack<int>();
        var current = node;
        while (ancestor[current] >= 0 && ancestor[ancestor[current]] >= 0)
        {
            path.Push(current);
            current = ancestor[current];
        }

        while (path.Count > 0)
        {
            current = path.Pop();
            if (semi[label[ancestor[current]]] < semi[label[current]])
            {
                label[current] = label[ancestor[current]];
            }

            ancestor[current] = ancestor[ancestor[current]];
        }
    }
}
