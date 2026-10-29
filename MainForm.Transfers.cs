namespace FileTransit;

public sealed partial class MainForm
{
    private async Task EnqueueUpload(string[]? paths = null)
    {
        var session = current ?? throw new InvalidOperationException("Önce sunucuya bağlanın.");
        paths ??= SelectedTags<FileSystemInfo>(localFiles).Select(x => x.FullName).ToArray();
        if (paths.Length == 0)
        {
            using var dialog = new OpenFileDialog { Multiselect = true, InitialDirectory = localDirectory, Title = "Kuyruğa eklenecek dosyalar" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return; paths = dialog.FileNames;
        }
        var batch = new List<TransferJob>();
        foreach (string source in paths) await AddLocal(source, RemotePaths.Join(currentPath, Path.GetFileName(source.TrimEnd('\\'))), session.Profile, batch, 0);
        CommitBatch(batch);
    }
    private async Task AddLocal(string source, string destination, ServerProfile server, List<TransferJob> batch, int depth)
    {
        Token.ThrowIfCancellationRequested(); CheckBatch(batch, depth);
        var attributes = File.GetAttributes(source);
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) { WriteLog("Bağlantı atlandı: " + source); return; }
        bool directory = attributes.HasFlag(FileAttributes.Directory);
        batch.Add(new TransferJob { Server = server.Copy(), Upload = true, Directory = directory, LocalPath = source, RemotePath = destination, Size = directory ? 0 : new FileInfo(source).Length, Conflict = workspace.Settings.Conflict });
        if (!directory) return;
        await Task.Yield();
        foreach (string child in Directory.EnumerateFileSystemEntries(source)) await AddLocal(child, RemotePaths.Join(destination, Path.GetFileName(child)), server, batch, depth + 1);
    }
    private async Task EnqueueDownload(RemoteEntry[]? entries = null)
    {
        var session = current ?? throw new InvalidOperationException("Önce sunucuya bağlanın.");
        entries ??= SelectedRemote(); if (entries.Length == 0) throw new InvalidOperationException("İndirilecek dosya veya klasörleri seçin.");
        if (!Directory.Exists(localDirectory)) throw new DirectoryNotFoundException("Sol panelde geçerli bir hedef klasör açın.");
        var batch = new List<TransferJob>();
        foreach (var entry in entries) await AddRemote(entry, localDirectory, session, batch, 0);
        CommitBatch(batch);
    }
    private async Task AddRemote(RemoteEntry entry, string parent, BrowserSession session, List<TransferJob> batch, int depth)
    {
        Token.ThrowIfCancellationRequested(); CheckBatch(batch, depth);
        if (entry.IsLink) { WriteLog("Sembolik bağlantı atlandı: " + entry.FullName); return; }
        var destination = RemotePaths.LocalChild(parent, entry.Name);
        batch.Add(new TransferJob { Server = session.Profile.Copy(), Upload = false, Directory = entry.IsDirectory, LocalPath = destination, RemotePath = entry.FullName, Size = entry.Size, Modified = entry.Modified, Conflict = workspace.Settings.Conflict });
        if (!entry.IsDirectory) return;
        foreach (var child in await session.Remote.List(entry.FullName, Token)) await AddRemote(child, destination, session, batch, depth + 1);
    }
    private static void CheckBatch(List<TransferJob> batch, int depth)
    {
        if (batch.Count >= 50000 || depth > 64) throw new IOException("Tek seferde en fazla 50.000 öğe ve 64 klasör derinliği desteklenir. Alt klasörleri ayrı ekleyin.");
    }
    private void CommitBatch(List<TransferJob> batch)
    {
        if (batch.GroupBy(j => j.TargetKey).Any(g => g.Count() > 1)) throw new IOException("Birden fazla kaynak aynı hedef adına dönüşüyor. Dosya adlarını veya seçiminizi düzenleyin.");
        if (workspace.Settings.Conflict == ConflictPolicy.Overwrite && batch.Count > 0 && MessageBox.Show(this, $"{batch.Count} öğe kuyruğa eklenecek. Mevcut hedef dosyaların üzerine yazılacak. Devam edilsin mi?", "Üzerine yazma ayarı etkin", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        int before = queue.Jobs.Count; queue.Add(batch); WriteLog($"{queue.Jobs.Count - before} öğe kuyruğa eklendi. Mevcut dosya politikası: {workspace.Settings.Conflict}.");
    }
    private void StartQueue()
    {
        foreach (var group in queue.Jobs.Where(j => j.State is JobState.Waiting or JobState.Paused).GroupBy(j => j.Server.Endpoint))
        {
            var profile = group.First().Server;
            if (profile.Password.Length > 0) continue;
            var live = sessions.Select(s => s.Profile).FirstOrDefault(p => p.Endpoint == profile.Endpoint);
            var saved = workspace.Profiles.FirstOrDefault(p => p.Endpoint == profile.Endpoint);
            string? secret = live?.Password ?? saved?.Password;
            if (string.IsNullOrEmpty(secret)) secret = AskPassword(profile);
            if (secret == null) return;
            foreach (var job in group) job.Server.Password = secret;
        }
        queue.Start();
    }
    private string? AskPassword(ServerProfile server)
    {
        using var dialog = new Form { Text = "Kuyruk için parola · " + server.Name, ClientSize = new Size(460, 155), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowIcon = false, Font = Font };
        var label = new Label { Text = server.User + " @ " + server.Host, Left = 18, Top = 16, Width = 420 };
        var field = new TextBox { UseSystemPasswordChar = true, Left = 18, Top = 46, Width = 420 };
        var ok = new Button { Text = "Devam", Left = 238, Top = 100, Width = 95, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Vazgeç", Left = 343, Top = 100, Width = 95, DialogResult = DialogResult.Cancel };
        dialog.BackColor = Color.White; Theme.Style(ok, Theme.ButtonKind.Primary); Theme.Style(cancel, Theme.Tone.Neutral); ok.Height = cancel.Height = 32;
        dialog.Controls.AddRange([label, field, ok, cancel]); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? field.Text : null;
    }
    private void ConfigureDragDrop()
    {
        files.AllowDrop = localFiles.AllowDrop = true;
        localFiles.ItemDrag += (_, _) =>
        {
            var paths = SelectedTags<FileSystemInfo>(localFiles).Select(x => x.FullName).ToArray();
            if (paths.Length > 0) localFiles.DoDragDrop(new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
        };
        files.DragEnter += (_, e) => e.Effect = current != null && operation == null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        files.DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await Run(() => EnqueueUpload(paths)); };
        files.ItemDrag += (_, _) => { var entries = SelectedRemote(); if (entries.Length > 0) files.DoDragDrop(new DataObject("FileTransit.RemoteEntries", entries), DragDropEffects.Copy); };
        localFiles.DragEnter += (_, e) => e.Effect = current != null && operation == null && e.Data?.GetDataPresent("FileTransit.RemoteEntries") == true ? DragDropEffects.Copy : DragDropEffects.None;
        localFiles.DragDrop += async (_, e) => { if (e.Data?.GetData("FileTransit.RemoteEntries") is RemoteEntry[] entries) await Run(() => EnqueueDownload(entries)); };
    }
    private static bool IsGoUp(KeyEventArgs e) => e.KeyCode == Keys.Back && e.Modifiers == Keys.None || e.KeyCode == Keys.Up && e.Modifiers == Keys.Alt;
    private void ConfigureMenus()
    {
        var localMenu = new ContextMenuStrip { Renderer = Theme.Renderer }; localMenu.Items.Add("Kuyruğa yükle", null, async (_, _) => await Run(() => EnqueueUpload())); localMenu.Items.Add("Yerel listeyi yenile", null, (_, _) => LoadLocal(localDirectory)); localFiles.ContextMenuStrip = localMenu;
        var remoteMenu = new ContextMenuStrip { Renderer = Theme.Renderer }; remoteMenu.Items.Add("Seçilenleri indir", null, async (_, _) => await Run(() => EnqueueDownload())); remoteMenu.Items.Add("Yeniden adlandır", null, async (_, _) => await Run(Rename)); remoteMenu.Items.Add("Sil", null, async (_, _) => await Run(Delete)); remoteMenu.Items.Add("Yenile", null, async (_, _) => await Run(() => Browse(currentPath))); files.ContextMenuStrip = remoteMenu;
        files.KeyDown += async (_, e) =>
        {
            if (current == null) return;
            if (e.KeyCode == Keys.F2) { e.SuppressKeyPress = true; await Run(Rename); }
            else if (e.KeyCode == Keys.Delete) { e.SuppressKeyPress = true; await Run(Delete); }
            else if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; await Run(() => Browse(currentPath)); }
            else if (IsGoUp(e)) { e.SuppressKeyPress = true; await Run(GoUpRemote); }
        };
        localFiles.KeyDown += (_, e) => { if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; LoadLocal(localDirectory); } else if (IsGoUp(e)) { e.SuppressKeyPress = true; GoUpLocal(); } };
        files.MouseUp += async (_, e) => { if (e.Button == MouseButtons.XButton1 && current != null) await Run(GoUpRemote); };
        localFiles.MouseUp += (_, e) => { if (e.Button == MouseButtons.XButton1) GoUpLocal(); };
    }
}
