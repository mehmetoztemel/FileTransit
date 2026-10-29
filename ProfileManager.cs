namespace FileTransit;

public sealed class ProfileManager : Form
{
    private readonly ListBox list = new() { Dock = DockStyle.Fill, DisplayMember = nameof(ServerProfile.Name), BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 48, IntegralHeight = false };
    private readonly PropertyGrid editor = new() { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true, PropertySort = PropertySort.Categorized };
    public List<ServerProfile> Profiles { get; }
    public ServerProfile? Selected => list.SelectedItem as ServerProfile;
    public ProfileManager(IEnumerable<ServerProfile> profiles, ServerProfile? current = null)
    {
        Text = "Site yöneticisi · FTP / FTPS / SFTP"; Size = new Size(900, 660); MinimumSize = new Size(720, 520); StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f); AutoScaleMode = AutoScaleMode.Dpi; BackColor = Theme.Canvas; ForeColor = Theme.Ink; if (Theme.AppIcon is { } appIcon) Icon = appIcon;
        Profiles = profiles.Select(p => p.Copy()).ToList();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(14, 10, 14, 10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        var intro = Dialogs.Intro("Kayıtlı sunucular", "Profil ekleyin, kopyalayın veya düzenleyin. Hatırlanan parolalar Windows hesabınıza bağlı olarak şifrelenir.");
        layout.Controls.Add(intro, 0, 0); layout.SetColumnSpan(intro, 2);
        layout.Controls.Add(Dialogs.Framed(list, new Padding(0, 0, 6, 0)), 0, 1); layout.Controls.Add(Dialogs.Framed(Dialogs.StyleGrid(editor), new Padding(6, 0, 0, 0)), 1, 1);
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        var add = Dialogs.Button("＋  Yeni", Theme.Tone.Green); var copy = Dialogs.Button("Kopyala", Theme.Tone.Blue); var remove = Dialogs.Button("Sil", Theme.Tone.Red);
        left.Controls.AddRange([add, copy, remove]); layout.Controls.Add(left, 0, 2);
        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        var save = Dialogs.Button("Kaydet"); var cancel = Dialogs.Button("Vazgeç", Theme.Tone.Neutral); cancel.DialogResult = DialogResult.Cancel;
        right.Controls.AddRange([save, cancel]); layout.Controls.Add(right, 1, 2); Controls.Add(layout); CancelButton = cancel;
        list.DrawItem += DrawProfile;
        list.SelectedIndexChanged += (_, _) => editor.SelectedObject = Selected;
        editor.PropertyValueChanged += (_, e) =>
        {
            if (Selected is not { } selected) return;
            if (e.ChangedItem?.PropertyDescriptor?.Name == nameof(ServerProfile.Protocol)) { selected.Port = selected.Protocol switch { ServerProtocol.SFTP => 22, ServerProtocol.ImplicitFTPS => 990, _ => 21 }; editor.Refresh(); }
            int index = list.SelectedIndex; list.Items[index] = selected;
        };
        add.Click += (_, _) => { var item = new ServerProfile { Name = "Yeni sunucu " + (Profiles.Count + 1) }; Profiles.Add(item); RefreshList(item); };
        copy.Click += (_, _) => { if (Selected is not { } selected) return; var item = selected.Copy(); item.Id = Guid.NewGuid(); item.Name += " kopya"; Profiles.Add(item); RefreshList(item); };
        remove.Click += (_, _) => { if (Selected is { } selected && MessageBox.Show(this, $"'{selected.Name}' profili silinsin mi? Kuyruktaki işler korunur.", "Profili sil", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) { Profiles.Remove(selected); RefreshList(); } };
        save.Click += (_, _) =>
        {
            editor.SelectedObject = null;
            try
            {
                foreach (var profile in Profiles) profile.Validate();
                if (Profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Profiles.Count) throw new InvalidOperationException("Profil adları benzersiz olmalı.");
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Profil kontrolü"); editor.SelectedObject = Selected; }
        };
        RefreshList();
        if (current != null && Profiles.Count == 0) { Profiles.Add(current.Copy()); RefreshList(Profiles[0]); }
    }
    // Two-line entry: profile name, then protocol and user@host:port.
    private void DrawProfile(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || list.Items[e.Index] is not ServerProfile profile) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Theme.AccentSoft : Theme.Surface)) e.Graphics.FillRectangle(back, e.Bounds);
        if (selected) using (var mark = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(mark, e.Bounds.X, e.Bounds.Y + 6, 3, e.Bounds.Height - 12);
        using var bold = new Font(Font, FontStyle.Bold);
        var name = e.Bounds with { X = e.Bounds.X + 12, Y = e.Bounds.Y + 5, Width = e.Bounds.Width - 18, Height = e.Bounds.Height / 2 - 2 };
        TextRenderer.DrawText(e.Graphics, profile.Name, bold, name, Theme.Ink, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        string detail = profile.Host.Length == 0 ? "Adres girilmedi" : $"{profile.Protocol} · {(profile.User.Length > 0 ? profile.User + "@" : "")}{profile.Host}:{profile.Port}";
        TextRenderer.DrawText(e.Graphics, detail, Font, name with { Y = name.Bottom + 2 }, Theme.Muted, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using var line = new Pen(Theme.Line); e.Graphics.DrawLine(line, e.Bounds.Left + 12, e.Bounds.Bottom - 1, e.Bounds.Right - 8, e.Bounds.Bottom - 1);
    }
    private void RefreshList(ServerProfile? selected = null)
    {
        list.Items.Clear(); foreach (var profile in Profiles) list.Items.Add(profile);
        if (selected != null) list.SelectedItem = selected; else if (list.Items.Count > 0) list.SelectedIndex = 0;
        editor.SelectedObject = Selected;
    }
}

public sealed class SettingsDialog : Form
{
    public TransferSettings Settings { get; }
    public SettingsDialog(TransferSettings settings)
    {
        Settings = settings.Copy(); Text = "Aktarım ayarları"; Size = new Size(640, 540); MinimumSize = new Size(540, 460); StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f); AutoScaleMode = AutoScaleMode.Dpi; BackColor = Theme.Canvas; ForeColor = Theme.Ink; if (Theme.AppIcon is { } appIcon) Icon = appIcon;
        var grid = Dialogs.StyleGrid(new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = Settings, ToolbarVisible = false, PropertySort = PropertySort.NoSort });
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(14, 10, 14, 10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.Controls.Add(Dialogs.Intro("Aktarım ayarları", "Eşzamanlılık, yeniden deneme ve hız sınırları yeni başlayan aktarımlara uygulanır."), 0, 0);
        layout.Controls.Add(Dialogs.Framed(grid, Padding.Empty), 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        var save = Dialogs.Button("Kaydet"); var cancel = Dialogs.Button("Vazgeç", Theme.Tone.Neutral); cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([save, cancel]); layout.Controls.Add(buttons, 0, 2); Controls.Add(layout); CancelButton = cancel;
        save.Click += (_, _) => { grid.SelectedObject = null; try { Settings.Validate(); DialogResult = DialogResult.OK; Close(); } catch (Exception ex) { MessageBox.Show(this, ex.Message); grid.SelectedObject = Settings; } };
    }
}

// Shared building blocks for the themed dialogs.
internal static class Dialogs
{
    public static Control Intro(string title, string text)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = Theme.Ink, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }, 0, 0);
        panel.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, ForeColor = Theme.Muted, AutoEllipsis = true, Margin = Padding.Empty }, 0, 1);
        return panel;
    }
    public static Control Framed(Control content, Padding margin)
    {
        var frame = new Panel { Dock = DockStyle.Fill, Margin = margin, Padding = new Padding(1) }; Theme.Card(frame);
        content.Dock = DockStyle.Fill; frame.Controls.Add(content); return frame;
    }
    public static PropertyGrid StyleGrid(PropertyGrid grid)
    {
        grid.LineColor = Theme.HeaderCell; grid.CategoryForeColor = Theme.Muted; grid.CategorySplitterColor = Theme.HeaderCell; grid.ViewBorderColor = Theme.Surface;
        grid.HelpBackColor = Theme.Stripe; grid.HelpForeColor = Theme.Ink; grid.HelpBorderColor = Theme.Line; grid.SelectedItemWithFocusBackColor = Theme.Accent; grid.ViewBackColor = Theme.Surface;
        return grid;
    }
    // Without a tone the button is the dialog's single solid primary action (Kaydet).
    public static Button Button(string text, Theme.Tone? tone = null)
    {
        // Sized to fit three buttons in the 280 px profile column without clipping.
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(84, 32), Margin = new Padding(0, 0, 6, 0), Padding = new Padding(6, 0, 6, 0) };
        if (tone is { } t) Theme.Style(button, t); else Theme.Style(button, Theme.ButtonKind.Primary); return button;
    }
}
