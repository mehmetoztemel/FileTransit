namespace FileTransit;

public sealed partial class MainForm : Form
{
    private readonly WorkspaceStore store;
    private WorkspaceState workspace = new();
    private TransferQueue queue = null!;
    private bool storageEnabled = true, closing, closeApproved, saving, saveAgain;
    private CancellationTokenSource? operation;
    private readonly List<BrowserSession> sessions = [];
    private BrowserSession? current;
    private string currentPath = "/";
    private string localDirectory = "";
    private sealed class BrowserSession(ServerProfile profile, IRemoteSession remote, string path)
    {
        public ServerProfile Profile = profile;
        public IRemoteSession Remote = remote;
        public string Path = path;
        public RemoteEntry[] Entries = [];
    }
    private CancellationToken Token => operation?.Token ?? CancellationToken.None;
    public MainForm(string? workspaceDirectory = null)
    {
        store = new WorkspaceStore(workspaceDirectory ?? WorkspaceStore.DefaultDirectory);
        string? imported = null;
        if (workspaceDirectory == null)
            foreach (var (legacy, keepQueue) in WorkspaceStore.LegacyDirectories)
            {
                try
                {
                    if (!WorkspaceStore.ImportLegacy(WorkspaceStore.DefaultDirectory, legacy, keepQueue)) continue;
                    imported = $"{legacy} klasöründen profiller, ayarlar ve SFTP anahtarları kopyalandı{(keepQueue ? " (kuyruk dahil)" : " (kuyruk hariç)")}. Eski dosya değiştirilmedi.";
                }
                catch (Exception ex) { imported = $"{legacy} klasöründen içe aktarılamadı; boş çalışma alanıyla başlandı: {ex.Message}"; }
                break;
            }
        try { workspace = store.Load(); }
        catch (Exception ex) { storageEnabled = false; MessageBox.Show("Çalışma alanı okunamadı; mevcut dosyanın üzerine yazılmayacak.\n" + ex.Message, "FileTransit"); }
        BuildWorkspace();
        if (imported != null) WriteLog(imported);
        queue = new TransferQueue(workspace.Queue, workspace.Settings, p => new RemoteSession(p, TrustHost));
        queue.Changed += () => queueDirty = true;
        queue.ProgressChanged += RefreshProgress;
        RefreshProfiles(); RenderQueue(); UpdateControls(); FormClosing += OnClosing;
    }
    private bool TrustHost(string endpoint, string fingerprint)
    {
        if (InvokeRequired) return (bool)Invoke(() => TrustHost(endpoint, fingerprint));
        if (workspace.HostKeys.TryGetValue(endpoint, out var known)) return known == fingerprint;
        if (closing) return false;
        if (MessageBox.Show(this, $"{endpoint}\n\nSunucu anahtarı:\n{fingerprint}\n\nBu parmak izini sunucu yöneticinizle doğruladıktan sonra güvenmek istiyor musunuz?", "İlk SFTP bağlantısı", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
        workspace.HostKeys[endpoint] = fingerprint; SaveWorkspace(); return true;
    }
    private void SaveWorkspace()
    {
        if (!storageEnabled) return;
        try { store.Save(workspace); }
        catch (Exception ex) { ReportSaveError(ex); }
    }
    // Large queues serialize off the UI thread; the store discards snapshots older than one already written.
    private async void SaveWorkspaceInBackground()
    {
        if (!storageEnabled) return;
        if (saving) { saveAgain = true; return; }
        saving = true;
        try
        {
            do
            {
                saveAgain = false; var snapshot = store.Capture(workspace);
                try { await Task.Run(() => store.Write(snapshot)); }
                catch (Exception ex) { if (storageEnabled) ReportSaveError(ex); return; }
            }
            while (saveAgain && storageEnabled);
        }
        finally { saving = false; }
    }
    private void ReportSaveError(Exception ex)
    {
        storageEnabled = false; WriteLog("Kayıt hatası: " + ex.Message);
        if (!closing) MessageBox.Show(this, "Çalışma alanı kaydedilemedi. Bu oturumdaki değişiklikler kapanınca kaybolabilir.\n" + ex.Message, "Kayıt hatası");
    }
    private async Task Run(Func<Task> action)
    {
        if (operation != null || closing) return;
        operation = new CancellationTokenSource(); UpdateControls();
        try { await action(); }
        catch (OperationCanceledException) { WriteLog("İşlem iptal edildi."); }
        catch (Exception ex) { WriteLog("Hata: " + ex.Message); if (!closing) MessageBox.Show(this, ex.Message, "İşlem tamamlanamadı", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { operation.Dispose(); operation = null; UpdateControls(); }
    }
    private ServerProfile ReadQuickProfile()
    {
        var selected = profiles.SelectedItem as ServerProfile;
        var profile = selected?.Copy() ?? new ServerProfile();
        profile.Host = host.Text.Trim(); profile.Port = (int)port.Value; profile.User = user.Text.Trim(); profile.Password = password.Text; profile.Protocol = (ServerProtocol)protocol.SelectedIndex;
        if (selected == null) profile.Name = profile.Host;
        profile.Validate(); return profile;
    }
    private void ApplyProfile(ServerProfile profile)
    {
        host.Text = profile.Host; protocol.SelectedIndex = (int)profile.Protocol; port.Value = Math.Clamp(profile.Port, 1, 65535); user.Text = profile.User; password.Text = profile.Password;
        if (Directory.Exists(profile.LocalFolder)) LoadLocal(profile.LocalFolder);
    }
    private async Task Connect()
    {
        var profile = ReadQuickProfile(); var remote = new RemoteSession(profile, TrustHost);
        try
        {
            await remote.Connect(Token);
            var initial = string.IsNullOrWhiteSpace(profile.RemoteFolder) ? await remote.WorkingDirectory(Token) : profile.RemoteFolder;
            var session = new BrowserSession(profile, remote, initial) { Entries = await remote.List(initial, Token) };
            sessions.Add(session); var tab = new TabPage(profile.Name) { Tag = session, BackColor = Color.White }; remoteTabs.TabPages.Add(tab); remoteTabs.SelectedTab = tab; ActivateSession(session);
            WriteLog($"Bağlandı: {profile.Name} ({profile.Protocol})");
        }
        catch { remote.Dispose(); throw; }
    }
    private void ActivateSession(BrowserSession? session)
    {
        current = session; currentPath = session?.Path ?? "/"; path.Text = currentPath;
        RenderRemote(session?.Entries ?? []);
        if (session != null) { profiles.SelectedItem = workspace.Profiles.FirstOrDefault(p => p.Id == session.Profile.Id) ?? (object)"Hızlı bağlantı"; ApplyProfile(session.Profile); }
        UpdateControls();
    }
    private void Disconnect()
    {
        if (current == null) return;
        var old = current; var tab = remoteTabs.TabPages.Cast<TabPage>().First(t => t.Tag == old);
        sessions.Remove(old); remoteTabs.TabPages.Remove(tab); tab.Dispose(); old.Remote.Dispose();
        ActivateSession(remoteTabs.SelectedTab?.Tag as BrowserSession);
        WriteLog("Gezinti bağlantısı kapatıldı. Aktarım kuyruğu kendi bağlantılarıyla devam eder.");
    }
    private async Task Browse(string directory)
    {
        var session = current ?? throw new InvalidOperationException("Önce sunucuya bağlanın.");
        if (string.IsNullOrWhiteSpace(directory)) directory = "/";
        if (!directory.StartsWith('/')) directory = RemotePaths.Join(currentPath, directory);
        var entries = await session.Remote.List(directory, Token);
        session.Path = directory; session.Entries = entries; currentPath = directory; path.Text = directory; RenderRemote(entries);
    }
    private RemoteEntry[] SelectedRemote() => SelectedTags<RemoteEntry>(files);
    private RemoteEntry OneRemote() => SelectedRemote() is [var item] ? item : throw new InvalidOperationException("Bir dosya veya klasör seçin.");
    private async Task CreateFolder()
    {
        var name = AskName("Yeni klasör", ""); if (name == null || current == null) return;
        await current.Remote.CreateDirectory(RemotePaths.Join(currentPath, name), Token); await Browse(currentPath);
    }
    private async Task Rename()
    {
        var entry = OneRemote(); var name = AskName("Yeniden adlandır", entry.Name); if (name == null || name == entry.Name) return;
        string destination = RemotePaths.Join(currentPath, name);
        if (await current!.Remote.Exists(destination, Token)) throw new IOException("Hedefte bu adla bir öğe var.");
        await current.Remote.Rename(entry.FullName, destination, Token); await Browse(currentPath);
    }
    private async Task Delete()
    {
        var entries = SelectedRemote(); if (entries.Length == 0) return;
        if (MessageBox.Show(this, $"{entries.Length} öğe kalıcı olarak silinecek. Seçilen klasörlerin tüm içeriği de silinir.\nDevam edilsin mi?", "Uzak dosyaları sil", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        foreach (var entry in entries) await current!.Remote.Delete(entry, Token);
        await Browse(currentPath);
    }
    private void ManageProfiles()
    {
        ServerProfile? draft = null; try { draft = ReadQuickProfile(); } catch { }
        using var dialog = new ProfileManager(workspace.Profiles, draft);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        workspace.Profiles = dialog.Profiles; SaveWorkspace(); RefreshProfiles(dialog.Selected?.Id);
    }
    private void SaveCurrentProfile()
    {
        try
        {
            var profile = ReadQuickProfile(); var name = AskName("Profil adı", profile.Name); if (name == null) return;
            profile.Name = name; profile.LocalFolder = localDirectory; profile.RemoteFolder = currentPath;
            profile.RememberPassword = MessageBox.Show(this, "Parola Windows hesabınıza bağlı olarak şifrelenip kaydedilsin mi?", "Parolayı hatırla", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            var existing = workspace.Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (MessageBox.Show(this, "Mevcut profil güncellensin mi?", "Profil mevcut", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                profile.Id = existing.Id; workspace.Profiles.Remove(existing);
            }
            else profile.Id = Guid.NewGuid();
            workspace.Profiles.Add(profile); SaveWorkspace(); RefreshProfiles(profile.Id);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Profil kaydedilemedi"); }
    }
    private void RefreshProfiles(Guid? id = null)
    {
        profiles.Items.Clear(); profiles.Items.Add("Hızlı bağlantı"); foreach (var profile in workspace.Profiles) profiles.Items.Add(profile);
        profiles.SelectedItem = workspace.Profiles.FirstOrDefault(p => p.Id == id) ?? (object)"Hızlı bağlantı";
    }
    private void EditSettings()
    {
        using var dialog = new SettingsDialog(workspace.Settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        workspace.Settings = dialog.Settings; queue.Settings = workspace.Settings; SaveWorkspace(); UpdateControls();
        WriteLog("Ayarlar kaydedildi. Çalışan dosyalar mevcut ayarlarıyla tamamlanır.");
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closeApproved) return;
        e.Cancel = true; if (closing) return;
        if ((queue.ActiveCount > 0 || operation != null) && MessageBox.Show(this, "Aktif işlemler duraklatılıp kuyruk kaydedilsin ve uygulama kapatılsın mı?", "Aktif işlemler", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        closing = true; operation?.Cancel(); queue.Pause(); UpdateControls();
        while (queue.ActiveCount > 0 || operation != null) await Task.Delay(100);
        SaveWorkspace(); foreach (var session in sessions) session.Remote.Dispose();
        closeApproved = true; Close();
    }
    private string? AskName(string title, string initial)
    {
        using var dialog = new Form { Text = title, ClientSize = new Size(440, 125), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowIcon = false, Font = Font };
        var input = new TextBox { Text = initial, Left = 16, Top = 18, Width = 408 }; var ok = new Button { Text = "Tamam", DialogResult = DialogResult.OK, Left = 224, Top = 70, Width = 95 }; var back = new Button { Text = "Vazgeç", DialogResult = DialogResult.Cancel, Left = 329, Top = 70, Width = 95 };
        dialog.BackColor = Color.White; Theme.Style(ok, Theme.ButtonKind.Primary); Theme.Style(back, Theme.Tone.Neutral); ok.Height = back.Height = 32;
        dialog.Controls.AddRange([input, ok, back]); dialog.AcceptButton = ok; dialog.CancelButton = back;
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        string name = input.Text.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\r', '\n', '\0']) >= 0) throw new InvalidOperationException("Geçerli bir ad girin.");
        return name;
    }
    private void WriteLog(string message) { if (log.TextLength > 150000) log.Clear(); log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"); }
}
