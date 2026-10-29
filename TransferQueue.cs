namespace FileTransit;

// All mutations run on the owning synchronization context; remote sessions are never shared.
public sealed class TransferQueue
{
    private readonly Func<ServerProfile, IRemoteSession> factory;
    private readonly Dictionary<Guid, CancellationTokenSource> active = [];
    // Destinations of running jobs, counted so Pump never scans the whole queue per candidate.
    private readonly Dictionary<string, int> activeTargets = [];
    public List<TransferJob> Jobs { get; }
    public TransferSettings Settings { get; set; }
    public bool IsRunning { get; private set; }
    public int ActiveCount => active.Count;
    public event Action? Changed;
    public event Action? ProgressChanged;
    public TransferQueue(List<TransferJob> jobs, TransferSettings settings, Func<ServerProfile, IRemoteSession> factory) { Jobs = jobs; Settings = settings; this.factory = factory; }
    public void Add(IEnumerable<TransferJob> jobs)
    {
        var pending = Jobs.Where(x => x.State is JobState.Waiting or JobState.Running or JobState.Paused).Select(x => x.TargetKey).ToHashSet();
        foreach (var job in jobs)
            if (pending.Add(job.TargetKey)) Jobs.Add(job);
        Changed?.Invoke();
        // Newly queued work must not silently resume jobs paused by the user or restored from disk.
        if (Settings.AutoStart) { IsRunning = true; Pump(); }
    }
    public void Start()
    {
        Settings.Validate();
        foreach (var job in Jobs.Where(j => j.State == JobState.Paused)) job.State = JobState.Waiting;
        IsRunning = true; Changed?.Invoke(); Pump();
    }
    public void Pause()
    {
        IsRunning = false;
        foreach (var job in Jobs.Where(j => j.State is JobState.Waiting or JobState.Running)) job.State = JobState.Paused;
        foreach (var source in active.Values) source.Cancel();
        Changed?.Invoke();
    }
    public void Retry(IEnumerable<TransferJob> jobs)
    {
        foreach (var job in jobs.Where(j => !active.ContainsKey(j.Id) && j.State is JobState.Failed or JobState.Cancelled)) { job.State = JobState.Waiting; job.Attempts = 0; job.Error = ""; }
        Changed?.Invoke(); if (IsRunning) Pump();
    }
    public void Cancel(IEnumerable<TransferJob> jobs)
    {
        foreach (var job in jobs.Where(j => j.State is JobState.Waiting or JobState.Running or JobState.Paused)) { job.State = JobState.Cancelled; if (active.TryGetValue(job.Id, out var source)) source.Cancel(); }
        Changed?.Invoke();
    }
    public void Move(TransferJob job, int direction)
    {
        if (IsRunning || active.Count > 0) return;
        int index = Jobs.IndexOf(job), next = Math.Clamp(index + direction, 0, Jobs.Count - 1);
        if (index >= 0) { Jobs.RemoveAt(index); Jobs.Insert(next, job); Changed?.Invoke(); }
    }
    public void ClearFinished()
    {
        Jobs.RemoveAll(j => !active.ContainsKey(j.Id) && j.State is JobState.Completed or JobState.Skipped);
        Changed?.Invoke();
    }
    private void Pump()
    {
        if (!IsRunning) return;
        while (active.Count < Settings.Parallel)
        {
            var job = Jobs.FirstOrDefault(j => j.State == JobState.Waiting && !activeTargets.ContainsKey(j.TargetKey));
            if (job == null) break;
            var source = new CancellationTokenSource(); active.Add(job.Id, source); activeTargets[job.TargetKey] = activeTargets.GetValueOrDefault(job.TargetKey) + 1; job.State = JobState.Running;
            _ = Execute(job, source, Settings.Copy());
        }
        if (active.Count == 0 && !Jobs.Any(j => j.State == JobState.Waiting)) IsRunning = false;
        Changed?.Invoke();
    }
    private async Task Execute(TransferJob job, CancellationTokenSource source, TransferSettings settings)
    {
        string target = job.TargetKey;
        await Task.Yield();
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                source.Token.ThrowIfCancellationRequested(); job.Attempts++; job.Error = ""; Changed?.Invoke();
                try
                {
                    using var session = factory(job.Server);
                    await session.Connect(source.Token);
                    var progress = new Progress<TransferProgressInfo>(p => { if (job.State != JobState.Running) return; job.Percent = Math.Clamp(p.Percent, 0, 100); job.BytesPerSecond = p.BytesPerSecond; ProgressChanged?.Invoke(); });
                    bool transferred = await session.Transfer(job, settings, progress, source.Token);
                    source.Token.ThrowIfCancellationRequested();
                    job.State = transferred ? JobState.Completed : JobState.Skipped; job.Percent = transferred ? 100 : 0; break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && attempt < settings.Retries && !source.IsCancellationRequested)
                {
                    job.Error = $"Yeniden denenecek: {ex.Message}"; Changed?.Invoke(); await Task.Delay(TimeSpan.FromSeconds(settings.RetryDelay), source.Token);
                }
            }
        }
        catch (OperationCanceledException) { if (job.State != JobState.Cancelled) job.State = JobState.Paused; }
        catch (Exception ex) { if (source.IsCancellationRequested) { if (job.State != JobState.Cancelled) job.State = JobState.Paused; } else { job.State = JobState.Failed; job.Error = ex.Message; } }
        finally
        {
            job.BytesPerSecond = 0; active.Remove(job.Id);
            if (activeTargets.TryGetValue(target, out int count) && count > 1) activeTargets[target] = count - 1; else activeTargets.Remove(target);
            source.Dispose(); Changed?.Invoke(); Pump();
        }
    }
}
