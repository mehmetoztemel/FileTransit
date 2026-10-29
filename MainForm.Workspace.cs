using System.Collections;
using System.Drawing.Drawing2D;

namespace FileTransit;

public sealed partial class MainForm
{
    private static readonly Color Ink = Theme.Ink, Muted = Theme.Muted, Accent = Theme.Accent;
    private readonly TextBox host = new() { PlaceholderText = "ftp.ornek.com" }, user = new(), password = new() { UseSystemPasswordChar = true }, path = new() { Text = "/" }, localPath = new();
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = 21 };
    private readonly ComboBox protocol = new() { DropDownStyle = ComboBoxStyle.DropDownList }, profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button connect = new() { Text = "Bağlan" }, disconnect = new() { Text = "Oturumu kapat" }, saveProfile = new() { Text = "Profili kaydet" };
    private readonly ListView files = new(), localFiles = new();
    private readonly TabControl remoteTabs = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
    private readonly Label localCount = Caption(""), remoteCount = Caption("Bağlantı bekleniyor"), connectionBadge = Caption("ÇEVRİMDIŞI");
    private readonly Label remoteHint = new() { Dock = DockStyle.Fill, Text = "Henüz bağlı bir sunucu yok\n\nYukarıdan kayıtlı bir sunucu seçin ya da adres bilgilerini girip Bağlan'a basın.\nFTP, FTPS ve SFTP desteklenir; her bağlantı ayrı bir sekmede açılır.", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Muted, BackColor = Color.White };
    private readonly ToolStrip actions = Theme.Strip(), localActions = Theme.Strip(), queueTools = Theme.Strip();
    private ToolStripButton uploadAction = null!;
    private readonly ToolStripButton startQueue = new("Başlat"), pauseQueue = new("Duraklat"), retryQueue = new("Yeniden dene"), cancelJobs = new("Seçileni iptal et"), moveUp = new("Yukarı"), moveDown = new("Aşağı");
    private readonly ToolTip tips = new() { InitialDelay = 400 };
    private readonly ContextMenuStrip settingsMenu = new();
    private Button localUp = null!, remoteUp = null!;
    private readonly ToolStripComboBox queueFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly DataGridView queueGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly TabPage queueTab = new("Aktarım kuyruğu") { BackColor = Color.White };
    private readonly StatusStrip statusBar = new();
    private readonly ToolStripStatusLabel status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft }, queueSummary = new();
    private readonly ToolStripProgressBar progress = new() { Width = 140 };
    private readonly ToolStripButton cancelOperation = new("İşlemi iptal et");
    private readonly ImageList icons = new() { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };
    private FileTypes fileTypes = null!;
    private TableLayoutPanel quick = null!, navigation = null!;
    private Control profileBar = null!;
    private TransferJob[] visibleJobs = [];
    private readonly Dictionary<Guid, int> jobRows = [];
    private readonly System.Windows.Forms.Timer progressTimer = new() { Interval = 300 };
    private bool progressDirty, queueDirty;

    private void BuildWorkspace()
    {
        SuspendLayout(); Text = "FileTransit · Dosya aktarım çalışma alanı"; Font = new Font("Segoe UI", 9.5f); AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(1060, 720); Size = new Size(1400, 930); StartPosition = FormStartPosition.CenterScreen; BackColor = Theme.Canvas; ForeColor = Ink;
        if (Theme.AppIcon is { } appIcon) Icon = appIcon;
        icons.Images.Add("folder", DrawIcon(true)); icons.Images.Add("file", DrawIcon(false)); fileTypes = new FileTypes(icons, 1);
        var root = Grid(1, 4);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.Controls.Add(Header(), 0, 0); root.Controls.Add(ConnectionCard(), 0, 1);
        var vertical = new SplitContainer { Size = new Size(1320, 620), Orientation = Orientation.Horizontal, SplitterDistance = 380, Panel1MinSize = 230, Panel2MinSize = 160, SplitterWidth = 8, Dock = DockStyle.Fill, Margin = new Padding(12, 6, 12, 8), BackColor = Theme.Canvas };
        var panes = new SplitContainer { Size = new Size(1320, 380), SplitterDistance = 655, Panel1MinSize = 360, Panel2MinSize = 360, SplitterWidth = 8, Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Canvas };
        ConfigureList(localFiles); ConfigureList(files); localFiles.MultiSelect = files.MultiSelect = true;
        panes.Panel1.Controls.Add(FilePane(false)); panes.Panel2.Controls.Add(FilePane(true)); vertical.Panel1.Controls.Add(panes);
        vertical.Panel2.Controls.Add(QueuePane()); root.Controls.Add(vertical, 0, 2);
        statusBar.Dock = DockStyle.Fill; statusBar.SizingGrip = false; statusBar.BackColor = Color.White; statusBar.Renderer = Theme.Renderer; status.ForeColor = queueSummary.ForeColor = Muted;
        Theme.Tint(cancelOperation, Theme.Tone.Red); statusBar.Items.AddRange([status, queueSummary, progress, cancelOperation]); root.Controls.Add(statusBar, 0, 3); Controls.Add(root);
        localFiles.DoubleClick += async (_, _) => { if (SelectedTags<FileSystemInfo>(localFiles) is [DirectoryInfo d]) LoadLocal(d.FullName); else if (current != null) await Run(() => EnqueueUpload()); };
        files.DoubleClick += async (_, _) => { if (SelectedRemote() is [var entry]) await Run(() => entry.IsDirectory && !entry.IsLink ? Browse(entry.FullName) : EnqueueDownload()); };
        remoteTabs.SelectedIndexChanged += (_, _) => ActivateSession(remoteTabs.SelectedTab?.Tag as BrowserSession);
        cancelOperation.Click += (_, _) => operation?.Cancel();
        ConfigureDragDrop(); ConfigureMenus();
        // Queue events can fire many times per second; render, save and button state are coalesced per tick.
        progressTimer.Tick += (_, _) =>
        {
            if (queueDirty) { queueDirty = progressDirty = false; RenderQueue(); UpdateControls(); SaveWorkspaceInBackground(); }
            else if (progressDirty) { progressDirty = false; UpdateProgressRows(); }
        };
        progressTimer.Start();
        Disposed += (_, _) => { progressTimer.Dispose(); icons.Dispose(); tips.Dispose(); settingsMenu.Dispose(); };
        // Start in the address box rather than the first header button, so a stray Enter cannot open a dialog.
        Shown += (_, _) => { ActiveControl = host; panes.SplitterDistance = (panes.ClientSize.Width - panes.SplitterWidth) / 2; vertical.SplitterDistance = Math.Clamp((int)(vertical.Height * .65), vertical.Panel1MinSize, vertical.Height - vertical.Panel2MinSize - vertical.SplitterWidth); };
        LoadLocal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)); ResumeLayout(true);
    }
    private Control Header()
    {
        var header = Grid(3, 1); header.BackColor = Ink; header.Padding = new Padding(18, 0, 14, 0);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var brand = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var titleFont = new Font("Segoe UI", 15f, FontStyle.Bold); var subtitleFont = new Font("Segoe UI", 9.5f);
        brand.Disposed += (_, _) => { titleFont.Dispose(); subtitleFont.Dispose(); };
        brand.Paint += (_, e) =>
        {
            const string title = "⇄  FileTransit"; var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            int width = TextRenderer.MeasureText(e.Graphics, title, titleFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(e.Graphics, title, titleFont, new Rectangle(0, 0, width + 4, brand.Height), Color.White, flags);
            TextRenderer.DrawText(e.Graphics, "Dosya aktarım çalışma alanı", subtitleFont, new Rectangle(width + 16, 0, Math.Max(0, brand.Width - width - 16), brand.Height), Theme.HeaderMuted, flags | TextFormatFlags.EndEllipsis);
        };
        header.Controls.Add(brand, 0, 0);
        // One "Ayarlar" entry point: profile management and transfer settings stacked in a drop-down menu.
        var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty, BackColor = Ink };
        var settings = new Button { Text = "Ayarlar  ▾", AutoSize = true, Height = 32, Margin = new Padding(4, 0, 4, 0), Padding = new Padding(8, 0, 10, 0), Image = Theme.Icon(Theme.Glyph.Settings, Color.White), ImageAlign = ContentAlignment.MiddleLeft, TextImageRelation = TextImageRelation.ImageBeforeText };
        Theme.Style(settings, Theme.ButtonKind.Header); tools.Controls.Add(settings);
        settingsMenu.Renderer = Theme.Renderer; settingsMenu.ShowItemToolTips = true;
        settingsMenu.Items.AddRange([
            MenuItem("Site yöneticisi…", Theme.Glyph.Servers, ManageProfiles, "Kayıtlı sunucuları ekle, düzenle, kopyala veya sil"),
            new ToolStripSeparator(),
            MenuItem("Aktarım ayarları…", Theme.Glyph.Settings, EditSettings, "Eşzamanlı aktarım, yeniden deneme, hız sınırları ve mevcut dosya politikası")]);
        settings.Click += (_, _) => settingsMenu.Show(settings, new Point(settings.Width - settingsMenu.PreferredSize.Width, settings.Height + 2));
        profileBar = tools; header.Controls.Add(tools, 1, 0);
        connectionBadge.Dock = DockStyle.None; connectionBadge.AutoSize = true; connectionBadge.Anchor = AnchorStyles.Right; connectionBadge.Padding = new Padding(10, 5, 10, 5);
        connectionBadge.Margin = new Padding(12, 0, 0, 0); connectionBadge.BackColor = Theme.HeaderChip; connectionBadge.Font = new Font(Font, FontStyle.Bold);
        header.Controls.Add(connectionBadge, 2, 0); return header;
    }
    private Control ConnectionCard()
    {
        quick = Grid(9, 2); Theme.Card(quick); quick.Margin = new Padding(12, 10, 12, 4); quick.Padding = new Padding(12, 6, 12, 8);
        quick.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); quick.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var (type, value) in new[] { (SizeType.Percent, 20f), (SizeType.Percent, 24f), (SizeType.Absolute, 74f), (SizeType.Percent, 18f), (SizeType.Percent, 18f), (SizeType.Absolute, 140f), (SizeType.Absolute, 100f), (SizeType.Absolute, 124f), (SizeType.Absolute, 128f) })
            quick.ColumnStyles.Add(new ColumnStyle(type, value));
        protocol.Items.AddRange(["FTPS · TLS", "FTP", "FTPS · Implicit", "SFTP · SSH"]); protocol.SelectedIndex = 0;
        protocol.SelectedIndexChanged += (_, _) => port.Value = protocol.SelectedIndex switch { 3 => 22, 2 => 990, _ => 21 };
        profiles.SelectedIndexChanged += (_, _) => { if (profiles.SelectedItem is ServerProfile p) ApplyProfile(p); };
        string[] titles = ["Kayıtlı sunucu", "Sunucu adresi", "Port", "Kullanıcı adı", "Parola", "Protokol", "", "", ""];
        Control[] inputs = [profiles, host, port, user, password, protocol, connect, disconnect, saveProfile];
        for (int i = 0; i < inputs.Length; i++) { quick.Controls.Add(Caption(titles[i]), i, 0); inputs[i].Dock = DockStyle.Fill; inputs[i].Margin = new Padding(3, 2, 5, 4); quick.Controls.Add(inputs[i], i, 1); }
        Theme.Style(connect, Theme.Tone.Green); Theme.Style(disconnect, Theme.Tone.Red); Theme.Style(saveProfile, Theme.Tone.Blue);
        saveProfile.Image = Theme.Icon(Theme.Glyph.Save, Theme.Colors(Theme.Tone.Blue).Text); saveProfile.ImageAlign = ContentAlignment.MiddleLeft; saveProfile.TextImageRelation = TextImageRelation.ImageBeforeText;
        tips.SetToolTip(saveProfile, "Bu satırdaki bağlantı bilgilerini ve açık klasörleri kayıtlı sunucu olarak sakla");
        Button[] rowButtons = [connect, disconnect, saveProfile];
        foreach (var button in rowButtons) { button.Dock = DockStyle.None; button.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; button.Height = host.PreferredHeight; button.Padding = Padding.Empty; }
        saveProfile.Padding = new Padding(6, 0, 4, 0); connect.Font = new Font(Font, FontStyle.Bold);
        quick.Layout += (_, _) => { foreach (var button in rowButtons) button.Height = host.Height; };
        connect.Click += async (_, _) => await Run(Connect); disconnect.Click += (_, _) => Disconnect(); saveProfile.Click += (_, _) => SaveCurrentProfile();
        return quick;
    }
    private Control FilePane(bool remote)
    {
        var panel = Grid(1, 6); Theme.Card(panel); panel.Padding = new Padding(12, 8, 12, 4);
        foreach (float height in new float[] { 28, 34, 34, 36 }) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.Controls.Add(new Label { Text = remote ? "Uzak sunucu" : "Bu bilgisayar", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), ForeColor = Ink, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 0, 3, 0) }, 0, 0);
        if (remote)
        {
            var tabsArea = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            tabsArea.Controls.Add(remoteTabs); tabsArea.Controls.Add(Caption("Açık oturum yok · Her bağlantı burada ayrı bir sekmede açılır."));
            panel.Controls.Add(tabsArea, 0, 1);
            ActionButton(actions, "İndir", () => EnqueueDownload(), Theme.Tone.Teal, "Seçilenleri soldaki klasöre indir", Theme.Glyph.Download);
            actions.Items.Add(new ToolStripSeparator());
            ActionButton(actions, "Yenile", () => Browse(currentPath), Theme.Tone.Blue, "Uzak listeyi yenile (F5)", Theme.Glyph.Refresh); ActionButton(actions, "Yeni klasör", CreateFolder, Theme.Tone.Green, "Bu klasörde yeni klasör oluştur", Theme.Glyph.NewFolder);
            ActionButton(actions, "Yeniden adlandır", Rename, Theme.Tone.Amber, "Seçili öğeyi yeniden adlandır (F2)", Theme.Glyph.Rename); ActionButton(actions, "Sil", Delete, Theme.Tone.Red, "Seçilenleri kalıcı olarak sil (Delete)", Theme.Glyph.Delete);
            panel.Controls.Add(actions, 0, 2);
        }
        else
        {
            var shortcuts = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady)) { var d = drive.RootDirectory.FullName; shortcuts.Controls.Add(Button(d, () => LoadLocal(d), 46)); }
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            foreach (var (name, folder) in new[] { ("Masaüstü", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)), ("Belgeler", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), ("İndirilenler", downloads) })
                if (Directory.Exists(folder)) shortcuts.Controls.Add(Button(name, () => LoadLocal(folder), 92));
            panel.Controls.Add(shortcuts, 0, 1);
            uploadAction = ActionButton(localActions, "Sunucuya yükle", () => EnqueueUpload(), Theme.Tone.Teal, "Seçilenleri açık uzak klasöre yükle", Theme.Glyph.Upload);
            localActions.Items.Add(new ToolStripSeparator());
            var refresh = Theme.Tint(new ToolStripButton("Yenile") { ToolTipText = "Yerel listeyi yenile (F5)" }, Theme.Tone.Blue, Theme.Glyph.Refresh); refresh.Click += (_, _) => LoadLocal(localDirectory); localActions.Items.Add(refresh);
            panel.Controls.Add(localActions, 0, 2);
        }
        var nav = Grid(3, 1); nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118)); nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        var up = Button("Üst klasör", () => { if (remote) _ = Run(GoUpRemote); else GoUpLocal(); });
        up.Image = Theme.Icon(Theme.Glyph.Up, Theme.Colors(Theme.Tone.Blue).Text); up.ImageAlign = ContentAlignment.MiddleLeft; up.TextImageRelation = TextImageRelation.ImageBeforeText; up.Padding = new Padding(6, 0, 4, 0);
        tips.SetToolTip(up, "Bir üst klasöre çık (Backspace, Alt+↑ veya farenin geri tuşu)");
        if (remote) remoteUp = up; else localUp = up;
        var go = Button("Git", () => { if (remote) _ = Run(() => Browse(path.Text)); else LoadLocal(localPath.Text); });
        var address = remote ? path : localPath; address.Dock = DockStyle.Fill; address.Margin = new Padding(3, 4, 3, 3); nav.Controls.Add(up, 0, 0); nav.Controls.Add(address, 1, 0); nav.Controls.Add(go, 2, 0);
        address.KeyDown += async (_, e) => { if (e.KeyCode != Keys.Enter) return; e.SuppressKeyPress = true; if (remote) await Run(() => Browse(path.Text)); else LoadLocal(localPath.Text); };
        if (remote) navigation = nav; panel.Controls.Add(nav, 0, 3);
        var area = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) }; area.Controls.Add(remote ? files : localFiles);
        if (remote) { remoteHint.Font = new Font(Font.FontFamily, 10.5f); area.Controls.Add(remoteHint); remoteHint.BringToFront(); }
        panel.Controls.Add(area, 0, 4); panel.Controls.Add(remote ? remoteCount : localCount, 0, 5); return panel;
    }
    private Control QueuePane()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Point(14, 5) }; var logTab = new TabPage("İşlem günlüğü") { BackColor = Color.White };
        var panel = Grid(1, 2); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var clear = new ToolStripButton("Bitenleri temizle");
        var show = new ToolStripLabel("Göster:") { Alignment = ToolStripItemAlignment.Right, ForeColor = Muted }; queueFilter.Alignment = ToolStripItemAlignment.Right;
        Theme.Tint(startQueue, Theme.Tone.Green, Theme.Glyph.Play); Theme.Tint(pauseQueue, Theme.Tone.Neutral, Theme.Glyph.Pause); Theme.Tint(retryQueue, Theme.Tone.Amber, Theme.Glyph.Refresh); Theme.Tint(cancelJobs, Theme.Tone.Red, Theme.Glyph.Cancel);
        Theme.Tint(moveUp, Theme.Tone.Blue, Theme.Glyph.ChevronUp); Theme.Tint(moveDown, Theme.Tone.Blue, Theme.Glyph.ChevronDown); Theme.Tint(clear, Theme.Tone.Violet);
        
        if (moveUp.Image != null) moveUp.DisplayStyle = moveDown.DisplayStyle = ToolStripItemDisplayStyle.Image;
        startQueue.ToolTipText = "Bekleyen ve duraklatılmış işleri başlat"; pauseQueue.ToolTipText = "Çalışan aktarımları durdur; işler kuyrukta kalır"; retryQueue.ToolTipText = "Seçili başarısız/iptal edilmiş işleri yeniden beklemeye al";
        moveUp.ToolTipText = "Seçili işi yukarı taşı (kuyruk dururken)"; moveDown.ToolTipText = "Seçili işi aşağı taşı (kuyruk dururken)";
        queueFilter.Items.AddRange(["Tüm işler", "Bekleyenler", "Başarısız", "Tamamlananlar"]); queueFilter.SelectedIndex = 0; queueFilter.SelectedIndexChanged += (_, _) => { if (queue != null) RenderQueue(); };
        queueTools.Items.AddRange([startQueue, pauseQueue, new ToolStripSeparator(), retryQueue, cancelJobs, new ToolStripSeparator(), moveUp, moveDown, new ToolStripSeparator(), clear, queueFilter, show]);
        startQueue.Click += (_, _) => StartQueue(); pauseQueue.Click += (_, _) => queue.Pause(); retryQueue.Click += (_, _) => queue.Retry(SelectedJobs()); cancelJobs.Click += (_, _) => queue.Cancel(SelectedJobs());
        moveUp.Click += (_, _) => { if (SelectedJobs() is [var job]) queue.Move(job, -1); }; moveDown.Click += (_, _) => { if (SelectedJobs() is [var job]) queue.Move(job, 1); }; clear.Click += (_, _) => queue.ClearFinished();
        string[] names = ["Dosya", "Sunucu", "Yön", "Boyut", "Durum", "İlerleme", "Hız", "Deneme", "Açıklama"];
        int[] weights = [190, 110, 70, 70, 95, 125, 80, 55, 190];
        for (int i = 0; i < names.Length; i++) queueGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = names[i], HeaderText = names[i], FillWeight = weights[i], MinimumWidth = i == 5 ? 95 : i == 7 ? 68 : 55, SortMode = DataGridViewColumnSortMode.NotSortable });
        queueGrid.EnableHeadersVisualStyles = false; queueGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; queueGrid.ColumnHeadersHeight = 32;
        queueGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None; var header = queueGrid.ColumnHeadersDefaultCellStyle;
        header.BackColor = header.SelectionBackColor = Theme.HeaderCell; header.ForeColor = header.SelectionForeColor = Muted; header.Font = new Font(Font, FontStyle.Bold); header.Padding = new Padding(4, 0, 4, 0);
        queueGrid.DefaultCellStyle.SelectionBackColor = Theme.AccentSoft; queueGrid.DefaultCellStyle.SelectionForeColor = Ink; queueGrid.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        queueGrid.AlternatingRowsDefaultCellStyle.BackColor = Theme.Stripe; queueGrid.RowTemplate.Height = 28; queueGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; queueGrid.GridColor = Theme.Line;
        // Virtual mode: cells are read from visibleJobs on paint, so large queues are never copied into grid rows.
        queueGrid.VirtualMode = true;
        queueGrid.CellValueNeeded += (_, e) => e.Value = JobCell(e.RowIndex, e.ColumnIndex);
        queueGrid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleJobs.Length || e.CellStyle == null) return;
            var job = visibleJobs[e.RowIndex];
            Color? color = job.State == JobState.Failed ? Theme.Danger : e.ColumnIndex == 4 ? StateColor(job.State) : e.ColumnIndex == 2 ? (job.Upload ? Theme.Upload : Accent) : null;
            if (color is { } c) e.CellStyle.ForeColor = e.CellStyle.SelectionForeColor = c;
        };
        queueGrid.CellPainting += PaintProgressCell;
        queueGrid.CellToolTipTextNeeded += (_, e) => { if (e.ColumnIndex == 0 && e.RowIndex >= 0 && e.RowIndex < visibleJobs.Length) { var job = visibleJobs[e.RowIndex]; e.ToolTipText = job.LocalPath + " ↔ " + job.RemotePath; } };
        log.Font = new Font("Consolas", 9.5f); log.BackColor = Theme.Stripe;
        panel.Controls.Add(queueTools, 0, 0); panel.Controls.Add(queueGrid, 0, 1); queueTab.Controls.Add(panel); logTab.Controls.Add(log); tabs.TabPages.AddRange([queueTab, logTab]); return tabs;
    }
    // "İlerleme" column: thin bar plus percentage, coloured by job state.
    private void PaintProgressCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.ColumnIndex != 5 || e.RowIndex < 0 || e.RowIndex >= visibleJobs.Length || e.Graphics == null) return;
        var job = visibleJobs[e.RowIndex]; var cell = e.CellBounds;
        e.PaintBackground(e.ClipBounds, true);
        var bar = new Rectangle(cell.X + 8, cell.Y + cell.Height / 2 - 3, Math.Max(0, cell.Width - 58), 6);
        var color = job.State switch { JobState.Failed => Theme.Danger, JobState.Completed => Theme.Success, JobState.Paused => Theme.Warning, JobState.Skipped or JobState.Cancelled => Muted, _ => Accent };
        if (bar.Width > 8)
        {
            using var track = new SolidBrush(Theme.Line); using var fill = new SolidBrush(color);
            e.Graphics.FillRectangle(track, bar); e.Graphics.FillRectangle(fill, bar with { Width = (int)(bar.Width * Math.Clamp(job.Percent, 0, 100) / 100) });
        }
        TextRenderer.DrawText(e.Graphics, $"{job.Percent:0}%", e.CellStyle?.Font ?? Font, new Rectangle(bar.Right + 6, cell.Y, cell.Right - bar.Right - 8, cell.Height), job.State == JobState.Failed ? Theme.Danger : Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        e.Handled = true;
    }
    private static Color StateColor(JobState state) => state switch { JobState.Running => Accent, JobState.Completed => Theme.Success, JobState.Failed => Theme.Danger, JobState.Paused => Theme.Warning, JobState.Waiting => Ink, _ => Muted };
    private static TableLayoutPanel Grid(int columns, int rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = Padding.Empty };
        if (columns == 1) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (rows == 1) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); return grid;
    }
    private static Label Caption(string text) => new() { Text = text, Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false, Margin = new Padding(3, 0, 3, 0) };
    // Navigation helpers (drives, folders, Üst klasör, Git) use the blue tone.
    private Button Button(string text, Action action, int width = 0)
    {
        var button = new Button { Text = text, Dock = width == 0 ? DockStyle.Fill : DockStyle.None, Width = width == 0 ? 90 : width, Height = 28, Margin = new Padding(3), Padding = Padding.Empty }; Theme.Style(button, Theme.Tone.Blue); button.Click += (_, _) => action(); return button;
    }
    private static ToolStripMenuItem MenuItem(string text, char glyph, Action action, string tip)
    {
        var item = new ToolStripMenuItem(text, Theme.Icon(glyph, Theme.AccentDark)) { ToolTipText = tip, Padding = new Padding(4, 6, 4, 6) }; item.Click += (_, _) => action(); return item;
    }
    private ToolStripButton ActionButton(ToolStrip strip, string text, Func<Task> action, Theme.Tone tone, string tip, char glyph)
    {
        var button = Theme.Tint(new ToolStripButton(text) { ToolTipText = tip }, tone, glyph);
        button.Click += async (_, _) => await Run(action); strip.Items.Add(button); return button;
    }
    // Virtual mode: only visible rows reach the native control, so folders with tens of thousands of entries stay responsive.
    private sealed class ListRows { public ListViewItem[] Items = []; public EntryComparer? Sorter; }
    private void ConfigureList(ListView list)
    {
        list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.HideSelection = false; list.BorderStyle = BorderStyle.None; list.SmallImageList = icons;
        list.VirtualMode = true; var state = new ListRows(); list.Tag = state;
        list.Columns.Add("Dosya adı", 240); list.Columns.Add("Tür", 115); list.Columns.Add("Boyut", 78); list.Columns.Add("Değiştirilme", 112);
        list.SizeChanged += (_, _) => list.Columns[0].Width = Math.Max(110, list.ClientSize.Width - 310);
        list.RetrieveVirtualItem += (_, e) => e.Item = e.ItemIndex < state.Items.Length ? state.Items[e.ItemIndex] : new ListViewItem("");
        list.SearchForVirtualItem += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Text)) return;
            for (int i = Math.Max(0, e.StartIndex); i < state.Items.Length; i++)
                if (state.Items[i].Text.StartsWith(e.Text, StringComparison.CurrentCultureIgnoreCase)) { e.Index = i; return; }
        };
        list.ColumnClick += (_, e) =>
        {
            bool descending = state.Sorter is { } old && old.Column == e.Column && !old.Descending; state.Sorter = new EntryComparer(e.Column, descending);
            var chosen = list.SelectedIndices.Cast<int>().Where(i => i < state.Items.Length).Select(i => state.Items[i]).ToHashSet();
            list.BeginUpdate();
            try
            {
                list.SelectedIndices.Clear(); Array.Sort(state.Items, state.Sorter);
                if (chosen.Count > 0) for (int i = 0; i < state.Items.Length; i++) if (chosen.Contains(state.Items[i])) list.SelectedIndices.Add(i);
                list.Invalidate();
            }
            finally { list.EndUpdate(); }
        };
    }
    private static void ShowRows(ListView list, ListViewItem[] items)
    {
        var state = (ListRows)list.Tag!; if (state.Sorter != null) Array.Sort(items, state.Sorter);
        list.BeginUpdate();
        try { list.SelectedIndices.Clear(); list.VirtualListSize = 0; state.Items = items; list.VirtualListSize = items.Length; }
        finally { list.EndUpdate(); }
    }
    private static T[] SelectedTags<T>(ListView list)
    {
        var items = ((ListRows)list.Tag!).Items;
        return list.SelectedIndices.Cast<int>().Where(i => i < items.Length).Select(i => items[i].Tag).OfType<T>().ToArray();
    }
    private sealed class EntryComparer(int column, bool descending) : IComparer
    {
        public int Column => column; public bool Descending => descending;
        public int Compare(object? x, object? y)
        {
            var a = (ListViewItem)x!; var b = (ListViewItem)y!;
            int result = column == 2 ? SizeOf(a.Tag).CompareTo(SizeOf(b.Tag)) : string.Compare(a.SubItems[column].Text, b.SubItems[column].Text, StringComparison.CurrentCultureIgnoreCase);
            return descending ? -result : result;
        }
        private static long SizeOf(object? tag) => tag switch { FileInfo f => f.Length, RemoteEntry r => r.Size, _ => -1 };
    }
    private void LoadLocal(string directory)
    {
        localFiles.BeginUpdate();
        try
        {
            var folder = new DirectoryInfo(directory); var entries = folder.GetFileSystemInfos().OrderBy(x => x is not DirectoryInfo).ThenBy(x => x.Name).ToArray();
            var rows = entries.Select(entry =>
            {
                var type = entry is DirectoryInfo ? (Image: 0, Name: "Klasör") : fileTypes.Lookup(entry.Name);
                var row = new ListViewItem(entry.Name) { Tag = entry, ImageIndex = type.Image }; row.SubItems.AddRange([type.Name, entry is FileInfo f ? FormatSize(f.Length) : "—", entry.LastWriteTime.ToString("dd.MM.yy HH:mm")]); return row;
            }).ToArray();
            ShowRows(localFiles, rows); localDirectory = folder.FullName; localPath.Text = localDirectory; localCount.Text = $"{entries.Length} öğe · Dosyaları sağ panele sürükleyerek yükleyin";
        }
        catch (Exception ex) { WriteLog("Yerel klasör: " + ex.Message); localCount.Text = "Klasör açılamadı · İşlem günlüğüne bakın"; localPath.Text = localDirectory; }
        finally { localFiles.EndUpdate(); localUp.Enabled = localDirectory.Length > 0 && Directory.GetParent(localDirectory) != null; }
    }
    // Going up re-selects the folder just left, so repeated Backspace/Enter navigation keeps its place.
    private void GoUpLocal()
    {
        if (Directory.GetParent(localDirectory) is not { } parent) return;
        string child = new DirectoryInfo(localDirectory).Name; LoadLocal(parent.FullName); SelectByName(localFiles, child);
    }
    private async Task GoUpRemote()
    {
        if (current == null || currentPath.TrimEnd('/').Length == 0) return;
        string child = currentPath.TrimEnd('/').Split('/').Last(); await Browse(RemotePaths.Parent(currentPath)); SelectByName(files, child);
    }
    private static void SelectByName(ListView list, string name)
    {
        int index = Array.FindIndex(((ListRows)list.Tag!).Items, item => item.Text == name); if (index < 0) return;
        list.SelectedIndices.Clear(); list.SelectedIndices.Add(index); list.FocusedItem = list.Items[index]; list.EnsureVisible(index);
    }
    private void RenderRemote(IEnumerable<RemoteEntry> entries)
    {
        var rows = entries.OrderBy(x => !x.IsDirectory).ThenBy(x => x.Name).Select(entry =>
        {
            var type = entry.IsDirectory ? (Image: 0, Name: "Klasör") : fileTypes.Lookup(entry.Name);
            var row = new ListViewItem(entry.Name) { Tag = entry, ImageIndex = type.Image }; row.SubItems.AddRange([entry.IsLink ? "Bağlantı" : type.Name, entry.IsDirectory ? "—" : FormatSize(entry.Size), entry.Modified == DateTime.MinValue ? "—" : entry.Modified.ToString("dd.MM.yy HH:mm")]); return row;
        }).ToArray();
        ShowRows(files, rows); remoteCount.Text = current == null ? "Bağlantı bekleniyor" : $"{rows.Length} öğe · {current.Profile.Host}";
    }
    private void UpdateControls()
    {
        bool busy = operation != null || closing, connected = current?.Remote.IsConnected == true;
        quick.Enabled = profileBar.Enabled = !busy; disconnect.Enabled = !busy && current != null; actions.Enabled = navigation.Enabled = files.Enabled = !busy && connected; remoteTabs.Enabled = !busy;
        remoteTabs.Visible = sessions.Count > 0; remoteUp.Enabled = currentPath != "/"; localFiles.Enabled = localActions.Enabled = !busy; uploadAction.Enabled = connected; remoteHint.Visible = current == null; cancelOperation.Enabled = operation != null;
        connectionBadge.Text = connected ? $"●  {sessions.Count} AÇIK OTURUM" : "●  ÇEVRİMDIŞI"; connectionBadge.ForeColor = connected ? Theme.Online : Theme.HeaderMuted;
        if (queue == null) return;
        startQueue.Enabled = !closing && !(queue.ActiveCount > 0 && !queue.IsRunning); pauseQueue.Enabled = queue.IsRunning || queue.ActiveCount > 0;
        moveUp.Enabled = moveDown.Enabled = !queue.IsRunning && queue.ActiveCount == 0;
        status.Text = busy ? "İşlem sürüyor…" : connected ? $"{current!.Profile.Name} · {current.Profile.Protocol}" : "Hazır · Bir sunucu profili seçin";
        int pending = queue.Jobs.Count(j => j.State is JobState.Waiting or JobState.Paused);
        queueSummary.Text = $"{queue.ActiveCount} aktif / {pending} bekleyen · {workspace.Settings.Parallel} eşzamanlı";
        queueTab.Text = queue.Jobs.Count == 0 ? "Aktarım kuyruğu" : $"Aktarım kuyruğu ({queue.ActiveCount + pending} / {queue.Jobs.Count})";
    }
    private static string StateName(JobState state) => state switch { JobState.Waiting => "Bekliyor", JobState.Running => "Aktarılıyor", JobState.Completed => "Tamamlandı", JobState.Failed => "Başarısız", JobState.Paused => "Duraklatıldı", JobState.Skipped => "Atlandı", _ => "İptal edildi" };
    private TransferJob[] SelectedJobs() => queueGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i >= 0 && i < visibleJobs.Length).Select(i => visibleJobs[i]).ToArray();
    private object? JobCell(int row, int column)
    {
        if (row < 0 || row >= visibleJobs.Length) return null;
        var job = visibleJobs[row];
        return column switch
        {
            0 => job.Name, 1 => job.Server.Name, 2 => job.Upload ? "↑ Yükle" : "↓ İndir", 3 => job.Directory ? "Klasör" : FormatSize(job.Size), 4 => StateName(job.State),
            5 => $"{job.Percent:0}", 6 => job.State == JobState.Running ? FormatSize((long)job.BytesPerSecond) + "/sn" : "—", 7 => job.Attempts, _ => job.Error
        };
    }
    private void RenderQueue()
    {
        var next = queue.Jobs.Where(j => queueFilter.SelectedIndex switch { 1 => j.State is JobState.Waiting or JobState.Paused or JobState.Running, 2 => j.State == JobState.Failed, 3 => j.State is JobState.Completed or JobState.Skipped, _ => true }).ToArray();
        if (next.AsSpan().SequenceEqual(visibleJobs)) { queueGrid.Invalidate(); UpdateProgressRows(); return; }
        // Row membership changed: rebuild the virtual rows, then restore selection and scroll position.
        var selected = SelectedJobs().Select(j => j.Id).ToHashSet(); int top = queueGrid.FirstDisplayedScrollingRowIndex;
        visibleJobs = next; jobRows.Clear(); for (int i = 0; i < next.Length; i++) jobRows[next[i].Id] = i;
        queueGrid.RowCount = 0; queueGrid.RowCount = next.Length; queueGrid.ClearSelection();
        if (selected.Count > 0) for (int i = 0; i < next.Length; i++) if (selected.Contains(next[i].Id)) queueGrid.Rows[i].Selected = true;
        if (top > 0 && next.Length > 0) queueGrid.FirstDisplayedScrollingRowIndex = Math.Min(top, next.Length - 1);
        UpdateProgressRows();
    }
    private void RefreshProgress() => progressDirty = true;
    private void UpdateProgressRows()
    {
        foreach (var job in queue.Jobs.Where(j => j.State == JobState.Running)) if (jobRows.TryGetValue(job.Id, out var row)) queueGrid.InvalidateRow(row);
        var running = queue.Jobs.Where(j => j.State == JobState.Running).ToArray(); progress.Value = running.Length == 0 ? 0 : (int)Math.Clamp(running.Average(j => j.Percent), 0, 100);
    }
    private static string FormatSize(long size) => size < 0 ? "—" : size < 1024 ? $"{size} B" : size < 1048576 ? $"{size / 1024d:N1} KB" : size < 1073741824 ? $"{size / 1048576d:N1} MB" : $"{size / 1073741824d:N1} GB";
    private static Bitmap DrawIcon(bool folder)
    {
        var bitmap = new Bitmap(20, 20); using var g = Graphics.FromImage(bitmap); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(folder ? Color.FromArgb(224, 176, 74) : Color.FromArgb(153, 181, 204));
        if (folder) { g.FillRectangle(brush, 2, 4, 8, 5); g.FillRectangle(brush, 2, 8, 16, 10); } else g.FillRectangle(brush, 5, 3, 11, 15); return bitmap;
    }
}