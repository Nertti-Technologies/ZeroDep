namespace ZeroDep.Smoke;

/// <summary>
/// Finds the PDFs under a local corpus folder. Symbolic links are followed (the owner's corpus links one local
/// folder into another), but every real directory is checked against <see cref="ForbiddenRoots"/> and visited
/// once, so a link can neither lead into external or synced storage nor loop.
/// </summary>
internal static class CorpusScanner
{
    /// <summary>
    /// Network, attached and synced-cloud locations the tool must never read (owner rule §3). Paths beginning
    /// with <c>~/</c> are relative to the user's home directory.
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenRoots =
    [
        "/Volumes",
        "/Network",
        "/net",
        "~/SynologyDrive",
        "~/Library/CloudStorage",
        "~/Library/Mobile Documents",
    ];

    private const int MaxLinkHops = 32;

    private const string DataVolume = "/System/Volumes/Data";

    /// <summary>Whether the real path <paramref name="path"/> is inside a forbidden root.</summary>
    public static bool IsForbidden(string path, string home) => IsForbidden(path, home, ForbiddenRoots);

    /// <summary>Whether <paramref name="path"/> is inside one of <paramref name="roots"/>.</summary>
    public static bool IsForbidden(string path, string home, IReadOnlyList<string> roots)
    {
        string canonical = WithoutDataVolume(path);
        foreach (string root in roots)
        {
            string full = root.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, root.Substring(2)) : root;
            if (IsSameOrUnder(canonical, WithoutDataVolume(full))) return true;
        }

        return false;
    }

    /// <summary>
    /// On macOS the system volume firmlinks <c>/Volumes</c>, <c>/Users</c> and others to the data volume, so
    /// <c>/System/Volumes/Data/Volumes/NAS</c> is the same place as <c>/Volumes/NAS</c>. A firmlink is not a
    /// symbolic link and is not resolved by <see cref="RealPath(string, string)"/>, so the prefix is removed here.
    /// </summary>
    private static string WithoutDataVolume(string path)
        => path.StartsWith(DataVolume + "/", StringComparison.OrdinalIgnoreCase) ? path.Substring(DataVolume.Length) : path;

    /// <summary>
    /// Every <c>*.pdf</c> under <paramref name="corpus"/>, as real paths sorted ordinally (so a seeded sample is
    /// reproducible). Throws <see cref="InvalidOperationException"/> when the corpus or any directory reached
    /// through a link is forbidden.
    /// </summary>
    public static IReadOnlyList<string> Scan(string corpus, string home) => Scan(corpus, home, ForbiddenRoots);

    /// <summary>As <see cref="Scan(string, string)"/>, with the forbidden roots given (for tests).</summary>
    public static IReadOnlyList<string> Scan(string corpus, string home, IReadOnlyList<string> forbidden)
    {
        string root = RealPath(Path.GetFullPath(corpus), home, forbidden, 0);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"corpus not found: {corpus}");

        var files = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string dir = RealPath(pending.Pop(), home, forbidden, 0);
            if (!visited.Add(dir)) continue;

            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                if (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) files.Add(RealPath(file, home, forbidden, 0));
            }

            foreach (string sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
            {
                pending.Push(sub);
            }
        }

        return [.. files];
    }

    /// <summary>
    /// Resolves every symbolic link in <paramref name="path"/>, component by component. Each path is checked
    /// against <see cref="ForbiddenRoots"/> <i>before</i> the file system is asked about it, so not even a
    /// metadata read reaches a forbidden location.
    /// </summary>
    /// <exception cref="InvalidOperationException">The path or a link target is forbidden.</exception>
    public static string RealPath(string path, string home) => RealPath(path, home, ForbiddenRoots, 0);

    private static string RealPath(string path, string home, IReadOnlyList<string> forbidden, int hops)
    {
        // The same bound as the operating system's own link resolution (MAXSYMLINKS on macOS is 32).
        if (hops > MaxLinkHops) throw new IOException($"too many levels of symbolic links: {path}");
        string full = Path.GetFullPath(path);
        Guard(full, home, forbidden);
        string? parent = Path.GetDirectoryName(full);
        if (parent is null) return full;

        string resolved = Path.Combine(RealPath(parent, home, forbidden, hops), Path.GetFileName(full));
        Guard(resolved, home, forbidden);
        FileSystemInfo info = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
        string? target = info.LinkTarget;
        if (target is null) return resolved;

        // One link at a time, so every hop of a chain is guarded before it is read.
        return RealPath(Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(resolved)!, target), home, forbidden, hops + 1);
    }

    private static void Guard(string realPath, string home, IReadOnlyList<string> forbidden)
    {
        if (IsForbidden(realPath, home, forbidden))
        {
            throw new InvalidOperationException(
                $"refusing to read external or synced storage: {realPath}. Copy the files to a local folder first.");
        }
    }

    private static bool IsSameOrUnder(string path, string root)
    {
        string p = path.TrimEnd('/');
        string r = root.TrimEnd('/');
        return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase);
    }
}
