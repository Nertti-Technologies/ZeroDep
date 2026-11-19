namespace ZeroDep.Smoke;

/// <summary>Picks a reproducible random sample: the same files, sorted list and seed always give the same picks.</summary>
internal static class Sampler
{
    public static IReadOnlyList<string> Pick(IReadOnlyList<string> files, int count, int seed)
    {
        if (count >= files.Count) return files;

        // A partial Fisher–Yates shuffle over a copy; System.Random with a seed is stable across runs.
        string[] pool = [.. files];
        var random = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            int j = random.Next(i, pool.Length);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        return pool[..count];
    }
}
