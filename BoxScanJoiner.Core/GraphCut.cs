namespace BoxScanJoiner.Core;

/// <summary>
///     Two-terminal minimum cut using the Boykov–Kolmogorov augmenting-path algorithm, which is fast on the sparse
///     grid graphs produced by image labelling problems.
/// </summary>
/// <remarks>
///     Y. Boykov and V. Kolmogorov, "An Experimental Comparison of Min-Cut/Max-Flow Algorithms for Energy
///     Minimization in Vision", IEEE TPAMI 26(9), 2004.
/// </remarks>
internal sealed class GraphCut
{
    const int NONE     = -1;
    const int TERMINAL = -2;
    const int ORPHAN   = -3;

    // Nodes
    readonly int[]  _first;
    readonly int[]  _parent;
    readonly int[]  _timestamp;
    readonly int[]  _distance;
    readonly bool[] _isSink;
    readonly bool[] _isActive;
    readonly long[] _terminalCapacity;

    // Arcs, stored in sister pairs so arc ^ 1 is the reverse arc
    int[]  _head;
    int[]  _next;
    long[] _capacity;
    int    _arcCount;

    readonly Queue<int> _active  = new();
    readonly Queue<int> _orphans = new();
    int                 _time;

    public GraphCut(int nodes, int edgeHint)
    {
        _first            = new int[nodes];
        _parent           = new int[nodes];
        _timestamp        = new int[nodes];
        _distance         = new int[nodes];
        _isSink           = new bool[nodes];
        _isActive         = new bool[nodes];
        _terminalCapacity = new long[nodes];
        Array.Fill(_first, NONE);

        _head     = new int[Math.Max(2, edgeHint * 2)];
        _next     = new int[_head.Length];
        _capacity = new long[_head.Length];
    }

    public long Flow { get; private set; }

    /// <summary>Adds capacities from the source and to the sink for <paramref name="node" />.</summary>
    public void AddTerminal(int node, long source, long sink)
    {
        // Only the difference matters for the cut; the common part is flow that always passes
        _terminalCapacity[node] += source - sink;
        Flow                    += Math.Min(source, sink);
    }

    public void AddEdge(int a, int b, long capacityAb, long capacityBa)
    {
        if(_arcCount + 2 > _head.Length)
        {
            int size = _head.Length * 2;
            Array.Resize(ref _head,     size);
            Array.Resize(ref _next,     size);
            Array.Resize(ref _capacity, size);
        }

        int ab = _arcCount++;
        int ba = _arcCount++;

        _head[ab]     = b;
        _capacity[ab] = capacityAb;
        _next[ab]     = _first[a];
        _first[a]     = ab;

        _head[ba]     = a;
        _capacity[ba] = capacityBa;
        _next[ba]     = _first[b];
        _first[b]     = ba;
    }

    /// <summary>True if <paramref name="node" /> ends up on the sink side of the minimum cut.</summary>
    public bool IsSink(int node) => _parent[node] != NONE && _isSink[node];

    public long MaxFlow()
    {
        for(var i = 0; i < _first.Length; i++)
        {
            _timestamp[i] = 0;

            if(_terminalCapacity[i] == 0)
            {
                _parent[i] = NONE;

                continue;
            }

            _isSink[i]   = _terminalCapacity[i] < 0;
            _parent[i]   = TERMINAL;
            _distance[i] = 1;
            SetActive(i);
        }

        int current = NONE;

        while(true)
        {
            int i = current;

            if(i == NONE || _parent[i] == NONE)
            {
                i = NextActive();

                if(i == NONE) break;
            }

            int middle = Grow(i);
            _time++;

            if(middle == NONE)
            {
                current = NONE;

                continue;
            }

            // Keep growing from the same node next time, it may still have paths
            current = i;
            Augment(middle);
            AdoptOrphans();
        }

        return Flow;
    }

    void SetActive(int i)
    {
        if(_isActive[i]) return;

        _isActive[i] = true;
        _active.Enqueue(i);
    }

    int NextActive()
    {
        while(_active.TryDequeue(out int i))
        {
            _isActive[i] = false;

            if(_parent[i] != NONE) return i;
        }

        return NONE;
    }

    /// <summary>Grows the tree containing <paramref name="i" />; returns an arc joining both trees, source to sink.</summary>
    int Grow(int i)
    {
        bool sink = _isSink[i];

        for(int a = _first[i]; a != NONE; a = _next[a])
        {
            // Source tree grows along residual arcs leaving i, sink tree along residual arcs entering i
            if((sink ? _capacity[a ^ 1] : _capacity[a]) == 0) continue;

            int j = _head[a];

            if(_parent[j] == NONE)
            {
                _isSink[j]    = sink;
                _parent[j]    = a ^ 1;
                _timestamp[j] = _timestamp[i];
                _distance[j]  = _distance[i] + 1;
                SetActive(j);
            }
            else if(_isSink[j] != sink)
                return sink ? a ^ 1 : a;
            else if(_timestamp[j] <= _timestamp[i] && _distance[j] > _distance[i])
            {
                // Shorter path to the terminal through i
                _parent[j]    = a ^ 1;
                _timestamp[j] = _timestamp[i];
                _distance[j]  = _distance[i] + 1;
            }
        }

        return NONE;
    }

    void Augment(int middle)
    {
        long bottleneck = _capacity[middle];
        int  i;

        // Source side: walk from the middle arc's tail up to the source
        for(i = _head[middle ^ 1]; _parent[i] != TERMINAL; i = _head[_parent[i]])
            bottleneck = Math.Min(bottleneck, _capacity[_parent[i] ^ 1]);

        bottleneck = Math.Min(bottleneck, _terminalCapacity[i]);

        // Sink side: walk from the middle arc's head down to the sink
        for(i = _head[middle]; _parent[i] != TERMINAL; i = _head[_parent[i]])
            bottleneck = Math.Min(bottleneck, _capacity[_parent[i]]);

        bottleneck = Math.Min(bottleneck, -_terminalCapacity[i]);

        _capacity[middle ^ 1] += bottleneck;
        _capacity[middle]     -= bottleneck;

        for(i = _head[middle ^ 1];;)
        {
            int a = _parent[i];

            if(a == TERMINAL) break;

            _capacity[a]     += bottleneck;
            _capacity[a ^ 1] -= bottleneck;

            if(_capacity[a ^ 1] == 0) MakeOrphan(i);

            i = _head[a];
        }

        _terminalCapacity[i] -= bottleneck;

        if(_terminalCapacity[i] == 0) MakeOrphan(i);

        for(i = _head[middle];;)
        {
            int a = _parent[i];

            if(a == TERMINAL) break;

            _capacity[a ^ 1] += bottleneck;
            _capacity[a]     -= bottleneck;

            if(_capacity[a] == 0) MakeOrphan(i);

            i = _head[a];
        }

        _terminalCapacity[i] += bottleneck;

        if(_terminalCapacity[i] == 0) MakeOrphan(i);

        Flow += bottleneck;
    }

    void MakeOrphan(int i)
    {
        _parent[i] = ORPHAN;
        _orphans.Enqueue(i);
    }

    void AdoptOrphans()
    {
        while(_orphans.TryDequeue(out int i)) Adopt(i);
    }

    /// <summary>Finds a new valid parent for orphan <paramref name="i" />, or frees it.</summary>
    void Adopt(int i)
    {
        bool sink     = _isSink[i];
        int  bestArc  = NONE;
        int  bestDist = int.MaxValue;

        for(int a0 = _first[i]; a0 != NONE; a0 = _next[a0])
        {
            // Candidate parent j must be able to push flow to i (source tree) or receive it (sink tree)
            if((sink ? _capacity[a0] : _capacity[a0 ^ 1]) == 0) continue;

            int j = _head[a0];

            if(_isSink[j] != sink || _parent[j] == NONE) continue;

            // Check that j is rooted at a terminal and measure its distance
            int d = 0;

            while(true)
            {
                if(_timestamp[j] == _time)
                {
                    d += _distance[j];

                    break;
                }

                int a = _parent[j];
                d++;

                if(a == TERMINAL)
                {
                    _timestamp[j] = _time;
                    _distance[j]  = 1;

                    break;
                }

                if(a == ORPHAN)
                {
                    d = int.MaxValue;

                    break;
                }

                j = _head[a];
            }

            if(d == int.MaxValue) continue;

            if(d < bestDist)
            {
                bestArc  = a0;
                bestDist = d;
            }

            // Cache distances along the checked path
            for(j = _head[a0]; _timestamp[j] != _time; j = _head[_parent[j]])
            {
                _timestamp[j] = _time;
                _distance[j]  = d--;
            }
        }

        if(bestArc != NONE)
        {
            _parent[i]    = bestArc;
            _timestamp[i] = _time;
            _distance[i]  = bestDist + 1;

            return;
        }

        // No parent: i becomes free; neighbours that could reach it get another chance
        _parent[i] = NONE;

        for(int a0 = _first[i]; a0 != NONE; a0 = _next[a0])
        {
            int j = _head[a0];
            int a = _parent[j];

            if(_isSink[j] != sink || a == NONE) continue;

            if((sink ? _capacity[a0] : _capacity[a0 ^ 1]) > 0) SetActive(j);

            if(a != TERMINAL && a != ORPHAN && _head[a] == i) MakeOrphan(j);
        }
    }
}