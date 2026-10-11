using static NetSynq.Core.I18n;

namespace NetSynq.Core;

public static class Scanner
{
    /// <summary>
    /// Elenca ricorsivamente file e cartelle sotto <paramref name="root"/>.
    /// Collegamenti simbolici/junction e file .nspart (temporanei) sono ignorati.
    /// </summary>
    public static List<Entry> Scan(string root, ExcludeMatcher ex, Action<string>? warn = null,
        List<string>? stalePartsOut = null, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var result = new List<Entry>();
        var stack = new Stack<string>();
        stack.Push(root);
        int n = 0;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> children;
            try { children = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) { warn?.Invoke(Tr("sync.scan.dir_unreadable", dir, e.Message)); continue; }

            foreach (var fsi in children)
            {
                var rel = PathUtil.ToRel(root, fsi.FullName);
                if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    warn?.Invoke(Tr("sync.scan.symlink", rel));
                    continue;
                }
                bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;

                if (!isDir && fsi.Name.EndsWith(PathUtil.PartExtension, StringComparison.OrdinalIgnoreCase))
                {
                    stalePartsOut?.Add(fsi.FullName);
                    continue;
                }
                if (ex.IsExcluded(rel)) continue;

                try
                {
                    var e = new Entry
                    {
                        Path = rel,
                        IsDir = isDir,
                        Size = isDir ? 0 : ((FileInfo)fsi).Length,
                        MTimeTicks = isDir ? 0 : fsi.LastWriteTimeUtc.Ticks,
                        Attr = (int)(fsi.Attributes & PathUtil.ReplicatedAttrs)
                    };
                    result.Add(e);
                    if (isDir) stack.Push(fsi.FullName);
                }
                catch (Exception e) { warn?.Invoke(Tr("sync.scan.entry_unreadable", rel, e.Message)); }

                if ((++n & 0x3FF) == 0) progress?.Report(n);
            }
        }
        progress?.Report(n);
        return result;
    }
}
