using System.Collections;
using System.Reflection;
using Halfling.Geometry;
using HarmonyLib;

internal static class ResourceTraversalTests
{
    private delegate bool Lookup(Dictionary<IntVector2, int> cells, IntVector2 cell, out int value, ref int remaining);
    private delegate bool Next(IEnumerator iterator, int remaining);

    internal static void Run(Assembly patches)
    {
        var type = patches.GetType("EmmanimLagFix.Code.ResourceSearchTraversalPatch", true)!;
        if (!(bool)AccessTools.Property(type, "Applied").GetValue(null)!)
            throw new Exception("Resource traversal transpiler did not apply to the current game.");
        var lookup = AccessTools.Method(type, "LookupAndCount").MakeGenericMethod(typeof(int)).CreateDelegate<Lookup>();
        var next = AccessTools.Method(type, "MoveNext").CreateDelegate<Next>();
        var random = new Random(29210);
        for (var trial = 0; trial < 400; trial++)
        {
            var cells = new Dictionary<IntVector2, int>();
            for (var i = 0; i < 64; i++) if (random.Next(4) == 0) cells.Add(new IntVector2(i, 0), i);
            // Include unreachable source cells and path iteration limits.
            var path = Enumerable.Range(0, random.Next(65)).OrderBy(_ => random.Next()).Select(i => new IntVector2(i, 0)).ToArray();
            foreach (var disabled in new[] { false, true })
            {
                var original = new List<int>();
                var originalVisited = 0;
                var count = cells.Count;
                foreach (var cell in path)
                {
                    originalVisited++;
                    if (cells.TryGetValue(cell, out var value)) original.Add(value);
                    if (!disabled && cells.Count > 0 && cells.ContainsKey(cell) && --count == 0) break;
                }
                var actual = new List<int>();
                var visited = 0;
                var remaining = disabled || cells.Count == 0 ? -1 : cells.Count;
                using (var iterator = ((IEnumerable<IntVector2>)path).GetEnumerator())
                {
                    while (next(iterator, remaining))
                    {
                        visited++;
                        if (lookup(cells, iterator.Current, out var value, ref remaining)) actual.Add(value);
                    }
                }
                if (visited != originalVisited || !actual.SequenceEqual(original))
                    throw new Exception("Resource traversal order/termination differs from the previous patch.");
            }
        }
        Console.WriteLine("PASS resource traversal: transpiler applied; 800 order/termination fixtures, including unreachable cells, empty maps and disabled narrowing mode.");
    }
}