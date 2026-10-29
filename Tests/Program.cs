using FileTransit;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private static int passed;
    private static string root = "";
    // --ui-only skips the loopback FTP/SFTP checks when Tests/servers.py cannot run on this machine.
    private static bool uiOnly;
    [STAThread]
    private static void Main(string[] args)
    {
        uiOnly = args.Contains("--ui-only");
        root = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts"));
        ApplicationConfiguration.Initialize();
        using var context = new ApplicationContext();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ = Run(context); Application.Run(context);
    }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); passed++; }
    private static async Task Run(ApplicationContext context)
    {
        await Task.Yield();
        try
        {
            var config = JsonDocument.Parse(uiOnly ? """{"ftp":21,"sftp":22,"fingerprint":""}""" : File.ReadAllText(Path.Combine(root, "test-servers.json"))).RootElement;
            string fingerprint = config.GetProperty("fingerprint").GetString()!;
            var folder = Path.Combine(root, "test-results", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            var profile = new ServerProfile { Name = "Fixture", Host = "127.0.0.1", User = "portly-test", Password = "local-test-only", RememberPassword = true, Protocol = ServerProtocol.FTP, Port = config.GetProperty("ftp").GetInt32() };
            var store = new WorkspaceStore(folder);
            var state = new WorkspaceState { Profiles = [profile], Queue = [new TransferJob { Server = profile.Copy(), LocalPath = Path.Combine(folder, "persist.bin"), RemotePath = "/persist.bin", State = JobState.Running }] };
            store.Save(state); string json = File.ReadAllText(Path.Combine(folder, "workspace.json"));
            Check(!json.Contains(profile.Password), "Profile and queue passwords are encrypted at rest");
            var loaded = store.Load(); Check(loaded.Profiles[0].Password == profile.Password && loaded.Queue[0].State == JobState.Paused, "Profile round-trip; interrupted jobs restored paused");
            profile.RememberPassword = false; store.Save(state); Check(store.Load().Profiles[0].Password.Length == 0, "Do not remember password is respected");
            profile.RememberPassword = true; store.Save(state); string legacyJson = File.ReadAllText(Path.Combine(folder, "workspace.json")); string importTarget = Path.Combine(folder, "imported");
            Check(WorkspaceStore.ImportLegacy(importTarget, folder) && File.ReadAllText(Path.Combine(folder, "workspace.json")) == legacyJson, "Legacy workspace imported without touching the original file");
            var importedState = new WorkspaceStore(importTarget).Load();
            Check(importedState.Profiles.Single().Password == profile.Password && importedState.Queue.Count == 0, "Import keeps profiles and decryptable passwords, leaves the queue behind");
            Check(!WorkspaceStore.ImportLegacy(importTarget, folder), "Import never overwrites an existing workspace");
            string renamedTarget = Path.Combine(folder, "renamed");
            Check(WorkspaceStore.ImportLegacy(renamedTarget, folder, keepQueue: true) && new WorkspaceStore(renamedTarget).Load().Queue.Count == state.Queue.Count, "Import from this app's previous folder keeps its own queue");
            foreach (string name in new[] { "..", "../escape", "C:\\escape", "name:ads", "CON.txt", "trailing.", "slash\\escape" })
            { bool rejected = false; try { RemotePaths.LocalChild(folder, name); } catch (IOException) { rejected = true; } Check(rejected, "Unsafe remote filename rejected: " + name); }
            if (!uiOnly) foreach (var protocol in new[] { ServerProtocol.FTP, ServerProtocol.SFTP })
            {
                profile.Protocol = protocol; profile.Port = config.GetProperty(protocol == ServerProtocol.FTP ? "ftp" : "sftp").GetInt32();
                using var session = new RemoteSession(profile, (_, key) => key == fingerprint);
                await session.Connect(CancellationToken.None);
                var listing = await session.List("/", CancellationToken.None); var seed = listing.Single(e => e.Name == "seed.bin");
                Check(session.IsConnected && seed.Size == 4194304, protocol + " connect/list");
                var listWatch = System.Diagnostics.Stopwatch.StartNew(); var many = await session.List("/many", CancellationToken.None);
                Check(many.Length == 5000 && many.All(e => !e.IsDirectory), $"{protocol} lists 5,000-entry folder ({listWatch.ElapsedMilliseconds} ms)");
                string local = Path.Combine(folder, protocol + "-download.bin");
                var download = new TransferJob { Server = profile.Copy(), LocalPath = local, RemotePath = "/seed.bin", Size = seed.Size, Modified = seed.Modified, Conflict = ConflictPolicy.Overwrite };
                using (var cancel = new CancellationTokenSource(700))
                {
                    bool cancelled = false;
                    try { await session.Transfer(download, new TransferSettings { DownloadLimit = 128 }, new Progress<TransferProgressInfo>(), cancel.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && !File.Exists(local) && File.Exists(download.PartialPath), protocol + " cancellation preserves staging and no incomplete destination");
                }
                using var resumed = new RemoteSession(profile, (_, key) => key == fingerprint); await resumed.Connect(CancellationToken.None);
                Check(await resumed.Transfer(download, new(), new Progress<TransferProgressInfo>(), CancellationToken.None), protocol + " resume staged download");
                var expected = Enumerable.Range(0, 4194304).Select(i => (byte)(i % 256)).ToArray();
                Check(SHA256.HashData(File.ReadAllBytes(local)).SequenceEqual(SHA256.HashData(expected)), protocol + " downloaded bytes match");
                download.Conflict = ConflictPolicy.Skip; Check(!await resumed.Transfer(download, new(), new Progress<TransferProgressInfo>(), CancellationToken.None), protocol + " existing destination skipped");
                string remoteFolder = "/case-" + Guid.NewGuid().ToString("N");
                var upload = new TransferJob { Server = profile.Copy(), LocalPath = local, RemotePath = remoteFolder + "/child/upload.bin", Upload = true, Conflict = ConflictPolicy.Overwrite };
                Check(await resumed.Transfer(upload, new(), new Progress<TransferProgressInfo>(), CancellationToken.None), protocol + " upload creates parent directories");
                var interruptedUpload = new TransferJob { Server = profile.Copy(), LocalPath = local, RemotePath = remoteFolder + "/interrupted.bin", Upload = true, Conflict = ConflictPolicy.Skip };
                using (var cancelUpload = new CancellationTokenSource(700))
                {
                    bool interrupted = false;
                    try { await resumed.Transfer(interruptedUpload, new TransferSettings { UploadLimit = 128 }, new Progress<TransferProgressInfo>(), cancelUpload.Token); }
                    catch (OperationCanceledException) { interrupted = true; }
                    Check(interrupted && interruptedUpload.UploadStarted, protocol + " interrupted upload tracked");
                }
                using var retrySession = new RemoteSession(profile, (_, key) => key == fingerprint); await retrySession.Connect(CancellationToken.None);
                Check(await retrySession.Transfer(interruptedUpload, new(), new Progress<TransferProgressInfo>(), CancellationToken.None), protocol + " retries own partial upload under Skip policy");
                var uploadedEntry = (await retrySession.List(remoteFolder, CancellationToken.None)).Single(e => e.Name == "interrupted.bin");
                Check(uploadedEntry.Size == seed.Size, protocol + " retried upload has full size");
                var roundtrip = new TransferJob { Server = profile.Copy(), LocalPath = local + ".roundtrip", RemotePath = upload.RemotePath, Conflict = ConflictPolicy.Overwrite };
                await resumed.Transfer(roundtrip, new(), new Progress<TransferProgressInfo>(), CancellationToken.None);
                Check(SHA256.HashData(File.ReadAllBytes(roundtrip.LocalPath)).SequenceEqual(SHA256.HashData(expected)), protocol + " uploaded bytes round-trip");
                await resumed.Rename(upload.RemotePath, remoteFolder + "/child/renamed.bin", CancellationToken.None);
                Check(await resumed.Exists(remoteFolder + "/child/renamed.bin", CancellationToken.None), protocol + " rename");
                await resumed.Delete(new RemoteEntry(remoteFolder[1..], remoteFolder, true, false, 0, DateTime.MinValue), CancellationToken.None);
                Check(!await resumed.Exists(remoteFolder, CancellationToken.None), protocol + " recursive delete fixture");
            }
            if (!uiOnly) using (var rejected = new RemoteSession(profile, (_, _) => false))
            { bool blocked = false; try { await rejected.Connect(CancellationToken.None); } catch (IOException) { blocked = true; } Check(blocked, "SFTP rejects untrusted host key"); }
            await QueueTests(profile, folder);
            profile.Host = "127.0.0.1";
            if (!uiOnly)
            {
                var mixedJobs = new List<TransferJob>();
                for (int i = 0; i < 4; i++)
                {
                    var server = profile.Copy(); server.Protocol = i % 2 == 0 ? ServerProtocol.FTP : ServerProtocol.SFTP; server.Port = config.GetProperty(i % 2 == 0 ? "ftp" : "sftp").GetInt32();
                    mixedJobs.Add(new TransferJob { Server = server, LocalPath = Path.Combine(folder, "mixed-" + i), RemotePath = "/seed.bin" });
                }
                var mixed = new TransferQueue(mixedJobs, new TransferSettings { Parallel = 4 }, p => new RemoteSession(p, (_, key) => key == fingerprint));
                mixed.Start(); await Until(() => !mixed.IsRunning && mixed.ActiveCount == 0);
                Check(mixed.Jobs.All(j => j.State == JobState.Completed && new FileInfo(j.LocalPath).Length == 4194304), "Real FTP and SFTP jobs transfer concurrently to isolated destinations");
            }
            using (var form = new MainForm(Path.Combine(folder, "ui-state")))
            {
                form.CreateControl();
                var fields = typeof(MainForm).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Control Get(string name) => (Control)fields.Single(f => f.Name == name).GetValue(form)!;
                foreach (var size in new[] { new Size(1060, 720), new Size(1400, 930), new Size(1920, 1080) })
                {
                    form.Size = size; form.PerformLayout();
                    Check(Get("connect").Height == Get("host").Height && Get("disconnect").Height == Get("host").Height, $"Connection button heights match text boxes at {size.Width}x{size.Height}");
                    Check(Get("connect").Top == Get("host").Top && Get("disconnect").Top == Get("host").Top, $"Connection row top alignment at {size.Width}x{size.Height}");
                }
                form.StartPosition = FormStartPosition.Manual; form.Opacity = 0; form.ShowInTaskbar = false; form.Show();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var remoteList = (ListView)Get("files");
                var big = Enumerable.Range(0, 50000).Select(i => new RemoteEntry($"file-{i:00000}.txt", $"/big/file-{i:00000}.txt", i % 1000 == 0, false, i, DateTime.MinValue)).ToArray();
                var renderWatch = System.Diagnostics.Stopwatch.StartNew(); typeof(MainForm).GetMethod("RenderRemote", flags)!.Invoke(form, [big]); Application.DoEvents(); renderWatch.Stop();
                Check(remoteList.VirtualMode && remoteList.VirtualListSize == 50000 && renderWatch.ElapsedMilliseconds < 2000, $"50,000 remote entries render as virtual list ({renderWatch.ElapsedMilliseconds} ms)");
                RemoteEntry[] Picked() => (RemoteEntry[])typeof(MainForm).GetMethod("SelectedRemote", flags)!.Invoke(form, null)!;
                remoteList.SelectedIndices.Add(3); remoteList.SelectedIndices.Add(49999);
                Check(Picked().Select(e => e.Name).Order().SequenceEqual(["file-03000.txt", "file-49999.txt"]), "Virtual list maps selection to folder-first sorted entries");
                var columnClick = typeof(ListView).GetMethod("OnColumnClick", flags)!;
                columnClick.Invoke(remoteList, [new ColumnClickEventArgs(0)]); columnClick.Invoke(remoteList, [new ColumnClickEventArgs(0)]);
                Check(remoteList.Items[0].Text == "file-49999.txt" && Picked().Select(e => e.Name).Order().SequenceEqual(["file-03000.txt", "file-49999.txt"]), "Column sort reorders virtual list and keeps selection");
                var typed = new[] { "photo.jpg", "report.pdf", "notes.txt", "noextension", "../evil.weird*ext" }.Select(n => new RemoteEntry(n, "/" + n, false, false, 1, DateTime.MinValue)).ToArray();
                typeof(MainForm).GetMethod("RenderRemote", flags)!.Invoke(form, [typed]);
                ListViewItem Row(string name) => Enumerable.Range(0, remoteList.VirtualListSize).Select(i => remoteList.Items[i]).Single(r => r.Text == name);
                Check(Row("photo.jpg").ImageIndex > 1 && Row("photo.jpg").ImageIndex != Row("notes.txt").ImageIndex && Row("photo.jpg").SubItems[1].Text is { Length: > 0 } t && t != "Dosya", $"File type icon and shell type name by extension (jpg: {Row("photo.jpg").SubItems[1].Text})");
                Check(Row("noextension").ImageIndex == 1 && Row("noextension").SubItems[1].Text == "Dosya" && Row("../evil.weird*ext").ImageIndex == 1, "Files without a safe extension use the generic icon");
                var navChild = Directory.CreateDirectory(Path.Combine(folder, "nav-parent", "nav-child")).FullName;
                typeof(MainForm).GetMethod("LoadLocal", flags)!.Invoke(form, [navChild]); typeof(MainForm).GetMethod("GoUpLocal", flags)!.Invoke(form, null);
                var localList = (ListView)Get("localFiles"); var upButton = (Button)fields.Single(f => f.Name == "localUp").GetValue(form)!;
                Check(((TextBox)Get("localPath")).Text == Path.GetDirectoryName(navChild) && localList.SelectedIndices.Count == 1 && localList.Items[localList.SelectedIndices[0]].Text == "nav-child" && upButton.Enabled, "Up folder opens parent and reselects the folder just left");
                typeof(MainForm).GetMethod("LoadLocal", flags)!.Invoke(form, [Path.GetPathRoot(navChild)!]);
                Check(!upButton.Enabled, "Up folder disabled at drive root");
                object Field(string name) => fields.Single(f => f.Name == name).GetValue(form)!;
                var menuItems = ((ContextMenuStrip)Field("settingsMenu")).Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToArray();
                Check(menuItems.SequenceEqual(["Site yöneticisi…", "Aktarım ayarları…"]), "Settings menu holds site manager and transfer settings");
                Check(Get("saveProfile").Height == Get("host").Height && Get("saveProfile").Top == Get("host").Top && Get("saveProfile").Parent == Get("host").Parent, "Save profile button sits in the connection row at text box height");
                Check(!((ToolStrip)Field("queueTools")).Items.Cast<ToolStripItem>().Any(i => i.Text?.StartsWith("Ayarlar") == true), "Queue toolbar no longer has its own settings button");
                var formQueue = (TransferQueue)Field("queue"); var grid = (DataGridView)Field("queueGrid"); var filter = (ToolStripComboBox)Field("queueFilter");
                var bulkServer = new ServerProfile { Name = "Bulk", Host = "127.0.0.1", User = "portly-test", Password = "local-test-only", RememberPassword = true };
                var bulk = Enumerable.Range(0, 50000).Select(i => new TransferJob { Server = bulkServer.Copy(), LocalPath = Path.Combine(folder, "bulk", "f" + i), RemotePath = "/bulk/f" + i, State = JobState.Paused }).ToList();
                var addWatch = System.Diagnostics.Stopwatch.StartNew(); formQueue.Add(bulk); addWatch.Stop();
                Check(formQueue.Jobs.Count == 50000 && addWatch.ElapsedMilliseconds < 3000, $"50,000 jobs added to queue ({addWatch.ElapsedMilliseconds} ms)");
                var queueWatch = System.Diagnostics.Stopwatch.StartNew(); typeof(MainForm).GetMethod("RenderQueue", flags)!.Invoke(form, null); Application.DoEvents(); queueWatch.Stop();
                Check(grid.VirtualMode && grid.RowCount == 50000 && queueWatch.ElapsedMilliseconds < 2000, $"50,000-job queue renders as virtual grid ({queueWatch.ElapsedMilliseconds} ms)");
                TransferJob[] PickedJobs() => (TransferJob[])typeof(MainForm).GetMethod("SelectedJobs", flags)!.Invoke(form, null)!;
                grid.ClearSelection(); grid.Rows[7].Selected = true;
                bulk[0].State = JobState.Completed; filter.SelectedIndex = 1;
                Check(grid.RowCount == 49999 && PickedJobs() is [var kept] && kept == bulk[7], "Queue filter change keeps selected job by identity");
                filter.SelectedIndex = 0;
                string savedPath = Path.Combine(folder, "ui-state", "workspace.json");
                int SavedJobs() { try { return JsonDocument.Parse(File.ReadAllText(savedPath)).RootElement.GetProperty("Queue").GetArrayLength(); } catch (IOException) { return -1; } catch (JsonException) { return -1; } }
                await Until(() => File.Exists(savedPath) && SavedJobs() == 50000);
                Check(!File.ReadAllText(savedPath).Contains(bulkServer.Password), "Large queue saved in background with encrypted passwords");
                form.Hide();
            }
            Console.WriteLine($"ALL {passed} CHECKS PASSED" + (uiOnly ? " (UI-only: loopback FTP/SFTP checks skipped)" : ""));
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        finally { context.ExitThread(); }
    }
    private static async Task QueueTests(ServerProfile profile, string folder)
    {
        var tracker = new Tracker();
        TransferJob Job(int i) => new() { Server = profile.Copy(), LocalPath = Path.Combine(folder, "queue-" + i), RemotePath = "/" + i };
        var queue = new TransferQueue([], new TransferSettings { AutoStart = false, Parallel = 1, Retries = 1, RetryDelay = 1 }, _ => new FakeSession(tracker));
        queue.Add([Job(1), Job(2), Job(3)]); queue.Start(); await Until(() => !queue.IsRunning && queue.ActiveCount == 0);
        Check(tracker.Maximum == 1 && tracker.Order.SequenceEqual(new[] { "/1", "/2", "/3" }), "Sequential queue preserves order");
        tracker.Maximum = 0; queue.Settings.Parallel = 3; queue.Add([Job(4), Job(5), Job(6)]); queue.Start(); await Until(() => !queue.IsRunning && queue.ActiveCount == 0);
        Check(tracker.Maximum == 3, "Parallel queue respects concurrency setting");
        tracker.FailOnce = true; var retry = Job(7); queue.Add([retry]); queue.Start(); await Until(() => !queue.IsRunning && queue.ActiveCount == 0);
        Check(retry.State == JobState.Completed && retry.Attempts == 2, "Automatic retry succeeds after transient failure");
        var paused = Job(8); queue.Add([paused]); queue.Start(); await Until(() => tracker.Active > 0); queue.Pause(); await Until(() => queue.ActiveCount == 0);
        Check(paused.State == JobState.Paused && !queue.IsRunning, "Pause stops running jobs without marking failed");
        queue.Start(); await Until(() => !queue.IsRunning && queue.ActiveCount == 0); Check(paused.State == JobState.Completed, "Paused queue resumes");
        var snapshot = Job(9); queue.Add([snapshot]); string original = snapshot.Server.Host; profile.Host = "changed.invalid";
        Check(snapshot.Server.Host == original, "Queued destination isolated from profile changes");
        queue.Cancel([snapshot]); Check(snapshot.State == JobState.Cancelled, "Cancel pending job");
        var restored = Job(10); restored.State = JobState.Paused; queue.Jobs.Add(restored); queue.Settings.AutoStart = true; queue.Add([Job(11)]);
        await Until(() => !queue.IsRunning && queue.ActiveCount == 0);
        Check(restored.State == JobState.Paused && queue.Jobs.Last().State == JobState.Completed, "Auto-start new jobs does not resume explicitly paused jobs");
    }
    private static async Task Until(Func<bool> predicate) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); while (!predicate()) await Task.Delay(20, timeout.Token); }
    private sealed class Tracker { public int Active, Maximum; public bool FailOnce; public List<string> Order = []; }
    private sealed class FakeSession(Tracker tracker) : IRemoteSession
    {
        public bool IsConnected => true;
        public Task Connect(CancellationToken token) => Task.CompletedTask;
        public Task<string> WorkingDirectory(CancellationToken token) => Task.FromResult("/");
        public Task<RemoteEntry[]> List(string path, CancellationToken token) => Task.FromResult(Array.Empty<RemoteEntry>());
        public Task<bool> Exists(string path, CancellationToken token) => Task.FromResult(false);
        public Task CreateDirectory(string path, CancellationToken token) => Task.CompletedTask;
        public Task Rename(string a, string b, CancellationToken token) => Task.CompletedTask;
        public Task Delete(RemoteEntry e, CancellationToken token) => Task.CompletedTask;
        public async Task<bool> Transfer(TransferJob job, TransferSettings settings, IProgress<TransferProgressInfo> progress, CancellationToken token)
        {
            tracker.Active++; tracker.Maximum = Math.Max(tracker.Maximum, tracker.Active);
            try { if (tracker.FailOnce) { tracker.FailOnce = false; throw new IOException("Simulated transient failure"); } await Task.Delay(150, token); tracker.Order.Add(job.RemotePath); return true; }
            finally { tracker.Active--; }
        }
        public void Dispose() { }
    }
}
