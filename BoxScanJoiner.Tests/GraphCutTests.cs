using BoxScanJoiner.Core;

namespace BoxScanJoiner.Tests;

public class GraphCutTests
{
    /// <summary>Reference Edmonds–Karp max-flow on a dense capacity matrix; node 0 is the source, 1 the sink.</summary>
    static long ReferenceMaxFlow(long[,] capacity)
    {
        int  n    = capacity.GetLength(0);
        var  r    = (long[,])capacity.Clone();
        long flow = 0;

        while(true)
        {
            var parent = new int[n];
            Array.Fill(parent, -1);
            parent[0] = 0;
            var queue = new Queue<int>([0]);

            while(queue.Count > 0 && parent[1] < 0)
            {
                int u = queue.Dequeue();

                for(var v = 0; v < n; v++)
                {
                    if(parent[v] >= 0 || r[u, v] <= 0) continue;

                    parent[v] = u;
                    queue.Enqueue(v);
                }
            }

            if(parent[1] < 0) return flow;

            long b = long.MaxValue;

            for(var v = 1; v != 0; v = parent[v]) b = Math.Min(b, r[parent[v], v]);

            for(var v = 1; v != 0; v = parent[v])
            {
                r[parent[v], v] -= b;
                r[v, parent[v]] += b;
            }

            flow += b;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void MatchesReferenceOnRandomGrids(int seed)
    {
        var rng = new Random(seed);
        int w   = rng.Next(3, 12);
        int h   = rng.Next(3, 12);
        int n   = w * h;

        var graph = new GraphCut(n, n * 2);
        var dense = new long[n + 2, n + 2];

        for(var i = 0; i < n; i++)
        {
            long source = rng.Next(3) == 0 ? rng.Next(0, 50) : 0;
            long sink   = rng.Next(3) == 0 ? rng.Next(0, 50) : 0;
            graph.AddTerminal(i, source, sink);
            dense[0, i + 2]    += source;
            dense[i    + 2, 1] += sink;
        }

        for(var y = 0; y < h; y++)
        {
            for(var x = 0; x < w; x++)
            {
                int i = y * w + x;

                foreach(int j in new[]
                        {
                            x + 1 < w ? i + 1 : -1, y + 1 < h ? i + w : -1
                        })
                {
                    if(j < 0) continue;

                    long ab = rng.Next(0, 30), ba = rng.Next(0, 30);
                    graph.AddEdge(i, j, ab, ba);
                    dense[i + 2, j + 2] += ab;
                    dense[j + 2, i + 2] += ba;
                }
            }
        }

        long flow = graph.MaxFlow();
        Assert.Equal(ReferenceMaxFlow(dense), flow);

        // The labelling must be a cut whose capacity equals the flow
        long cut = 0;

        for(var i = 0; i < n; i++)
        {
            bool si = graph.IsSink(i);
            cut += si ? dense[0, i + 2] : dense[i + 2, 1];

            for(var j = 0; j < n; j++)
            {
                if(!si && graph.IsSink(j)) cut += dense[i + 2, j + 2];
            }
        }

        Assert.Equal(flow, cut);
    }
}