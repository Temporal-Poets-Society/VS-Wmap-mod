using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;

namespace Wmap.Core;

/// <summary>
/// The size of a world's cache folder of predicted tiles, shared by every writer: the server's atlas and ore heat maps and
/// the client's tiles (0.5.3, WM-0040: it lived in AtlasServer, and client code called the server for it).
/// </summary>
public static class TileCache
{
    // Each cache folder's trimmed tiles in bytes (-1 until first counted), and whether a trim of it is running.
    // Bytes and RetryAt are used only by the writer holding Trimming; the others add to During.
    sealed class CacheSize { public long Bytes = -1, During, RetryAt; public int Trimming; }
    static readonly ConcurrentDictionary<string, CacheSize> cacheSizes = new();

    /// <summary>Counts a predicted tile written to a cache folder. The folder is scanned once, when first counted, then
    /// only when the count passes <paramref name="mib"/>: it is trimmed to 90 % of that. One trim of a folder at a time,
    /// for every writer sharing it; the others do not wait, and what they wrote meanwhile is counted after it. Rewritten
    /// files, and files written while a scan ran, may count twice, which only trims a little sooner. A trim that cannot
    /// get under the limit (files in use or read-only) is not tried again for a minute.</summary>
    public static void Wrote(string folder, long bytes, int mib)
    {
        var size = cacheSizes.GetOrAdd(Path.GetFullPath(folder), _ => new());
        if (Interlocked.CompareExchange(ref size.Trimming, 1, 0) != 0) { Interlocked.Add(ref size.During, bytes); return; }
        long limit = (long)mib << 20, now = Environment.TickCount64;
        try
        {
            long added = bytes + Interlocked.Exchange(ref size.During, 0);
            if (size.Bytes >= 0 && (size.Bytes += added) <= limit || now < size.RetryAt) return;
            // The scan counts what was written before it; what is written while it runs is added after.
            long left = Trim(folder, mib, 0.9);
            size.Bytes = left + Interlocked.Exchange(ref size.During, 0);
            size.RetryAt = left > limit ? now + 60_000 : 0;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { size.RetryAt = now + 60_000; }
        finally { Volatile.Write(ref size.Trimming, 0); }
    }

    /// <summary>Deletes the oldest predicted tiles while they take more than <paramref name="mib"/>, down to
    /// <paramref name="to"/> of it. Returns the bytes they take afterwards.</summary>
    public static long Trim(string folder, int mib, double to = 1)
    {
        if (!Directory.Exists(folder)) return 0;
        // Only predicted tiles (and ore heat tiles, WM-D48) are trimmed; the world chart and recorded observations are kept.
        var dir = new DirectoryInfo(folder);
        var files = dir.EnumerateFiles("0-*.wmt").Concat(dir.EnumerateFiles("b-*.wmt")).Concat(dir.EnumerateFiles("o-*.wmt")).OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length), limit = (long)mib * 1024 * 1024;
        if (total <= limit) return total;
        long target = (long)(limit * to);
        foreach (var f in files)
        {
            if (total <= target) break;
            // A file another trim removed, one in use or read-only: skip it; trimming must not fail the tile being made.
            try { long size = f.Length; f.Delete(); total -= size; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
        return total;
    }
}
