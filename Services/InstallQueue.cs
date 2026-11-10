using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace JavaDeck.Services;

public enum JobState { Queued, Downloading, Extracting, Done, Failed, Cancelled }

/// <summary>一个下载/安装任务。</summary>
public sealed class InstallJob : INotifyPropertyChanged
{
    public LibericaRelease Release { get; init; } = new();
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public JobKind Kind { get; init; } = JobKind.Install;

    /// <summary>更新任务：要替换掉的旧 JDK（成功后删除）。</summary>
    public InstalledJdk? ReplacedJdk { get; init; }

    /// <summary>卸载任务：要删除的 JDK。</summary>
    public InstalledJdk? Target { get; init; }

    public string KindText => Kind switch
    {
        JobKind.Update => "更新",
        JobKind.Uninstall => "卸载",
        _ => "安装",
    };

    public System.Windows.Media.Brush KindBrush => Kind switch
    {
        JobKind.Update => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB2, 0x6A, 0x00)),
        JobKind.Uninstall => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0x6F, 0xE0)),
    };

    private JobState _state = JobState.Queued;
    public JobState State
    {
        get => _state;
        set { _state = value; Notify(); Notify(nameof(StateText)); Notify(nameof(IsFinished)); }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set { _progress = value; Notify(); Notify(nameof(ProgressText)); }
    }

    private string _detail = "";
    public string Detail
    {
        get => _detail;
        set { _detail = value; Notify(); }
    }

    private string _error = "";
    public string Error
    {
        get => _error;
        set { _error = value; Notify(); }
    }

    public CancellationTokenSource Cts { get; } = new();

    public string Title => Kind == JobKind.Uninstall
        ? $"Java {Target?.FeatureVersion} · {Target?.Version}"
        : Release.DisplayName;

    public string Subtitle => Kind == JobKind.Uninstall
        ? (Target?.Path ?? "")
        : $"{Release.Size / 1048576.0:F0} MB";

    public string StateText => State switch
    {
        JobState.Queued => "等待中",
        JobState.Downloading => "下载中",
        JobState.Extracting => "解压中",
        JobState.Done => "已完成",
        JobState.Failed => "失败",
        JobState.Cancelled => "已取消",
        _ => "",
    };

    public string ProgressText => State == JobState.Downloading
        ? $"{Progress:F0}%"
        : State == JobState.Done ? "✓" : "";

    public bool IsFinished => State is JobState.Done or JobState.Failed or JobState.Cancelled;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>安装队列：串行解压、并行下载。</summary>
public sealed class InstallQueue
{
    private readonly JdkStore _store;
    private readonly LibericaClient _api;
    private readonly SemaphoreSlim _downloadGate;
    private readonly SemaphoreSlim _extractLock = new(1, 1);

    public ObservableCollection<InstallJob> Jobs { get; } = new();

    public event Action<InstalledJdk>? Installed;
    public event Action<InstallJob>? JobFinished;

    public InstallQueue(JdkStore store, LibericaClient api, int maxParallel = 3)
    {
        _store = store;
        _api = api;
        _downloadGate = new SemaphoreSlim(Math.Max(1, maxParallel));
    }

    public void Enqueue(IEnumerable<LibericaRelease> releases, JobKind kind = JobKind.Install,
                        Func<LibericaRelease, InstalledJdk?>? replacedSelector = null)
    {
        foreach (var r in releases)
        {
            if (Jobs.Any(j => j.Release.FileName == r.FileName && !j.IsFinished)) continue;
            var job = new InstallJob
            {
                Release = r,
                Kind = kind,
                ReplacedJdk = replacedSelector?.Invoke(r),
            };
            Jobs.Add(job);
            _ = StartAsync(job);
        }
    }

    /// <summary>卸载任务也走队列，这样进度可见、可取消。</summary>
    public void EnqueueUninstall(InstalledJdk jdk)
    {
        if (Jobs.Any(j => j.Kind == JobKind.Uninstall && j.Target?.Path == jdk.Path && !j.IsFinished)) return;
        var job = new InstallJob
        {
            Kind = JobKind.Uninstall,
            Target = jdk,
            Release = new LibericaRelease { Version = jdk.Version, FeatureVersion = jdk.FeatureVersion },
        };
        Jobs.Add(job);
        _ = RunUninstallAsync(job);
    }

    private async Task RunUninstallAsync(InstallJob job)
    {
        try
        {
            job.State = JobState.Extracting;
            job.Detail = "正在删除…";
            var target = job.Target!;

            // 从 PATH 移掉
            var bin = Path.Combine(target.Path, "bin");
            var raw = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            var kept = raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
                          .Where(p => !string.Equals(p.Trim().TrimEnd('\\'), bin.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            Environment.SetEnvironmentVariable("PATH", string.Join(";", kept), EnvironmentVariableTarget.User);

            // 若它就是 JAVA_HOME，清掉
            var home = EnvManager.GetJdkHome(out _);
            if (!string.IsNullOrEmpty(home) &&
                string.Equals(Junction.ResolveTarget(home).TrimEnd('\\'), target.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("JAVA_HOME", "", EnvironmentVariableTarget.User);

            EnvManager.Broadcast();

            await Task.Run(() => _store.Delete(target, null), job.Cts.Token);

            job.State = JobState.Done;
            job.Progress = 100;
            job.Detail = "已删除 " + target.Path;
        }
        catch (OperationCanceledException) { job.State = JobState.Cancelled; }
        catch (Exception ex) { job.State = JobState.Failed; job.Error = ex.Message; }
        finally { JobFinished?.Invoke(job); }
    }

    public void Cancel(InstallJob job)
    {
        try { job.Cts.Cancel(); } catch { }
        if (job.State == JobState.Queued) job.State = JobState.Cancelled;
    }

    public void ClearFinished()
    {
        for (var i = Jobs.Count - 1; i >= 0; i--)
            if (Jobs[i].IsFinished) Jobs.RemoveAt(i);
    }

    private async Task StartAsync(InstallJob job)
    {
        var dir = Path.Combine(Path.GetTempPath(), "JavaDeck");
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, job.Release.FileName);

        await _downloadGate.WaitAsync();
        try
        {
            if (job.Cts.IsCancellationRequested) { job.State = JobState.Cancelled; return; }

            job.State = JobState.Downloading;
            if (File.Exists(zip) && new FileInfo(zip).Length == job.Release.Size)
            {
                job.Progress = 100;
                job.Detail = "使用已下载的缓存";
            }
            else
            {
                if (File.Exists(zip)) File.Delete(zip);
                var progress = new Progress<(long done, long total)>(p =>
                {
                    job.Progress = p.total > 0 ? p.done * 100.0 / p.total : 0;
                    job.Detail = $"{p.done / 1048576.0:F0}/{p.total / 1048576.0:F0} MB";
                });
                await _api.DownloadParallelAsync(job.Release.DownloadUrl, zip, job.Release.Size,
                    job.Release.Sha1, progress, job.Cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            job.State = JobState.Cancelled;
            JobFinished?.Invoke(job);
            return;
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.Error = ex.Message;
            JobFinished?.Invoke(job);
            return;
        }
        finally
        {
            _downloadGate.Release();
        }

        // 解压串行，避免磁盘打满
        await _extractLock.WaitAsync();
        try
        {
            if (job.Cts.IsCancellationRequested) { job.State = JobState.Cancelled; return; }
            job.State = JobState.Extracting;
            job.Detail = "解压中…";

            var jdk = await Task.Run(() => _store.Extract(zip, job.Release, null, job.Cts.Token), job.Cts.Token);

            // 更新/同大版本重装：删掉被替换的旧构建，避免同版本留两份
            var removedOld = new List<string>();
            if (job.ReplacedJdk != null &&
                !string.Equals(job.ReplacedJdk.Path, jdk.Path, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    job.Detail = "清理旧版本…";
                    _store.Delete(job.ReplacedJdk, null);
                    removedOld.Add(job.ReplacedJdk.Version);
                }
                catch { }
            }
            // 兜底：同一大版本下的其它目录也清掉（用户要求更新后不留旧版）
            if (job.Kind == JobKind.Update || job.ReplacedJdk != null)
            {
                try
                {
                    foreach (var other in _store.LoadAll()
                             .Where(x => x.FeatureVersion == jdk.FeatureVersion
                                      && !string.Equals(x.Path, jdk.Path, StringComparison.OrdinalIgnoreCase))
                             .ToList())
                    {
                        try { _store.Delete(other, null); removedOld.Add(other.Version); } catch { }
                    }
                }
                catch { }
            }

            // 安装完成 -> 把它加入 PATH 末尾（除非它已被设为 JAVA_HOME）
            try
            {
                var curHome = EnvManager.GetJdkHome(out _);
                var isHome = !string.IsNullOrEmpty(curHome)
                          && string.Equals(Junction.ResolveTarget(curHome).TrimEnd('\\'),
                                           jdk.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                if (!isHome) EnvManager.AddJdkToPath(jdk.Path);
            }
            catch { }

            job.State = JobState.Done;
            job.Progress = 100;
            job.Detail = jdk.Path + (removedOld.Count > 0 ? $"（已清理旧版 {string.Join(", ", removedOld)}）" : "");
            Installed?.Invoke(jdk);
            try { if (File.Exists(zip)) File.Delete(zip); } catch { }
        }
        catch (OperationCanceledException)
        {
            job.State = JobState.Cancelled;
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.Error = ex.Message;
        }
        finally
        {
            _extractLock.Release();
            JobFinished?.Invoke(job);
        }
    }
}