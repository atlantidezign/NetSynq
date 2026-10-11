using static NetSynq.Core.I18n;
using System.Text;
using System.Text.Json.Serialization;

namespace NetSynq.Core;

public enum CompactCompression
{
    /// <summary>File già compressi (jpg, mp4, zip…) solo archiviati; gli altri compressi in modo veloce.</summary>
    Auto = 0,
    /// <summary>Nessuna compressione: lo zip serve solo a raggruppare i file (il più veloce).</summary>
    None = 1,
    /// <summary>Compressione massima (lenta).</summary>
    Max = 2
}

public enum CompactKind
{
    Folder,     // si zippa l'intera cartella (e la si rimuove)
    SmallFiles  // la cartella resta; si raggruppano in uno zip solo i suoi file piccoli diretti
}

public enum CompactStatus
{
    Candidate,  // sarà zippata
    Skipped     // rispetta il criterio N/K ma un limite di sicurezza lo impedisce
}

public sealed class CompactOptions
{
    /// <summary>Una cartella è candidata se contiene PIÙ DI questo numero di file piccoli (direttamente al suo interno).</summary>
    public int MinSmallFiles { get; set; } = 5;
    /// <summary>"Piccolo" = dimensione STRETTAMENTE inferiore a questa soglia (byte).</summary>
    public long SmallFileBytes { get; set; } = 10 * 1024;
    /// <summary>Dimensione massima del sottoalbero da zippare (0 = nessun limite).</summary>
    public long MaxFolderBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    /// <summary>Dimensione massima del singolo file contenuto (0 = nessun limite).</summary>
    public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;
    public CompactCompression Compression { get; set; } = CompactCompression.Auto;
    /// <summary>Pattern separati da ';' (nomi di file/cartelle). Una cartella che li contiene non viene zippata.</summary>
    public string Excludes { get; set; } = ".git; .svn";
    /// <summary>Se una cartella diventerebbe un insieme di soli zip delle sue sottocartelle (più di N voci), zippa direttamente la cartella madre.</summary>
    public bool MergeParents { get; set; } = true;
    /// <summary>Nelle cartelle che rispettano N/K ma non si possono zippare per intero (troppo grandi, file grossi…), raggruppa comunque i soli file piccoli diretti in uno zip dentro la cartella.</summary>
    public bool PartialPacks { get; set; } = true;
    /// <summary>Nomi di cartella (separati da ';', senza distinzione maiuscole/minuscole) che vengono sempre zippate per intero, senza guardare i file piccoli né i limiti di dimensione e senza analizzarne l'interno.</summary>
    public string AlwaysZip { get; set; } = "node_modules";
    /// <summary>Nomi di cartella (separati da ';') delle quali NON si zippa la cartella ma ogni sottocartella diretta, per intero, senza limiti di dimensione e senza scenderci dentro (es. "profiles").</summary>
    public string ZipSubfolders { get; set; } = "profiles";

    /// <summary>Copia senza limiti di dimensione (per le cartelle "sempre zippate").</summary>
    public CompactOptions WithoutSizeLimits() => new()
    {
        MinSmallFiles = MinSmallFiles, SmallFileBytes = SmallFileBytes, MaxFolderBytes = 0, MaxFileBytes = 0,
        Compression = Compression, Excludes = Excludes, MergeParents = MergeParents, PartialPacks = PartialPacks, AlwaysZip = AlwaysZip, ZipSubfolders = ZipSubfolders
    };
}

public sealed class CompactItem
{
    public string Path { get; set; } = "";          // relativo alla radice, con '/'
    public string FullPath { get; set; } = "";
    public int SmallFiles { get; set; }             // file piccoli diretti
    public int TotalFiles { get; set; }             // tutti i file del sottoalbero
    public int TotalDirs { get; set; }              // sottocartelle (escluse sé stessa)
    public long TotalBytes { get; set; }
    public long LargestFile { get; set; }
    public CompactStatus Status { get; set; }
    public string? Reason { get; set; }
    public bool Selected { get; set; } = true;
    /// <summary>Spiegazione mostrata all'utente: perché questa cartella viene zippata.</summary>
    public string Note { get; set; } = "";
    /// <summary>Per le cartelle accorpate: le unità che sarebbero state zippate separatamente (restano la scelta di ripiego se l'accorpamento viene deselezionato).</summary>
    public List<CompactItem> Children { get; set; } = new();
    public bool IsMerged => Children.Count > 0;
    public CompactKind Kind { get; set; } = CompactKind.Folder;
    /// <summary>Cartella in elenco "sempre zippate": i limiti di dimensione non si applicano.</summary>
    public bool Forced { get; set; }
    public const string SmallZipBase = "_piccoli";
    public string ZipName => Kind == CompactKind.SmallFiles ? SmallZipBase + ".zip" : System.IO.Path.GetFileName(FullPath) + ".zip";
}

public sealed class CompactPlan
{
    public string Root { get; set; } = "";
    public CompactOptions Options { get; set; } = new();
    public List<CompactItem> Items { get; set; } = new();
    /// <summary>Numero totale di file nella cartella analizzata (esclusi elementi esclusi/collegamenti).</summary>
    public long ScannedFiles { get; set; }
    /// <summary>Numero di file dopo la compattazione prevista: i file zippati diventano uno zip a cartella.</summary>
    public long FilesAfter => ScannedFiles - SelectedFiles + SelectedCount;
    public long ScannedDirs { get; set; }

    public IEnumerable<CompactItem> Candidates => Items.Where(i => i.Status == CompactStatus.Candidate);
    /// <summary>Le cartelle che verrebbero realmente zippate: un accorpamento selezionato vale per sé; deselezionato, si ripiega sulle sue unità interne.</summary>
    public IEnumerable<CompactItem> SelectedCandidates => Candidates.SelectMany(Expand);
    private static IEnumerable<CompactItem> Expand(CompactItem i)
    {
        if (i.IsMerged && !i.Selected) return i.Children.SelectMany(Expand);
        return i.Selected ? new[] { i } : Array.Empty<CompactItem>();
    }
    public int CandidateCount => Candidates.Count();
    public int SelectedCount => SelectedCandidates.Count();
    public int SkippedCount => Items.Count(i => i.Status == CompactStatus.Skipped);
    public long SelectedBytes => SelectedCandidates.Sum(i => i.TotalBytes);
    public long SelectedFiles => SelectedCandidates.Sum(i => (long)i.TotalFiles);
    public long SelectedDirs => SelectedCandidates.Sum(i => i.Kind == CompactKind.Folder ? (long)i.TotalDirs + 1 : 0);
}

public sealed class CompactItemResult
{
    public string Path { get; set; } = "";
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string? ZipPath { get; set; }
    public int Files { get; set; }
    public int Dirs { get; set; }
    public long BytesBefore { get; set; }
    public long ZipBytes { get; set; }
    /// <summary>Numero di volumi (1 = zip unico; oltre il limite di 4 GB lo zip viene diviso in nome.zip.001, .002…).</summary>
    public int Volumes { get; set; } = 1;
    /// <summary>File non cancellati perché modificati dopo l'archiviazione (restano al loro posto).</summary>
    public int Leftover { get; set; }
}

public sealed class CompactResult
{
    public List<CompactItemResult> Items { get; } = new();
    public bool Cancelled { get; set; }
    public int Packed => Items.Count(i => i.Ok);
    public int Failed => Items.Count(i => !i.Ok);
    public long FilesArchived => Items.Where(i => i.Ok).Sum(i => (long)i.Files);
    public long BytesBefore => Items.Where(i => i.Ok).Sum(i => i.BytesBefore);
    public long ZipBytes => Items.Where(i => i.Ok).Sum(i => i.ZipBytes);
    public int Leftover => Items.Sum(i => i.Leftover);
    public int SplitArchives => Items.Count(i => i.Ok && i.Volumes > 1);
}

/// <summary>Report di una simulazione (compattazione o ripristino): nessuna modifica eseguita.</summary>
public sealed class CompactReport
{
    public string Title { get; set; } = Tr("cmp.model.sim_title");
    public List<string> Summary { get; } = new();
    public List<string> Lines { get; } = new();
    public string SummaryText => string.Join("\n", new[] { Title }.Concat(Summary));

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(SummaryText).AppendLine(new string('-', 78));
        foreach (var l in Lines) sb.AppendLine(l);
        return sb.ToString();
    }
}

// ---------------------------------------------------------------- manifest dentro lo zip
public sealed class ZipManifestFile
{
    [JsonPropertyName("p")] public string Path { get; set; } = "";
    [JsonPropertyName("s")] public long Size { get; set; }
    [JsonPropertyName("m")] public long MTimeTicks { get; set; }
    [JsonPropertyName("a")] public int Attr { get; set; }
    [JsonPropertyName("h")] public string Sha256 { get; set; } = "";
}

public sealed class ZipManifestDir
{
    [JsonPropertyName("p")] public string Path { get; set; } = "";
    [JsonPropertyName("m")] public long MTimeTicks { get; set; }
    [JsonPropertyName("a")] public int Attr { get; set; }
}

/// <summary>
/// File ".netsynq-manifest.json" nella radice dello zip: permette di verificare l'archivio e di
/// ripristinare la cartella originale con date e attributi esatti.
/// </summary>
public sealed class ZipManifest
{
    [JsonPropertyName("tool")] public string Tool { get; set; } = "NetSynq";
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("root")] public string Root { get; set; } = "";
    [JsonPropertyName("rootm")] public long RootMTimeTicks { get; set; }
    [JsonPropertyName("roota")] public int RootAttr { get; set; }
    /// <summary>true = zip dei soli file piccoli di una cartella (che resta al suo posto): le voci sono nella radice dello zip.</summary>
    [JsonPropertyName("partial")] public bool Partial { get; set; }
    [JsonPropertyName("created")] public string CreatedUtc { get; set; } = "";
    [JsonPropertyName("files")] public List<ZipManifestFile> Files { get; set; } = new();
    [JsonPropertyName("dirs")] public List<ZipManifestDir> Dirs { get; set; } = new();
}
