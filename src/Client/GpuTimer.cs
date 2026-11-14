using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using OpenTK.Graphics.OpenGL;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>GPU time between <see cref="Begin"/> and <see cref="End"/>, from timestamp queries read a few frames later (never
/// waiting for them), averaged. Timestamps, unlike elapsed-time queries, cannot clash with another mod's open query.</summary>
public sealed class GpuTimer : IDisposable
{
    const int Depth = 4;
    readonly int[] queries = new int[Depth * 2];
    readonly bool[] pending = new bool[Depth];
    int next;
    /// <summary>Averaged over about the last 30 measured frames.</summary>
    public double Milliseconds { get; private set; }
    public void Begin()
    {
        if (queries[0] == 0) GL.GenQueries(queries.Length, queries);
        int i = next;
        if (pending[i])
        {
            GL.GetQueryObject(queries[i * 2 + 1], GetQueryObjectParam.QueryResultAvailable, out int available);
            if (available == 0) { skip = true; return; }
            GL.GetQueryObject(queries[i * 2], GetQueryObjectParam.QueryResult, out long start);
            GL.GetQueryObject(queries[i * 2 + 1], GetQueryObjectParam.QueryResult, out long end);
            Milliseconds += ((end - start) / 1e6 - Milliseconds) / 30;
            pending[i] = false;
        }
        skip = false;
        GL.QueryCounter(queries[i * 2], QueryCounterTarget.Timestamp);
    }
    bool skip;
    public void End()
    {
        if (skip || queries[0] == 0) return;
        GL.QueryCounter(queries[next * 2 + 1], QueryCounterTarget.Timestamp);
        pending[next] = true;
        next = (next + 1) % Depth;
    }
    public void Dispose() { if (queries[0] != 0) GL.DeleteQueries(queries.Length, queries); queries[0] = 0; }
}
