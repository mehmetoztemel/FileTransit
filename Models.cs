using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileTransit;

public enum ServerProtocol { FTPS, FTP, ImplicitFTPS, SFTP }
public enum ConflictPolicy { Skip, Overwrite }
public enum JobState { Waiting, Running, Completed, Failed, Paused, Skipped, Cancelled }

public sealed class ServerProfile
{
    [Browsable(false)] public Guid Id { get; set; } = Guid.NewGuid();
    [Category("1 · Sunucu"), DisplayName("Profil adı")] public string Name { get; set; } = "Yeni sunucu";
    [Category("1 · Sunucu"), DisplayName("Sunucu adresi")] public string Host { get; set; } = "";
    [Category("1 · Sunucu"), DisplayName("Protokol")] public ServerProtocol Protocol { get; set; }
    [Category("1 · Sunucu"), DisplayName("Port"), Description("FTP/FTPS: 21, implicit FTPS: 990, SFTP: 22")] public int Port { get; set; } = 21;
    [Category("2 · Kimlik"), DisplayName("Kullanıcı adı")] public string User { get; set; } = "";
    [JsonIgnore, Category("2 · Kimlik"), DisplayName("Parola / anahtar parolası"), PasswordPropertyText(true)] public string Password { get; set; } = "";
    [Category("2 · Kimlik"), DisplayName("Parolayı hatırla"), Description("Windows kullanıcı hesabınıza bağlı olarak şifrelenir.")] public bool RememberPassword { get; set; }
    [Category("2 · Kimlik"), DisplayName("SFTP özel anahtar dosyası"), Description("Boş bırakılırsa parola kullanılır. Şifreli anahtarın parolasını parola alanına girin.")] public string KeyFile { get; set; } = "";
    [Browsable(false)] public string ProtectedPassword { get; set; } = "";
    [Category("3 · Başlangıç"), DisplayName("Yerel klasör")] public string LocalFolder { get; set; } = "";
    [Category("3 · Başlangıç"), DisplayName("Uzak klasör")] public string RemoteFolder { get; set; } = "/";
    [Category("4 · Bağlantı"), DisplayName("Pasif FTP")] public bool Passive { get; set; } = true;
    [Category("4 · Bağlantı"), DisplayName("Zaman aşımı (sn)")] public int TimeoutSeconds { get; set; } = 30;
    [Browsable(false)] public string Endpoint => $"{Protocol}|{Host.ToLowerInvariant()}|{Port}|{User}";
    public override string ToString() => Name;
    public ServerProfile Copy() => (ServerProfile)MemberwiseClone();
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Host) || Host.Any(char.IsWhiteSpace) || Host.Contains('/') || Host.Contains('\\')) throw new InvalidOperationException("Profil adı ve geçerli bir sunucu adresi girin (protokol öneki olmadan).");
        if (Port is < 1 or > 65535 || TimeoutSeconds is < 5 or > 600) throw new InvalidOperationException("Port 1–65535, zaman aşımı 5–600 saniye olmalı.");
        if (string.IsNullOrWhiteSpace(User)) throw new InvalidOperationException("Kullanıcı adı gerekli.");
        if (Protocol == ServerProtocol.SFTP && KeyFile.Length > 0 && !File.Exists(KeyFile)) throw new FileNotFoundException("Özel anahtar dosyası bulunamadı.");
    }
    public ServerProfile ForStorage(Dictionary<string, string>? protectedCache = null)
    {
        var copy = Copy();
        if (!RememberPassword) copy.ProtectedPassword = "";
        else if (protectedCache == null) copy.ProtectedPassword = Secret.Protect(Password);
        else if (!protectedCache.TryGetValue(Password, out var cached)) copy.ProtectedPassword = protectedCache[Password] = Secret.Protect(Password);
        else copy.ProtectedPassword = cached;
        return copy;
    }
    public void Unlock() => Password = string.IsNullOrEmpty(ProtectedPassword) ? "" : Secret.Unprotect(ProtectedPassword);
}

public sealed class TransferSettings
{
    [DisplayName("Eşzamanlı aktarım"), Description("1 = sırayla; 2–4 = paralel. Her aktarım ayrı bağlantı kullanır.")] public int Parallel { get; set; } = 1;
    [DisplayName("Otomatik yeniden deneme")] public int Retries { get; set; } = 2;
    [DisplayName("Denemeler arası süre (sn)")] public int RetryDelay { get; set; } = 3;
    [DisplayName("Mevcut dosya"), Description("Skip: atla. Overwrite: üzerine yaz. Ayar yalnız yeni eklenen işlere uygulanır.")] public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Skip;
    [DisplayName("İndirme limiti (KB/sn)"), Description("Her aktarım için; 0 sınırsız.")] public int DownloadLimit { get; set; }
    [DisplayName("Yükleme limiti (KB/sn)"), Description("Her aktarım için; 0 sınırsız.")] public int UploadLimit { get; set; }
    [DisplayName("Kuyruğu otomatik başlat")] public bool AutoStart { get; set; } = true;
    [DisplayName("Yarım indirmeye devam et"), Description("Yalnız bu işin .part dosyası; uzak boyut ve tarih değiştiyse baştan indirilir.")] public bool ResumeDownloads { get; set; } = true;
    public TransferSettings Copy() => (TransferSettings)MemberwiseClone();
    public void Validate()
    {
        if (Parallel is < 1 or > 4 || Retries is < 0 or > 10 || RetryDelay is < 1 or > 120 || DownloadLimit is < 0 or > 1000000 || UploadLimit is < 0 or > 1000000) throw new InvalidOperationException("Paralel aktarım: 1–4, deneme: 0–10, bekleme: 1–120 sn, hız: 0–1.000.000 KB/sn.");
    }
}

public sealed class TransferJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ServerProfile Server { get; set; } = new();
    public string LocalPath { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public bool Upload { get; set; }
    public bool Directory { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public JobState State { get; set; }
    public ConflictPolicy Conflict { get; set; }
    public string Error { get; set; } = "";
    public int Attempts { get; set; }
    public bool UploadStarted { get; set; }
    [JsonIgnore] public double Percent { get; set; }
    [JsonIgnore] public double BytesPerSecond { get; set; }
    [JsonIgnore] public string PartialPath => LocalPath + ".filetransit-" + Id.ToString("N") + ".part";
    [JsonIgnore] public string Name => Path.GetFileName(LocalPath.TrimEnd(Path.DirectorySeparatorChar));
    [JsonIgnore] public string TargetKey => Upload ? Server.Endpoint + "|" + RemotePath : Path.GetFullPath(LocalPath).ToUpperInvariant();
}

internal static class Secret
{
    public static string Protect(string text) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string text) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text), null, DataProtectionScope.CurrentUser));
}

public sealed class WorkspaceState
{
    public List<ServerProfile> Profiles { get; set; } = [];
    public TransferSettings Settings { get; set; } = new();
    public List<TransferJob> Queue { get; set; } = [];
    public Dictionary<string, string> HostKeys { get; set; } = [];
}

public sealed record WorkspaceSnapshot(long Sequence, WorkspaceState State);

public sealed class WorkspaceStore(string directory)
{
    private readonly string path = Path.Combine(directory, "workspace.json");
    private readonly object gate = new();
    private long captured, written;
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileTransit");
    // Earlier data folders, newest first. Both are only read, never written:
    //   "Portly"        – this application before it was renamed to FileTransit (its queue is ours, so it is kept);
    //   "PortlyDesktop" – the separately built "Portly Desktop"/FileDock (its queue stays with that application).
    public static (string Directory, bool KeepQueue)[] LegacyDirectories =>
    [
        (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Portly"), true),
        (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortlyDesktop"), false)
    ];
    // First run only: copy profiles, settings and trusted host keys (and optionally the queue) from a legacy workspace.
    public static bool ImportLegacy(string target, string legacy, bool keepQueue = false)
    {
        string source = Path.Combine(legacy, "workspace.json"), destination = Path.Combine(target, "workspace.json");
        if (File.Exists(destination) || !File.Exists(source)) return false;
        if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(source)) is not System.Text.Json.Nodes.JsonObject state) throw new IOException("Eski çalışma alanı okunamadı.");
        if (!keepQueue) state["Queue"] = new System.Text.Json.Nodes.JsonArray();
        System.IO.Directory.CreateDirectory(target);
        File.WriteAllText(destination + ".tmp", state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(destination + ".tmp", destination);
        return true;
    }
    public WorkspaceState Load()
    {
        if (!File.Exists(path)) return new();
        var state = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(path)) ?? throw new IOException("Çalışma alanı okunamadı.");
        state.Settings.Validate();
        foreach (var profile in state.Profiles.Concat(state.Queue.Select(j => j.Server)))
            try { profile.Unlock(); } catch (CryptographicException) { profile.Password = ""; }
        foreach (var job in state.Queue.Where(j => j.State is JobState.Running or JobState.Waiting)) job.State = JobState.Paused;
        return state;
    }
    public void Save(WorkspaceState state) => Write(Capture(state));
    // Capture runs on the owning thread and copies everything mutable; Write may run on any thread.
    public WorkspaceSnapshot Capture(WorkspaceState state)
    {
        var copy = new WorkspaceState { Profiles = state.Profiles.Select(p => p.Copy()).ToList(), Settings = state.Settings.Copy(), HostKeys = new(state.HostKeys) };
        copy.Queue = state.Queue.Select(j => new TransferJob { Id = j.Id, Server = j.Server.Copy(), LocalPath = j.LocalPath, RemotePath = j.RemotePath, Upload = j.Upload, Directory = j.Directory, Size = j.Size, Modified = j.Modified, State = j.State, Conflict = j.Conflict, Error = j.Error, Attempts = j.Attempts, UploadStarted = j.UploadStarted }).ToList();
        return new(Interlocked.Increment(ref captured), copy);
    }
    public void Write(WorkspaceSnapshot snapshot)
    {
        lock (gate)
        {
            if (snapshot.Sequence <= written) return; // a newer state is already on disk
            var cache = new Dictionary<string, string>(); var state = snapshot.State;
            state.Profiles = state.Profiles.Select(p => p.ForStorage(cache)).ToList();
            foreach (var job in state.Queue) job.Server = job.Server.ForStorage(cache);
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
            written = snapshot.Sequence;
        }
    }
}

public static class RemotePaths
{
    public static string Join(string parent, string name) => parent.TrimEnd('/') + "/" + name;
    public static string Parent(string path) { int i = path.TrimEnd('/').LastIndexOf('/'); return i <= 0 ? "/" : path[..i]; }
    public static string LocalChild(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' ')) throw new IOException("Windows için geçersiz uzak dosya adı: " + name);
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem)) throw new IOException("Windows için ayrılmış dosya adı: " + name);
        var result = Path.GetFullPath(Path.Combine(parent, name));
        if (!result.StartsWith(Path.GetFullPath(parent).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Dosya hedef klasörün dışında.");
        return result;
    }
}
