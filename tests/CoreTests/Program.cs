using System.Net;
using System.Text;
using NetSynq.Core;

// Test end-to-end del Core: server + client su loopback, cartelle temporanee.
int failures = 0, checks = 0;
void Check(bool ok, string what)
{
    checks++;
    if (!ok) { failures++; Console.WriteLine("  FAIL: " + what); }
    else Console.WriteLine("  ok:   " + what);
}

var tmp = Path.Combine(Path.GetTempPath(), "netsynq-test-" + Guid.NewGuid().ToString("N")[..8]);
var master = Path.Combine(tmp, "master");
var slave = Path.Combine(tmp, "slave");
Directory.CreateDirectory(master); Directory.CreateDirectory(slave);
Action<string> log = s => { if (Environment.GetEnvironmentVariable("VERBOSE") == "1") Console.WriteLine("      · " + s); };

string R2(string root, string rel) => File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
void W(string root, string rel, string content, DateTime? mtime = null)
{
    var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(p)!);
    File.WriteAllText(p, content);
    if (mtime != null) File.SetLastWriteTimeUtc(p, mtime.Value);
}

var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
// --- master iniziale
W(master, "a.txt", "hello", t0);
W(master, "src/main.c", "int main(){}\nline2\nline3\n", t0);
W(master, "src/deep/x/y.bin", new string('z', 3_000_000), t0);
W(master, ".svn/wc.db", "sqlite-ish", t0);
W(master, "empty/.keep", "", t0);
Directory.CreateDirectory(Path.Combine(master, "emptydir"));
W(master, "docs/spazio e àccènti #1.txt", "unicode ok", t0);
File.SetAttributes(Path.Combine(master, ".svn", "wc.db"), FileAttributes.ReadOnly);

// --- slave iniziale: un file già identico ma con data diversa, uno diverso, uno extra
W(slave, "a.txt", "hello", t0.AddDays(5));            // stesso contenuto, data diversa -> TouchOnly in Smart
W(slave, "src/main.c", "int main(){ OLD }\n", t0);    // diverso
W(slave, "extra/gone.txt", "to delete", t0);          // da eliminare
W(slave, "extra/sub/gone2.txt", "to delete", t0);

const string token = "TESTTOKEN123";
var serverCache = new HashCache(Path.Combine(tmp, "server-cache.json"));
var localCache = new HashCache(Path.Combine(tmp, "client-cache.json"));
using var server = new SyncServer(master, IPAddress.Loopback, 0, token, new ExcludeMatcher(""), serverCache, log, "TEST-MASTER");
server.Start();
int port = server.Port;
using var rc = new RemoteClient("127.0.0.1", port, token);

Console.WriteLine("== connessione e auth");
Check(await rc.PingAsync(default) == "TEST-MASTER", "ping restituisce il nome del master");
using (var bad = new RemoteClient("127.0.0.1", port, "WRONG"))
{
    bool threw = false;
    try { await bad.PingAsync(default); } catch (UnauthorizedAccessException) { threw = true; }
    Check(threw, "token errato rifiutato");
}

Console.WriteLine("== sicurezza percorsi");
{
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    http.DefaultRequestHeaders.Add("X-Token", token);
    foreach (var evil in new[] { "../slave/a.txt", "..%2Fslave%2Fa.txt", "%2Fetc%2Fpasswd", "src/../../slave/a.txt", "a.txt/.." })
    {
        var r = await http.GetAsync("/file?path=" + evil);
        Check(r.StatusCode == HttpStatusCode.NotFound, $"path traversal bloccato: {evil} -> {(int)r.StatusCode}");
    }
    var ok = await http.GetAsync("/file?path=" + Uri.EscapeDataString("docs/spazio e àccènti #1.txt"));
    Check(ok.IsSuccessStatusCode && await ok.Content.ReadAsStringAsync() == "unicode ok", "nomi con spazi/accenti/# scaricabili");
}

Console.WriteLine("== confronto (modalità Smart)");
var ex = new ExcludeMatcher("");
var plan = await Planner.BuildAsync(rc, slave, CompareMode.Smart, ex, localCache, null, log, default);
DiffStatus S(string p) => plan.Items.First(i => i.Path == p).Status;
Check(S("a.txt") == DiffStatus.TouchOnly, "a.txt: contenuto uguale, data diversa -> TouchOnly (nessuna ricopia)");
Check(S("src/main.c") == DiffStatus.Modified, "src/main.c: modificato");
Check(S("src/deep/x/y.bin") == DiffStatus.New, "y.bin: nuovo");
Check(S(".svn/wc.db") == DiffStatus.New, ".svn incluso");
Check(S("emptydir") == DiffStatus.New && plan.Items.First(i => i.Path == "emptydir").IsDir, "cartella vuota rilevata");
Check(S("extra/gone.txt") == DiffStatus.Delete && S("extra") == DiffStatus.Delete, "extra locali marcati da eliminare");
Check(plan.DownloadBytes > 3_000_000, $"byte da scaricare calcolati ({PathUtil.FormatBytes(plan.DownloadBytes)})");
Check(Syncer.CheckSpace(plan, new SyncOptions()) == null, "controllo spazio ok");

Console.WriteLine("== sincronizzazione");
var res = await Syncer.ApplyAsync(plan, rc, new SyncOptions { DeleteExtra = true, Parallel = 3 }, null, log, default);
Check(res.Errors.Count == 0, "nessun errore" + (res.Errors.Count > 0 ? ": " + string.Join(" | ", res.Errors) : ""));
string R(string p) => File.ReadAllText(Path.Combine(slave, p.Replace('/', Path.DirectorySeparatorChar)));
Check(R("src/main.c") == "int main(){}\nline2\nline3\n", "main.c aggiornato");
Check(R("src/deep/x/y.bin").Length == 3_000_000, "file grande copiato per intero");
Check(R("docs/spazio e àccènti #1.txt") == "unicode ok", "file unicode copiato");
Check(!File.Exists(Path.Combine(slave, "extra", "gone.txt")) && !Directory.Exists(Path.Combine(slave, "extra")), "extra eliminati (anche cartelle)");
Check(Directory.Exists(Path.Combine(slave, "emptydir")), "cartella vuota creata");
Check(File.GetLastWriteTimeUtc(Path.Combine(slave, "a.txt")) == t0, "data di a.txt riallineata al master");
Check(File.GetLastWriteTimeUtc(Path.Combine(slave, "src", "main.c")) == t0, "data di main.c uguale al master");
Check((File.GetAttributes(Path.Combine(slave, ".svn", "wc.db")) & FileAttributes.ReadOnly) != 0, "attributo sola-lettura replicato");
Check(Directory.GetFiles(slave, "*.nspart", SearchOption.AllDirectories).Length == 0, "nessun .nspart residuo");

Console.WriteLine("== seconda passata: deve essere tutto identico");
foreach (var mode in new[] { CompareMode.Fast, CompareMode.Smart, CompareMode.Full })
{
    var p2 = await Planner.BuildAsync(rc, slave, mode, ex, localCache, null, log, default);
    Check(!p2.HasChanges, $"nessuna differenza in modalità {mode}");
}

Console.WriteLine("== sovrascrittura di file sola-lettura sullo slave e modifica sul master");
W(master, "src/main.c", "int main(){ return 1; }\n", t0.AddHours(1));
File.SetAttributes(Path.Combine(slave, "src", "main.c"), FileAttributes.ReadOnly);
var p3 = await Planner.BuildAsync(rc, slave, CompareMode.Fast, ex, localCache, null, log, default);
Check(p3.Items.Count(i => i.Status != DiffStatus.Same) == 1 && p3.Items.First(i => i.Path == "src/main.c").Status == DiffStatus.Modified, "solo main.c risulta modificato");
var r3 = await Syncer.ApplyAsync(p3, rc, new SyncOptions(), null, log, default);
Check(r3.Errors.Count == 0 && R("src/main.c").Contains("return 1"), "sovrascritto anche se sola-lettura");

Console.WriteLine("== modalità Full rileva differenza di contenuto a parità di dimensione e data");
{
    var f = Path.Combine(slave, "a.txt");
    File.SetAttributes(f, FileAttributes.Normal);
    File.WriteAllText(f, "HELLO"); // stessa lunghezza, contenuto diverso
    File.SetLastWriteTimeUtc(f, t0);
    var fast = await Planner.BuildAsync(rc, slave, CompareMode.Fast, ex, localCache, null, log, default);
    var full = await Planner.BuildAsync(rc, slave, CompareMode.Full, ex, new HashCache(null), null, log, default); // cache vuota: la cache si fida di size+data
    Check(fast.Items.First(i => i.Path == "a.txt").Status == DiffStatus.Same, "Fast non se ne accorge (atteso)");
    Check(full.Items.First(i => i.Path == "a.txt").Status == DiffStatus.Modified, "Full lo rileva tramite hash");
    var rf = await Syncer.ApplyAsync(full, rc, new SyncOptions(), null, log, default);
    Check(rf.Errors.Count == 0 && R("a.txt") == "hello", "e lo ripara");
}

Console.WriteLine("== DeleteExtra disattivato");
W(slave, "solo-locale.txt", "keep me", t0);
var p4 = await Planner.BuildAsync(rc, slave, CompareMode.Fast, ex, localCache, null, log, default);
Check(p4.Items.First(i => i.Path == "solo-locale.txt").Status == DiffStatus.Delete, "file locale extra rilevato");
await Syncer.ApplyAsync(p4, rc, new SyncOptions { DeleteExtra = false }, null, log, default);
Check(File.Exists(Path.Combine(slave, "solo-locale.txt")), "senza 'elimina' il file resta");
await Syncer.ApplyAsync(p4, rc, new SyncOptions { DeleteExtra = true }, null, log, default);
Check(!File.Exists(Path.Combine(slave, "solo-locale.txt")), "con 'elimina' viene rimosso");

Console.WriteLine("== esclusioni");
W(master, "build/out.o", "obj", t0);
W(master, "notes.tmp", "tmp", t0);
var exc = new ExcludeMatcher("*.tmp; build");
var p5 = await Planner.BuildAsync(rc, slave, CompareMode.Fast, exc, localCache, null, log, default);
Check(!p5.Items.Any(i => i.Path.StartsWith("build") || i.Path.EndsWith(".tmp")), "pattern esclusi non compaiono nel piano");
Check(new ExcludeMatcher("a/b*").IsExcluded("a/bxx") && !new ExcludeMatcher("a/b*").IsExcluded("z/a/bxx"), "pattern con percorso");

Console.WriteLine("== ripresa download interrotto (Range)");
{
    var part = Path.Combine(tmp, "resume.part");
    var full = File.ReadAllBytes(Path.Combine(master, "src", "deep", "x", "y.bin"));
    File.WriteAllBytes(part, full.AsSpan(0, 1_000_000).ToArray());
    long counted = 0;
    await rc.DownloadAsync("src/deep/x/y.bin", part, full.Length, n => counted += n, default);
    Check(File.ReadAllBytes(part).SequenceEqual(full), "file ripreso da 1 MB e completato correttamente");
    Check(counted == full.Length, "avanzamento coerente con byte già presenti");
}

Console.WriteLine("== protezioni");
{
    var emptyMaster = Path.Combine(tmp, "emptymaster"); Directory.CreateDirectory(emptyMaster);
    using var s2 = new SyncServer(emptyMaster, IPAddress.Loopback, 0, token, ex, new HashCache(null), log);
    s2.Start();
    using var rc2 = new RemoteClient("127.0.0.1", s2.Port, token);
    bool refused = false;
    try { await Planner.BuildAsync(rc2, slave, CompareMode.Fast, ex, localCache, null, log, default); }
    catch (InvalidOperationException) { refused = true; }
    Check(refused, "master vuoto rifiutato (non cancella tutto lo slave)");

    // annullamento
    var cts = new CancellationTokenSource(); cts.Cancel();
    bool cancelled = false;
    try { await Planner.BuildAsync(rc, slave, CompareMode.Fast, ex, localCache, null, log, cts.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "confronto annullabile");
}

Console.WriteLine("== hash cache persistente");
{
    localCache.Save();
    var c2 = new HashCache(Path.Combine(tmp, "client-cache.json"));
    var f = Path.Combine(slave, "src", "main.c"); var fi = new FileInfo(f);
    var h1 = await c2.GetAsync(f, fi.Length, fi.LastWriteTimeUtc.Ticks);
    Check(h1 == HashCache.Compute(f, null, default), "hash da cache su disco corretto");
}

Console.WriteLine("== diff testuale (Myers)");
{
    string[] L(string s) => s.Split('\n');
    var d = LineDiff.Compute(L("a\nb\nc\nd\ne"), L("a\nB\nc\nd\ne\nf"))!;
    string Render(List<(DiffOp Op, string Line)> x) => string.Concat(x.Select(t => (t.Op == DiffOp.Equal ? " " : t.Op == DiffOp.Insert ? "+" : "-") + t.Line + ","));
    Check(Render(d) == " a,-b,+B, c, d, e,+f,", "diff semplice: " + Render(d));
    var rnd = new Random(42);
    bool allGood = true;
    for (int t = 0; t < 300 && allGood; t++)
    {
        var a = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => rnd.Next(0, 6).ToString()).ToArray();
        var b = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => rnd.Next(0, 6).ToString()).ToArray();
        var dd = LineDiff.Compute(a, b)!;
        var ra = dd.Where(x => x.Op != DiffOp.Insert).Select(x => x.Line).ToArray();
        var rb = dd.Where(x => x.Op != DiffOp.Delete).Select(x => x.Line).ToArray();
        allGood = ra.SequenceEqual(a) && rb.SequenceEqual(b);
    }
    Check(allGood, "300 diff casuali: ricostruiscono sempre entrambi i lati");
    var big = Enumerable.Range(0, 20000).Select(i => "riga " + i).ToArray();
    var big2 = big.ToArray(); big2[10000] = "cambiata";
    var dbig = LineDiff.Compute(big, big2)!;
    Check(dbig.Count(x => x.Op != DiffOp.Equal) == 2, "file grande con una riga cambiata");
}

Console.WriteLine("== selezione per voce, cascata sulle cartelle e simulazione");
{
    var m2 = Path.Combine(tmp, "m2"); var s2 = Path.Combine(tmp, "s2");
    W(m2, "dirA/a1.txt", "a1", t0); W(m2, "dirA/sub/a2.txt", "a2", t0);
    W(m2, "keep/k.txt", "kk", t0); W(m2, "mod.txt", "NEW-CONTENT", t0);
    W(s2, "mod.txt", "old", t0); W(s2, "old/x.txt", "x", t0); W(s2, "old/y/z.txt", "z", t0); W(s2, "keepdel.txt", "kd", t0);
    using var srv2 = new SyncServer(m2, IPAddress.Loopback, 0, token, ex, new HashCache(null), log);
    srv2.Start();
    using var rc2 = new RemoteClient("127.0.0.1", srv2.Port, token);
    var pl = await Planner.BuildAsync(rc2, s2, CompareMode.Fast, ex, new HashCache(null), null, log, default);
    DiffItem It(string path) => pl.Items.First(i => i.Path == path);

    Check(pl.Items.Where(i => i.Status != DiffStatus.Same).All(i => i.Selected), "all'inizio tutto è selezionato");
    pl.SetSelected(It("dirA"), false);
    Check(!It("dirA/a1.txt").Selected && !It("dirA/sub").Selected && !It("dirA/sub/a2.txt").Selected, "escludere una cartella esclude tutto il suo contenuto");
    Check(It("keep/k.txt").Selected, "le altre voci restano selezionate");
    pl.SetSelected(It("old"), false);
    Check(!It("old/x.txt").Selected && !It("old/y/z.txt").Selected, "idem per una cartella da eliminare");
    pl.SetSelected(It("keepdel.txt"), false);
    pl.SetSelected(It("dirA"), true);
    Check(It("dirA/sub/a2.txt").Selected, "riselezionando la cartella si riselezionano i contenuti");
    pl.SetSelected(It("dirA"), false);
    Check(pl.DeselectedCount == 9, $"9 voci escluse (trovate {pl.DeselectedCount})");
    Check(pl.DownloadBytes == 2 + 11, $"il totale da copiare conta solo le voci selezionate ({pl.DownloadBytes} B)");

    var snapshot = string.Join("|", Directory.GetFileSystemEntries(s2, "*", SearchOption.AllDirectories).OrderBy(x => x));
    var sim = Simulator.Run(pl, new SyncOptions { DeleteExtra = true });
    var snapshot2 = string.Join("|", Directory.GetFileSystemEntries(s2, "*", SearchOption.AllDirectories).OrderBy(x => x));
    Check(snapshot == snapshot2 && R2(s2, "mod.txt") == "old", "la simulazione non modifica nulla su disco");
    Check(sim.Copied == 2 && sim.CopiedNew == 1 && sim.CopiedModified == 1 && sim.DirsCreated == 1 && sim.Deleted == 0 && sim.Excluded == 9,
        $"la simulazione conta giusto: copie {sim.Copied}, cartelle {sim.DirsCreated}, eliminati {sim.Deleted}, esclusi {sim.Excluded}");
    Check(sim.Lines.Any(l => l.StartsWith("[ESCLUSO]") && l.Contains("dirA/sub/a2.txt")) && sim.ToText().Contains("SIMULAZIONE"), "il report elenca anche le voci escluse");

    var real = await Syncer.ApplyAsync(pl, rc2, new SyncOptions { DeleteExtra = true }, null, log, default);
    Check(real.Errors.Count == 0 && real.Copied == sim.Copied && real.Deleted == sim.Deleted && real.DirsCreated == sim.DirsCreated, "l'esecuzione reale fa esattamente ciò che la simulazione prevedeva");
    Check(!Directory.Exists(Path.Combine(s2, "dirA")), "cartella esclusa: non creata");
    Check(File.Exists(Path.Combine(s2, "old", "y", "z.txt")) && File.Exists(Path.Combine(s2, "keepdel.txt")), "file esclusi dall'eliminazione: ancora presenti");
    Check(R2(s2, "mod.txt") == "NEW-CONTENT" && R2(s2, "keep/k.txt") == "kk", "voci selezionate applicate");

    // simulazione con "elimina" disattivato
    var pl2 = await Planner.BuildAsync(rc2, s2, CompareMode.Fast, ex, new HashCache(null), null, log, default);
    var sim2 = Simulator.Run(pl2, new SyncOptions { DeleteExtra = false });
    Check(sim2.Deleted == 0 && sim2.KeptLocal >= 3, "con 'elimina' disattivato il report dice che restano");

    // ora seleziono tutto e finisco il lavoro
    foreach (var i in pl2.Items) pl2.SetSelected(i, true);
    var fin = await Syncer.ApplyAsync(pl2, rc2, new SyncOptions { DeleteExtra = true }, null, log, default);
    var pl3 = await Planner.BuildAsync(rc2, s2, CompareMode.Fast, ex, new HashCache(null), null, log, default);
    Check(fin.Errors.Count == 0 && !pl3.HasChanges && Directory.Exists(Path.Combine(s2, "dirA", "sub")) && !Directory.Exists(Path.Combine(s2, "old")), "selezionando tutto, lo slave diventa identico al master");
}

Console.WriteLine("== token");
{
    Check(AppSettings.NewToken().Length == AppSettings.GeneratedTokenLength && AppSettings.GeneratedTokenLength <= 6, "il token generato è corto");
    Check(Enumerable.Range(0, 200).Select(_ => AppSettings.NewToken()).Distinct().Count() > 190, "i token generati sono diversi");
    Check(AppSettings.ValidateToken("abc", true) != null && AppSettings.ValidateToken("abcd", true) == null && AppSettings.ValidateToken("pippo", true) == null, "token personalizzato: minimo 4 caratteri");
    Check(AppSettings.ValidateToken("con spazio", true) != null && AppSettings.ValidateToken("perché", true) != null, "token con spazi o accenti rifiutato");
    // un token personalizzato corto funziona davvero end-to-end
    using var s3 = new SyncServer(master, IPAddress.Loopback, 0, "Ab-1", ex, new HashCache(null), log);
    s3.Start();
    using var c3 = new RemoteClient("127.0.0.1", s3.Port, "Ab-1");
    Check(await c3.PingAsync(default) != "", "token personalizzato di 4 caratteri accettato dal server");
}

Console.WriteLine("== rete");
{
    var nics = NetworkHelper.GetCandidates(true);
    Console.WriteLine("      interfacce viste: " + string.Join(" ; ", nics.Select(n => n.Display)));
    var fake = new List<NicInfo> {
        new() { Name="Ethernet", Description="Intel", Ip=IPAddress.Parse("192.168.1.20"), Mask=IPAddress.Parse("255.255.255.0"), IsPrivate=true, HasGateway=true },
        new() { Name="Cisco AnyConnect", Description="Cisco AnyConnect Secure Mobility Client Virtual Miniport", Ip=IPAddress.Parse("10.8.0.5"), Mask=IPAddress.Parse("255.255.255.0"), IsPrivate=true, Suspect=true, HasGateway=true },
    };
    Check(NetworkHelper.Pick(fake, null)!.Name == "Ethernet", "senza indizi sceglie la LAN e non la VPN");
    Check(NetworkHelper.Pick(fake, IPAddress.Parse("10.8.0.9"))!.Name == "Cisco AnyConnect", "con IP peer sulla subnet VPN sceglie quella (scelta esplicita)");
    Check(NetworkHelper.Pick(fake, IPAddress.Parse("192.168.1.50"))!.Name == "Ethernet", "con IP peer LAN sceglie la LAN");
    Check(NetworkHelper.Broadcast(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0")).ToString() == "192.168.1.255", "indirizzo di broadcast");
}

Console.WriteLine("== ricerca automatica (UDP)");
{
    var lo = new NicInfo { Name = "lo", Ip = IPAddress.Loopback, Mask = IPAddress.Parse("255.0.0.0"), IsPrivate = false };
    using var resp = new Discovery.Responder(lo, port, "TEST-MASTER", log, 48999);
    var found = await Discovery.FindAsync(TimeSpan.FromSeconds(1.5), 48999, new[] { lo });
    Check(found.Count == 1 && found[0].Port == port && found[0].Name == "TEST-MASTER", "il master viene trovato via broadcast: " + string.Join(",", found));
}


Console.WriteLine("== Compatta (analisi, zip verificato, ripristino)");
{
    var cr = Path.Combine(tmp, "compat"); Directory.CreateDirectory(cr);
    void Mk(string rel, int size, byte seed = 1)
    {
        var f = Path.Combine(cr, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        var b = new byte[size]; for (int i = 0; i < size; i++) b[i] = (byte)(i * 7 + seed);
        File.WriteAllBytes(f, b);
        File.SetLastWriteTimeUtc(f, new DateTime(2021, 5, 17, 10, 20, 30, DateTimeKind.Utc));
    }
    for (int i = 0; i < 10; i++) Mk($"rootsmall{i}.txt", 100 + i);                 // la radice non si zippa mai
    for (int i = 0; i < 6; i++) Mk($"a/f{i}.txt", 200 + i, (byte)i);                // 6 piccoli: candidata
    Mk("a/sub/deep/x.bin", 5000); Directory.CreateDirectory(Path.Combine(cr, "a", "vuota"));
    for (int i = 0; i < 5; i++) Mk($"b/f{i}.txt", 100);                             // esattamente N: NO
    for (int i = 0; i < 8; i++) Mk($"c/f{i}.txt", 10 * 1024);                       // esattamente K: NO
    for (int i = 0; i < 8; i++) Mk($"h/z{i}.zip", 50);                              // gli zip non contano
    for (int gi = 0; gi < 6; gi++) for (int i = 0; i < 6; i++) Mk($"g/s{gi}/f{i}.txt", 150 + i, (byte)gi); // accorpamento
    for (int i = 0; i < 2; i++) for (int j = 0; j < 6; j++) Mk($"e/s{i}/f{j}.txt", 90 + j);           // solo 2 zip: nessun accorpamento
    for (int i = 0; i < 7; i++) Mk($"big/f{i}.txt", 120); Mk("big/grosso.dat", 3 * 1024 * 1024);
    for (int i = 0; i < 6; i++) Mk($"è ü/file {i} ß.txt", 130 + i);                 // unicode e spazi
    Mk("p1/node_modules/x/a.js", 50); Mk("p1/node_modules/b.js", 60); Mk("p1/node_modules/big.bin", 3 * 1024 * 1024); Mk("p1/src/main.js", 70); // sempre zippata, anche oltre i limiti
    Mk("p2/a/node_modules/x.js", 40);                                                                                                           // anche con un solo file
    Directory.CreateDirectory(Path.Combine(cr, "p3", "node_modules"));                                                                          // vuota: ignorata
    Directory.CreateDirectory(Path.Combine(cr, ".git"));
    var ro = Path.Combine(cr, "a", "f0.txt"); File.SetAttributes(ro, FileAttributes.ReadOnly);

    string Snapshot(string dir) => string.Join("|", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal)
        .Select(f => Path.GetRelativePath(dir, f) + ":" + new FileInfo(f).Length + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));
    var before = Snapshot(cr);

    var opt = new CompactOptions { MaxFileBytes = 1024 * 1024 };
    var bigMtime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    Directory.SetLastWriteTimeUtc(Path.Combine(cr, "big"), bigMtime);
    var cplan = CompactAnalyzer.Analyze(cr, opt, null, _ => { }, default);
    var paths = cplan.Items.Select(i => i.Path).ToList();
    Check(paths.Contains("a") && paths.Contains("è ü") && paths.Contains("g"), "candidate: a, 'è ü', g (accorpata): " + string.Join(",", paths));
    Check(!paths.Contains("b"), "esattamente N file piccoli: non candidata (serve PIÙ di N)");
    Check(!paths.Contains("c"), "file di esattamente K: non piccoli");
    Check(!paths.Contains("h"), "gli .zip non contano come file piccoli");
    Check(!paths.Contains("e") && !paths.Contains(""), "2 zip soltanto: nessun accorpamento; la radice non è mai zippata");
    var gm = cplan.Items.First(i => i.Path == "g");
    Check(gm.IsMerged && gm.Children.Count == 6 && !paths.Any(x => x.StartsWith("g/")), "g accorpata: 6 sottocartelle sostituite da un solo zip");
    Check(gm.Note.Contains("Accorpamento"), "l'accorpamento ha una spiegazione");
    var bigItem = cplan.Items.First(i => i.Path == "big");
    Check(bigItem.Kind == CompactKind.SmallFiles && bigItem.Status == CompactStatus.Candidate && bigItem.Reason!.Contains("file di") && bigItem.TotalFiles == 7 && bigItem.Note.Contains("solo i 7"),
        "cartella con file oltre il limite: non zippabile per intero, si raggruppano solo i 7 file piccoli (con motivo)");
    var noPartial = CompactAnalyzer.Analyze(cr, new CompactOptions { MaxFileBytes = 1024 * 1024, PartialPacks = false }, null, _ => { }, default);
    Check(noPartial.Items.First(i => i.Path == "big").Status == CompactStatus.Skipped, "con l'opzione disattivata la cartella resta non idonea");
    Check(!cplan.Items.Any(i => i.Path.StartsWith(".git")), ".git ignorata");
    var nm = cplan.Items.FirstOrDefault(i => i.Path == "p1/node_modules");
    Check(nm != null && nm.Forced && nm.Status == CompactStatus.Candidate && nm.TotalFiles == 3 && nm.Note.Contains("sempre zippate"), "node_modules: zippata sempre, anche con meno di N file e oltre il limite di dimensione");
    Check(!cplan.Items.Any(i => i.Path.StartsWith("p1/node_modules/")), "node_modules: non si entra al suo interno");
    Check(cplan.Items.Any(i => i.Path == "p2/a/node_modules") && !cplan.Items.Any(i => i.Path == "p3/node_modules"), "node_modules con un solo file zippata; vuota ignorata");
    long realFiles = Directory.EnumerateFiles(cr, "*", SearchOption.AllDirectories).LongCount();
    Check(cplan.ScannedFiles == realFiles, $"totale file analizzati corretto ({cplan.ScannedFiles} su {realFiles})");
    Check(cplan.FilesAfter == realFiles - cplan.SelectedFiles + cplan.SelectedCount && cplan.FilesAfter < realFiles, "stima dei file dopo la compattazione");

    // deselezionando l'accorpamento si torna agli zip separati
    gm.Selected = false;
    Check(cplan.SelectedCandidates.Count(i => i.Path.StartsWith("g/")) == 6, "accorpamento deselezionato: restano i 6 zip separati");
    gm.Selected = true;

    var rep = CompactPacker.Simulate(cplan);
    Check(Snapshot(cr) == before && !Directory.EnumerateFiles(cr, "*.zip", SearchOption.AllDirectories).Any(f => !f.Contains(Path.DirectorySeparatorChar + "h" + Path.DirectorySeparatorChar)) && rep.Lines.Count > 0, "anteprima: nessuna modifica su disco");

    File.WriteAllText(Path.Combine(cr, "a.zip"), "occupato"); // collisione nome zip
    var cres = CompactPacker.Pack(cplan, null, _ => { }, default);
    Check(cres.Failed == 0 && cres.Packed == cplan.SelectedCount && cres.Leftover == 0, $"compattazione riuscita senza errori ({cres.Packed} zip)");
    Check(!Directory.Exists(Path.Combine(cr, "a")) && File.Exists(Path.Combine(cr, "a (2).zip")), "cartella originale rimossa; zip con nome alternativo");
    Check(File.ReadAllText(Path.Combine(cr, "a.zip")) == "occupato", "lo zip preesistente non è stato sovrascritto");
    Check(Directory.Exists(Path.Combine(cr, "b")) && Directory.Exists(Path.Combine(cr, "c")) && Directory.Exists(Path.Combine(cr, "big")), "cartelle non candidate intatte");
    Check(!Directory.Exists(Path.Combine(cr, "g")) && File.Exists(Path.Combine(cr, "g.zip")) && !File.Exists(Path.Combine(cr, "s0.zip")), "g: un solo zip");
    Check(!Directory.EnumerateFiles(cr, "*.nstmp", SearchOption.AllDirectories).Any(), "nessun file temporaneo residuo");
    Check(File.Exists(Path.Combine(cr, "p1", "node_modules.zip")) && !Directory.Exists(Path.Combine(cr, "p1", "node_modules")) && File.Exists(Path.Combine(cr, "p1", "src", "main.js")), "node_modules zippata e rimossa, il resto del progetto intatto");
    Check(File.Exists(Path.Combine(cr, "big", "_piccoli.zip")) && File.Exists(Path.Combine(cr, "big", "grosso.dat")) && !File.Exists(Path.Combine(cr, "big", "f0.txt")),
        "big: i file piccoli sono nello zip _piccoli.zip, il file grosso è rimasto al suo posto");
    Check(Directory.GetLastWriteTimeUtc(Path.Combine(cr, "big")) == bigMtime, "big: data della cartella invariata");

    // ripristino
    File.Delete(Path.Combine(cr, "a.zip")); // lo zip "occupato" non è di NetSynq e va ignorato comunque
    var rit = CompactRestorer.Analyze(cr, default, _ => { });
    Check(rit.Count == cplan.SelectedCount && rit.All(r => !r.TargetExists), $"ripristino: {rit.Count} zip NetSynq riconosciuti, 'h/*.zip' ignorati");
    var rsim = CompactRestorer.Simulate(rit);
    Check(rsim.Lines.Count == rit.Count && Directory.GetFiles(cr, "*.zip", SearchOption.AllDirectories).Length == rit.Count + 8, "anteprima ripristino: nulla modificato");
    var rr = CompactRestorer.Restore(rit, true, null, _ => { }, default);
    Check(rr.Failed == 0 && rr.Restored == rit.Count, "ripristino riuscito");
    Check(Snapshot(cr) == before, "dopo compattazione + ripristino: contenuto identico byte per byte (nessun file perso)");
    var f0 = new FileInfo(ro);
    Check(f0.LastWriteTimeUtc == new DateTime(2021, 5, 17, 10, 20, 30, DateTimeKind.Utc) && (f0.Attributes & FileAttributes.ReadOnly) != 0, "date e attributi ripristinati");
    Check(Directory.Exists(Path.Combine(cr, "a", "vuota")), "cartelle vuote ripristinate");
    Check(!Directory.EnumerateFiles(cr, "*.zip", SearchOption.AllDirectories).Any(f => !f.Contains(Path.DirectorySeparatorChar + "h" + Path.DirectorySeparatorChar)), "zip NetSynq rimossi dopo il ripristino");

    // sicurezza: modifica durante l'archiviazione => nessuna cancellazione
    var s1 = CompactAnalyzer.Walk(Path.Combine(cr, "a"), "a", new ExcludeMatcher(""), default);
    Mk("a/nuovo.txt", 10);
    var s2 = CompactAnalyzer.Walk(Path.Combine(cr, "a"), "a", new ExcludeMatcher(""), default);
    Check(CompactPacker.Differences(s1, s2) != null && CompactPacker.Differences(s1, s1) == null, "il confronto prima/dopo rileva un file nuovo");

    // zip-slip: manifest con percorso fuori cartella
    var evil = Path.Combine(cr, "evil.zip");
    using (var fs = File.Create(evil)) using (var za = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
    {
        var m = new ZipManifest { Root = "evil" };
        m.Files.Add(new ZipManifestFile { Path = "../../fuori.txt", Size = 1, Sha256 = "00" });
        using (var w = za.CreateEntry(CompactPacker.ManifestName).Open()) System.Text.Json.JsonSerializer.Serialize(w, m);
        za.CreateEntry("evil/../../fuori.txt");
    }
    var evr = CompactRestorer.Analyze(cr, default, _ => { }).Where(r => r.ZipPath == evil).ToList();
    var er = CompactRestorer.Restore(evr, true, null, _ => { }, default);
    Check(er.Failed == 1 && File.Exists(evil) && !File.Exists(Path.Combine(cr, "..", "fuori.txt")) && !Directory.Exists(Path.Combine(cr, "evil")), "zip-slip rifiutato, zip conservato, nessun residuo");

    // se il target esiste già: saltato
    CompactPacker.Pack(CompactAnalyzer.Analyze(cr, new CompactOptions { MaxFileBytes = 1024 * 1024 }, null, _ => { }, default), null, _ => { }, default);
    Directory.CreateDirectory(Path.Combine(cr, "g"));
    var ex1 = CompactRestorer.Analyze(cr, default, _ => { }).Where(r => r.Target.EndsWith(Path.DirectorySeparatorChar + "g")).ToList();
    var er2 = ex1.Count == 1 ? CompactRestorer.Restore(ex1, true, null, _ => { }, default) : null;
    Check(er2 != null && er2.Failed == 1 && File.Exists(Path.Combine(cr, "g.zip")), "target già esistente: ripristino saltato, zip conservato");

    // ---- cartelle "profiles": una zip per ogni sottocartella, senza limiti, senza scendere
    var pr = Path.Combine(tmp, "profs"); Directory.CreateDirectory(pr);
    void Mp(string root, string rel, int size, bool random = false)
    {
        var f = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        var b = new byte[size];
        if (random) new Random(size).NextBytes(b); else for (int i = 0; i < size; i++) b[i] = (byte)(i * 3 + 1);
        File.WriteAllBytes(f, b);
        File.SetLastWriteTimeUtc(f, new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc));
    }
    Mp(pr, "x/profiles/alice/a.txt", 10); Mp(pr, "x/profiles/alice/big.bin", 3 * 1024 * 1024);   // file oltre il limite: non conta
    for (int i = 0; i < 8; i++) Mp(pr, $"x/profiles/bob/f{i}.txt", 100);
    Mp(pr, "x/profiles/carol/d1/d2/z.txt", 5);
    Directory.CreateDirectory(Path.Combine(pr, "x", "profiles", "vuota"));
    for (int i = 0; i < 7; i++) Mp(pr, $"x/profiles/loose{i}.txt", 50);
    for (int i = 0; i < 8; i++) Mp(pr, $"y/Altro/f{i}.txt", 60);                                  // analisi normale altrove
    string Snap2(string dir) => string.Join("|", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal)
        .Select(f => Path.GetRelativePath(dir, f) + ":" + new FileInfo(f).Length + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));
    var pbefore = Snap2(pr);
    var popt = new CompactOptions { MaxFileBytes = 1024 * 1024, MaxFolderBytes = 1024 * 1024 };
    var pplan = CompactAnalyzer.Analyze(pr, popt, null, _ => { }, default);
    var pp = pplan.Items.Select(i => i.Path).ToList();
    Check(new[] { "x/profiles/alice", "x/profiles/bob", "x/profiles/carol" }.All(pp.Contains), "profiles: una zip per ogni sottocartella: " + string.Join(",", pp));
    Check(pplan.Items.Where(i => i.Path.StartsWith("x/profiles/")).All(i => i.Forced && i.Status == CompactStatus.Candidate && i.Note.Contains("profiles")),
        "profiles: sottocartelle zippate anche oltre i limiti di dimensione, con spiegazione");
    Check(!pp.Any(x => x.StartsWith("x/profiles/") && x.Count(ch => ch == '/') > 2) && !pp.Contains("x/profiles/vuota"), "profiles: non si scende nelle sottocartelle; le vuote sono ignorate");
    Check(!pplan.Items.Any(i => i.Path == "x/profiles" && i.Kind == CompactKind.Folder) && !pp.Contains("x"), "profiles: la cartella stessa non viene zippata né accorpata");
    var ploose = pplan.Items.FirstOrDefault(i => i.Path == "x/profiles" && i.Kind == CompactKind.SmallFiles);
    Check(ploose != null && ploose.TotalFiles == 7 && ploose.TotalBytes == 350, "profiles: i 7 file piccoli diretti vanno in _piccoli.zip (con la loro dimensione)");
    Check(pp.Contains("y/Altro"), "fuori da profiles vale l'analisi normale");
    var pnoPart = CompactAnalyzer.Analyze(pr, new CompactOptions { MaxFileBytes = 1024 * 1024, MaxFolderBytes = 1024 * 1024, PartialPacks = false }, null, _ => { }, default);
    Check(!pnoPart.Items.Any(i => i.Path == "x/profiles"), "senza raggruppamento dei piccoli, profiles non produce alcuna voce");
    var pnone = CompactAnalyzer.Analyze(pr, new CompactOptions { ZipSubfolders = "" }, null, _ => { }, default);
    Check(pnone.Items.Any(i => i.Path == "x/profiles" && i.Kind == CompactKind.Folder || i.Path == "x/profiles/bob") == true, "con l'elenco vuoto torna il comportamento normale");
    var pres = CompactPacker.Pack(pplan, null, _ => { }, default);
    Check(pres.Failed == 0 && File.Exists(Path.Combine(pr, "x", "profiles", "alice.zip")) && File.Exists(Path.Combine(pr, "x", "profiles", "bob.zip")) &&
          File.Exists(Path.Combine(pr, "x", "profiles", "carol.zip")) && File.Exists(Path.Combine(pr, "x", "profiles", "_piccoli.zip")) &&
          !Directory.Exists(Path.Combine(pr, "x", "profiles", "alice")) && !File.Exists(Path.Combine(pr, "x", "profiles", "loose0.txt")), "profiles: zip create e originali rimossi");
    var prit = CompactRestorer.Analyze(pr, default, _ => { });
    var prr = CompactRestorer.Restore(prit, true, null, _ => { }, default);
    Check(prit.Count == 5 && prr.Failed == 0 && Snap2(pr) == pbefore, "profiles: ripristino identico byte per byte");

    // ---- zip oltre il limite: diviso in volumi a byte (nome.zip.001, .002…)
    var vr = Path.Combine(tmp, "vols"); Directory.CreateDirectory(vr);
    void MkBig()
    {
        Mp(vr, "w/big/a.bin", 120_000, true); Mp(vr, "w/big/b.bin", 30_001, true); Mp(vr, "w/big/sotto/c.bin", 7_000, true);
        for (int i = 0; i < 6; i++) Mp(vr, $"w/big/s{i}.txt", 100);
    }
    MkBig();
    for (int i = 0; i < 8; i++) Mp(vr, $"w/vz/q{i}.zip.001", 50);                                  // i volumi non contano come file piccoli
    var vopt = new CompactOptions { Compression = CompactCompression.None };
    var vbefore = Snap2(vr);
    var wdir = Path.Combine(vr, "w");
    File.WriteAllText(Path.Combine(wdir, "big.zip.001"), "occupato");                               // collisione con un volume esistente
    CompactPacker.VolumeBytes = 50_000;
    try
    {
        var vplan = CompactAnalyzer.Analyze(vr, vopt, null, _ => { }, default);
        Check(vplan.Items.Any(i => i.Path == "w/big") && !vplan.Items.Any(i => i.Path == "w/vz"), "volumi .zip.NNN non contati come file piccoli");
        var vres = CompactPacker.Pack(vplan, null, _ => { }, default);
        var vols = Directory.GetFiles(wdir, "big (2).zip.*").Where(f => !f.EndsWith(".nstmp")).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var first = vres.Items.FirstOrDefault(i => i.Path == "w/big");
        Check(vres.Failed == 0 && first != null && first.Volumes == vols.Length && vols.Length >= 4 && vols[0].EndsWith("big (2).zip.001"), $"zip oltre il limite diviso in volumi ({vols.Length}), nome senza collisioni");
        Check(File.ReadAllText(Path.Combine(wdir, "big.zip.001")) == "occupato", "il volume preesistente non è stato toccato");
        Check(vols.Take(vols.Length - 1).All(f => new FileInfo(f).Length == 50_000) && vols.All(f => new FileInfo(f).Length <= 50_000) && first!.ZipBytes == vols.Sum(f => new FileInfo(f).Length), "volumi della dimensione massima, ultimo più piccolo");
        Check(!Directory.Exists(Path.Combine(wdir, "big")) && !Directory.EnumerateFiles(vr, "*.nstmp", SearchOption.AllDirectories).Any() && !File.Exists(Path.Combine(wdir, "big (2).zip")), "originale rimosso, nessun residuo temporaneo né zip unico");
        // la concatenazione dei volumi è uno zip valido, apribile con qualunque programma
        var joined = Path.Combine(tmp, "joined.zip");
        using (var o = File.Create(joined)) foreach (var v in vols) { using var i = File.OpenRead(v); i.CopyTo(o); }
        using (var za = System.IO.Compression.ZipFile.OpenRead(joined))
            Check(za.GetEntry("big/a.bin") != null && za.GetEntry(CompactPacker.ManifestName) != null, "volumi concatenati = zip standard valido");
        File.Delete(Path.Combine(wdir, "big.zip.001"));

        var vlog = new List<string>();
        var vit = CompactRestorer.Analyze(vr, default, vlog.Add);
        Check(vit.Count == 1 && vit[0].Volumes == vols.Length && vit[0].ZipPath.EndsWith(".zip.001"), "ripristino: l'archivio a volumi compare una sola volta");
        // volume mancante: nessun ripristino e volumi conservati
        var mid = vols[1]; var midBytes = File.ReadAllBytes(mid); File.Delete(mid);
        var vlog2 = new List<string>();
        Check(CompactRestorer.Analyze(vr, default, vlog2.Add).Count == 0 && vlog2.Any(l => l.Contains("volumi")), "volume mancante: archivio non ripristinabile, segnalato");
        File.WriteAllBytes(mid, midBytes);
        var vrr = CompactRestorer.Restore(CompactRestorer.Analyze(vr, default, _ => { }), true, null, _ => { }, default);
        Check(vrr.Failed == 0 && vrr.Restored == 1 && Snap2(vr) == vbefore.Replace("w/vz", "w/vz") && !Directory.EnumerateFiles(wdir, "big*.zip*").Any(), "ripristino da volumi: contenuto identico e volumi cancellati");

        // limite non superato: zip unico
        CompactPacker.VolumeBytes = 0xFFFFFFFFL;
        var vres2 = CompactPacker.Pack(CompactAnalyzer.Analyze(vr, vopt, null, _ => { }, default), null, _ => { }, default);
        Check(vres2.Failed == 0 && File.Exists(Path.Combine(wdir, "big.zip")) && !File.Exists(Path.Combine(wdir, "big.zip.001")) && vres2.Items.All(i => i.Volumes == 1), "sotto il limite resta un unico zip");
    }
    finally { CompactPacker.VolumeBytes = 0xFFFFFFFFL; }
}

Console.WriteLine("== Traduzioni (it/en)");
{
    var dit = I18n.Load("it"); var den = I18n.Load("en");
    Check(dit.Count > 300 && dit.Count == den.Count && !dit.Keys.Except(den.Keys).Any() && !den.Keys.Except(dit.Keys).Any(), $"it/en hanno le stesse chiavi ({dit.Count})");
    var phRx = new System.Text.RegularExpressions.Regex(@"\{(\d+)(?:[:,][^}]*)?\}");
    string Ph(string t) => string.Join(",", phRx.Matches(t).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));
    var badPh = dit.Keys.Where(k => Ph(dit[k]) != Ph(den[k])).ToList();
    Check(badPh.Count == 0, "segnaposto {n} uguali nelle due lingue" + (badPh.Count > 0 ? ": " + string.Join(",", badPh) : ""));
    Check(dit.Values.All(v => v.Length > 0 || true) && dit.Where(kv => kv.Value.Length == 0).All(kv => den[kv.Key].Length == 0), "nessun testo vuoto in una sola lingua");
    // ogni chiave usata nel codice esiste nel dizionario
    var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/NetSynq"));
    var keyRx = new System.Text.RegularExpressions.Regex("\"((?:app|srv|cli|cp|sim|diff|cmp|sync|net|file)\\.[A-Za-z0-9_.]+)\"");
    var used = new HashSet<string>();
    foreach (var f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")))
        foreach (System.Text.RegularExpressions.Match m in keyRx.Matches(File.ReadAllText(f))) used.Add(m.Groups[1].Value);
    var missing = used.Where(k => !dit.ContainsKey(k)).ToList();
    Check(used.Count > 300 && missing.Count == 0, $"tutte le {used.Count} chiavi usate nel codice esistono" + (missing.Count > 0 ? ": " + string.Join(",", missing.Take(10)) : ""));
    var unused = dit.Keys.Where(k => !used.Contains(k)).ToList();
    Check(unused.Count == 0, "nessuna chiave inutilizzata nel dizionario" + (unused.Count > 0 ? ": " + string.Join(",", unused.Take(10)) : ""));

    var itText = I18n.Tr("app.log.title");
    I18n.SetLanguage("en");
    Check(I18n.Lang == "en" && I18n.Tr("app.log.title") != itText && I18n.Tr("chiave.inesistente") == "chiave.inesistente", "cambio lingua e chiave mancante");
    int changed = 0; I18n.Changed += () => changed++;
    I18n.SetLanguage("it"); I18n.SetLanguage("it"); I18n.SetLanguage("xx");
    Check(I18n.Lang == "it" && changed == 1 && I18n.Tr("app.log.title") == itText, "ritorno all'italiano, lingua sconosciuta = italiano, evento una volta");
    Check(I18n.Tr("cmp.pack.log_ok", "a", "b", 3, "1 KB", "2 KB") != "cmp.pack.log_ok" || true, "formattazione con argomenti non solleva eccezioni");
    // gli stessi messaggi del motore escono nella lingua scelta
    var rows = new[] { "it", "en" }.Select(l => { I18n.SetLanguage(l); return CompactPacker.Simulate(new CompactPlan { Root = "x" }).SummaryText; }).ToArray();
    I18n.SetLanguage("it");
    Check(rows[0] != rows[1] && rows[0].Contains("SIMULAZIONE") && rows[1].Contains("SIMULATION"), "i messaggi del motore seguono la lingua");
    // un dizionario esterno sovrascrive i testi incorporati
    var langDir = Path.Combine(AppContext.BaseDirectory, "lang"); Directory.CreateDirectory(langDir);
    File.WriteAllText(Path.Combine(langDir, "it.json"), "{ \"app.log.title\": \"REGISTRO-TEST\" /* commento */ }");
    Check(I18n.Load("it")["app.log.title"] == "REGISTRO-TEST" && I18n.Load("it").Count == dit.Count, "dizionario esterno (cartella lang) con precedenza, il resto resta");
    File.Delete(Path.Combine(langDir, "it.json")); try { Directory.Delete(langDir); } catch { }
}

server.Stop();
try { Directory.Delete(tmp, true); } catch { }
Console.WriteLine();
Console.WriteLine(failures == 0 ? $"TUTTI I {checks} CONTROLLI SUPERATI" : $"{failures} CONTROLLI FALLITI su {checks}");
return failures == 0 ? 0 : 1;
