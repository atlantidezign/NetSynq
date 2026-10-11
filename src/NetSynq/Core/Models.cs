using System.Text.Json.Serialization;

namespace NetSynq.Core;

/// <summary>File o cartella descritti nel manifest (percorso relativo con '/').</summary>
public sealed class Entry
{
    [JsonPropertyName("p")] public string Path { get; set; } = "";
    [JsonPropertyName("d")] public bool IsDir { get; set; }
    [JsonPropertyName("s")] public long Size { get; set; }
    /// <summary>Ultima modifica in UTC (ticks).</summary>
    [JsonPropertyName("m")] public long MTimeTicks { get; set; }
    /// <summary>Attributi replicati: ReadOnly / Hidden / System.</summary>
    [JsonPropertyName("a")] public int Attr { get; set; }

    [JsonIgnore] public DateTime MTimeUtc => new DateTime(MTimeTicks, DateTimeKind.Utc);
}

public sealed class Manifest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("proto")] public int Protocol { get; set; } = 1;
    [JsonPropertyName("entries")] public List<Entry> Entries { get; set; } = new();
}

public enum CompareMode
{
    /// <summary>Dimensione + data di modifica. Velocissimo.</summary>
    Fast = 0,
    /// <summary>Come Fast, ma se la dimensione è uguale e la data no, confronta l'hash: evita di ricopiare file identici (ideale alla prima sincronizzazione).</summary>
    Smart = 1,
    /// <summary>Hash SHA-256 di ogni file su entrambi i lati (lento la prima volta, poi in cache).</summary>
    Full = 2
}

public enum DiffStatus
{
    Same,       // identico
    New,        // esiste solo sul master
    Modified,   // diverso
    TouchOnly,  // contenuto identico, cambia solo la data (basta aggiornare la data)
    Delete      // esiste solo sullo slave (sarà eliminato)
}

public sealed class DiffItem
{
    public string Path { get; set; } = "";
    public bool IsDir { get; set; }
    public Entry? Remote { get; set; }
    public Entry? Local { get; set; }
    public DiffStatus Status { get; set; }
    /// <summary>Se false, l'utente ha escluso la voce dall'operazione.</summary>
    public bool Selected { get; set; } = true;
    /// <summary>File su un lato e cartella sull'altro.</summary>
    public bool TypeConflict { get; set; }
    public string? Note { get; set; }
}

public sealed class SyncPlan
{
    public string LocalRoot { get; set; } = "";
    public CompareMode Mode { get; set; }
    public List<DiffItem> Items { get; set; } = new();
    /// <summary>File temporanei di trasferimenti interrotti (.nspart) trovati sullo slave.</summary>
    public List<string> StaleParts { get; set; } = new();

    public int Count(DiffStatus s) => Items.Count(i => i.Status == s);
    /// <summary>File (non cartelle) con questo stato, selezionati o no.</summary>
    public int FileCount(DiffStatus s) => Items.Count(i => i.Status == s && !i.IsDir);
    /// <summary>File (non cartelle) con questo stato che l'utente ha lasciato selezionati.</summary>
    public int SelectedFileCount(DiffStatus s) => Items.Count(i => i.Selected && i.Status == s && !i.IsDir);

    // Tutti i totali seguenti riguardano SOLO le voci selezionate.
    public long DownloadBytes => Items.Where(i => i.Selected && !i.IsDir && (i.Status == DiffStatus.New || i.Status == DiffStatus.Modified))
                                      .Sum(i => i.Remote?.Size ?? 0);
    public long DeleteBytes => Items.Where(i => i.Selected && !i.IsDir && i.Status == DiffStatus.Delete).Sum(i => i.Local?.Size ?? 0);
    public long ReplacedBytes => Items.Where(i => i.Selected && !i.IsDir && i.Status == DiffStatus.Modified).Sum(i => i.Local?.Size ?? 0);

    /// <summary>Esistono differenze (a prescindere dalla selezione).</summary>
    public bool HasChanges => Items.Any(i => i.Status != DiffStatus.Same);
    /// <summary>Esiste almeno una differenza ancora selezionata.</summary>
    public bool HasSelectedChanges => Items.Any(i => i.Selected && i.Status != DiffStatus.Same);
    /// <summary>Differenze escluse dall'utente.</summary>
    public int DeselectedCount => Items.Count(i => !i.Selected && i.Status != DiffStatus.Same);

    /// <summary>
    /// Seleziona/deseleziona una voce. Se è una cartella, la scelta si propaga a tutto ciò che contiene:
    /// escludere una cartella esclude tutto il suo contenuto.
    /// </summary>
    public void SetSelected(DiffItem item, bool value)
    {
        if (item.Status == DiffStatus.Same) return;
        item.Selected = value;
        if (!item.IsDir) return;
        var prefix = item.Path + "/";
        foreach (var i in Items)
            if (i.Status != DiffStatus.Same && i.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                i.Selected = value;
    }
}

public sealed class ProgressInfo
{
    public string Phase { get; set; } = "";
    public long Done { get; set; }
    public long Total { get; set; }
    public long FilesDone { get; set; }
    public long FilesTotal { get; set; }
    public string? Current { get; set; }
}

public sealed class SyncOptions
{
    public bool DeleteExtra { get; set; } = true;
    public int Parallel { get; set; } = 3;
}

public sealed class SyncResult
{
    public int Copied, Touched, Deleted, DirsCreated, DirsDeleted;
    public long BytesCopied;
    public List<string> Errors { get; } = new();
    public bool Cancelled;
}
