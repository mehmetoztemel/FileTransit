using FluentFTP;
using Renci.SshNet;
using System.Diagnostics;
using System.Security.Cryptography;

namespace FileTransit;

public record RemoteEntry(string Name, string FullName, bool IsDirectory, bool IsLink, long Size, DateTime Modified);
public record TransferProgressInfo(double Percent, double BytesPerSecond);

public interface IRemoteSession : IDisposable
{
    bool IsConnected { get; }
    Task Connect(CancellationToken token);
    Task<string> WorkingDirectory(CancellationToken token);
    Task<RemoteEntry[]> List(string path, CancellationToken token);
    Task<bool> Exists(string path, CancellationToken token);
    Task CreateDirectory(string path, CancellationToken token);
    Task Rename(string source, string destination, CancellationToken token);
    Task Delete(RemoteEntry entry, CancellationToken token);
    Task<bool> Transfer(TransferJob job, TransferSettings settings, IProgress<TransferProgressInfo> progress, CancellationToken token);
}

public sealed class RemoteSession : IRemoteSession
{
    private readonly ServerProfile profile;
    private readonly AsyncFtpClient? ftp;
    private readonly SftpClient? sftp;
    private readonly PrivateKeyFile? privateKey;
    private string? hostKeyError;
    public bool IsConnected => ftp?.IsConnected ?? sftp?.IsConnected ?? false;

    public RemoteSession(ServerProfile profile, Func<string, string, bool> trustHost)
    {
        this.profile = profile.Copy();
        if (profile.Protocol == ServerProtocol.SFTP)
        {
            if (!string.IsNullOrWhiteSpace(profile.KeyFile))
            {
                privateKey = string.IsNullOrEmpty(profile.Password) ? new PrivateKeyFile(profile.KeyFile) : new PrivateKeyFile(profile.KeyFile, profile.Password);
                sftp = new SftpClient(profile.Host, profile.Port, profile.User, privateKey);
            }
            else sftp = new SftpClient(profile.Host, profile.Port, profile.User, profile.Password);
            sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(profile.TimeoutSeconds);
            sftp.OperationTimeout = TimeSpan.FromSeconds(profile.TimeoutSeconds);
            sftp.HostKeyReceived += (_, e) =>
            {
                var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
                e.CanTrust = trustHost($"{profile.Host.ToLowerInvariant()}:{profile.Port}", fingerprint);
                if (!e.CanTrust) hostKeyError = "SFTP sunucu anahtarı doğrulanamadı veya kayıtlı anahtarla eşleşmedi: " + fingerprint;
            };
        }
        else
        {
            ftp = new AsyncFtpClient(profile.Host, profile.User, profile.Password, profile.Port);
            ftp.Config.EncryptionMode = profile.Protocol switch { ServerProtocol.FTPS => FtpEncryptionMode.Explicit, ServerProtocol.ImplicitFTPS => FtpEncryptionMode.Implicit, _ => FtpEncryptionMode.None };
            ftp.Config.DataConnectionType = profile.Passive ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive;
            ftp.Config.ConnectTimeout = ftp.Config.ReadTimeout = ftp.Config.DataConnectionConnectTimeout = ftp.Config.DataConnectionReadTimeout = profile.TimeoutSeconds * 1000;
        }
    }

    public async Task Connect(CancellationToken token)
    {
        try { if (ftp != null) await ftp.Connect(token); else await sftp!.ConnectAsync(token); }
        catch (Exception ex) when (hostKeyError != null) { throw new IOException(hostKeyError, ex); }
    }
    public Task<string> WorkingDirectory(CancellationToken token) => ftp != null ? ftp.GetWorkingDirectory(token) : Task.FromResult(sftp!.WorkingDirectory);
    public async Task<RemoteEntry[]> List(string path, CancellationToken token)
    {
        // Large listings are read and parsed off the UI thread.
        if (ftp != null) return await Task.Run(async () => (await ftp.GetListing(path, token)).Where(x => x.Name is not "." and not "..").Select(x => new RemoteEntry(x.Name, x.FullName, x.Type == FtpObjectType.Directory, x.Type == FtpObjectType.Link, x.Size, x.Modified)).ToArray(), token);
        return await Task.Run(() => sftp!.ListDirectory(path).Where(x => x.Name is not "." and not "..").Select(x => new RemoteEntry(x.Name, x.FullName, x.IsDirectory, x.IsSymbolicLink, x.Length, x.LastWriteTimeUtc)).ToArray(), token);
    }
    public async Task<bool> Exists(string path, CancellationToken token) => ftp != null ? await ftp.FileExists(path, token) || await ftp.DirectoryExists(path, token) : await sftp!.ExistsAsync(path, token);
    public async Task CreateDirectory(string path, CancellationToken token)
    {
        if (ftp != null) { await ftp.CreateDirectory(path, token); return; }
        string current = path.StartsWith('/') ? "/" : "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.TrimEnd('/') + "/" + segment;
            if (!await sftp!.ExistsAsync(current, token))
            {
                try { await sftp.CreateDirectoryAsync(current, token); }
                catch { if (!await sftp.ExistsAsync(current, token)) throw; }
            }
        }
    }
    public async Task Rename(string source, string destination, CancellationToken token)
    {
        if (ftp != null) await ftp.Rename(source, destination, token); else await sftp!.RenameFileAsync(source, destination, token);
    }
    public async Task Delete(RemoteEntry entry, CancellationToken token)
    {
        if (ftp != null)
        {
            if (entry.IsDirectory && !entry.IsLink) await ftp.DeleteDirectory(entry.FullName, token); else await ftp.DeleteFile(entry.FullName, token);
            return;
        }
        if (entry.IsDirectory && !entry.IsLink)
        {
            foreach (var child in await List(entry.FullName, token)) await Delete(child, token);
            await sftp!.DeleteDirectoryAsync(entry.FullName, token);
        }
        else await sftp!.DeleteFileAsync(entry.FullName, token);
    }

    public async Task<bool> Transfer(TransferJob job, TransferSettings settings, IProgress<TransferProgressInfo> progress, CancellationToken token)
    {
        if (job.Directory)
        {
            if (job.Upload) await CreateDirectory(job.RemotePath, token); else Directory.CreateDirectory(job.LocalPath);
            return true;
        }
        if (job.Upload)
        {
            if (!job.UploadStarted && job.Conflict == ConflictPolicy.Skip && await Exists(job.RemotePath, token)) return false;
            await CreateDirectory(RemotePaths.Parent(job.RemotePath), token);
            if (ftp != null)
            {
                ftp.Config.UploadRateLimit = (uint)settings.UploadLimit;
                // Retrying this job replaces its own interrupted upload, even under Skip.
                var mode = job.UploadStarted || job.Conflict == ConflictPolicy.Overwrite ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip;
                job.UploadStarted = true;
                var result = await ftp.UploadFile(job.LocalPath, job.RemotePath, mode, true, FtpVerify.None,
                    new Progress<FtpProgress>(p => progress.Report(new(p.Progress, p.TransferSpeed))), token);
                if (result == FtpStatus.Failed) throw new IOException("Yükleme başarısız.");
                return result == FtpStatus.Success;
            }
            using var input = File.OpenRead(job.LocalPath);
            var uploadMode = job.UploadStarted || job.Conflict == ConflictPolicy.Overwrite ? FileMode.Create : FileMode.CreateNew;
            using var output = await Task.Run(() => sftp!.Open(job.RemotePath, uploadMode, FileAccess.Write), token);
            job.UploadStarted = true;
            await Copy(input, output, 0, input.Length, settings.UploadLimit, progress, token);
            return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(job.LocalPath)!);
        if (job.Conflict == ConflictPolicy.Skip && File.Exists(job.LocalPath)) return false;
        // Resume only our private staging file and only if remote metadata is unchanged.
        var entry = (await List(RemotePaths.Parent(job.RemotePath), token)).FirstOrDefault(x => x.FullName == job.RemotePath || x.Name == job.RemotePath.Split('/').Last()) ?? throw new FileNotFoundException("Uzak dosya bulunamadı.");
        bool resume = settings.ResumeDownloads && job.Modified != DateTime.MinValue && entry.Size == job.Size && entry.Modified == job.Modified && File.Exists(job.PartialPath) && new FileInfo(job.PartialPath).Length <= entry.Size;
        if (!resume && File.Exists(job.PartialPath)) File.Delete(job.PartialPath);
        job.Size = entry.Size; job.Modified = entry.Modified;
        if (ftp != null)
        {
            ftp.Config.DownloadRateLimit = (uint)settings.DownloadLimit;
            var result = await ftp.DownloadFile(job.PartialPath, job.RemotePath, resume ? FtpLocalExists.Resume : FtpLocalExists.Overwrite, FtpVerify.None,
                new Progress<FtpProgress>(p => progress.Report(new(p.Progress, p.TransferSpeed))), token);
            if (result != FtpStatus.Success) throw new IOException("İndirme tamamlanamadı.");
        }
        else
        {
            using var input = await Task.Run(() => sftp!.OpenRead(job.RemotePath), token);
            using var output = new FileStream(job.PartialPath, resume ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            var offset = output.Length;
            if (offset > 0 && !input.CanSeek) { output.SetLength(0); offset = 0; }
            if (offset > 0) input.Seek(offset, SeekOrigin.Begin);
            output.Position = offset;
            await Copy(input, output, offset, job.Size, settings.DownloadLimit, progress, token);
        }
        token.ThrowIfCancellationRequested();
        if (entry.Size >= 0 && new FileInfo(job.PartialPath).Length != entry.Size) throw new IOException("İndirilen boyut uzak dosyayla eşleşmiyor.");
        File.Move(job.PartialPath, job.LocalPath, job.Conflict == ConflictPolicy.Overwrite);
        return true;
    }

    private static async Task Copy(Stream input, Stream output, long offset, long total, int limit, IProgress<TransferProgressInfo> progress, CancellationToken token)
    {
        var watch = Stopwatch.StartNew(); long sent = 0; byte[] buffer = new byte[65536];
        while (true)
        {
            int count = await input.ReadAsync(buffer, token); if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), token); sent += count;
            if (limit > 0)
            {
                var delay = TimeSpan.FromSeconds(sent / (limit * 1024d)) - watch.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            }
            progress.Report(new(total > 0 ? 100d * (offset + sent) / total : 0, sent / Math.Max(.001, watch.Elapsed.TotalSeconds)));
        }
        await output.FlushAsync(token);
    }
    public void Dispose() { ftp?.Dispose(); sftp?.Dispose(); privateKey?.Dispose(); }
}

