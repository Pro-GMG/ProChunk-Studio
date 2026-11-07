using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace ProChunk.App.Engine;

/// <summary>
/// Resmi Minecraft sunucusunu (Java server.jar / Bedrock Dedicated Server) arka planda çalıştırıp
/// seçili chunk'ları oyunun kendi arazi üreticisiyle ürettiren motorların ortak altyapısı.
/// </summary>
public abstract class ServerEngine
{
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProChunkStudio", "Servers");

    private static readonly string EulaFile = Path.Combine(Root, "eula-accepted.txt");

    public static bool EulaAccepted => File.Exists(EulaFile);
    public static void AcceptEula() { Directory.CreateDirectory(Root); File.WriteAllText(EulaFile, $"Kabul edildi: {DateTime.Now:O}\nhttps://aka.ms/MinecraftEULA\n"); }

    protected static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static ServerEngine() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("ProChunkStudio/1.0");

    /// <summary>Üretimi çalıştırır; üretilmiş dünyanın (kopya) klasör yolunu döndürür.</summary>
    public abstract Task<string> GenerateAsync(string worldRoot, int dimension, IReadOnlyCollection<(int X, int Z)> chunks,
        IProgress<string> progress, CancellationToken ct);

    // ---------- Yardımcılar ----------
    protected static async Task DownloadAsync(string url, string path, IProgress<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        var tmp = path + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long done = 0; int n; var last = DateTime.MinValue;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if ((DateTime.Now - last).TotalMilliseconds > 300)
                {
                    last = DateTime.Now;
                    progress.Report(total is > 0 ? $"İndiriliyor… %{done * 100 / total.Value} ({done / 1048576} / {total.Value / 1048576} MB)" : $"İndiriliyor… {done / 1048576} MB");
                }
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    protected static int FreePort(ProtocolType proto = ProtocolType.Tcp)
    {
        using var s = proto == ProtocolType.Udp
            ? new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    protected static void CopyDirectory(string src, string dst, Func<string, bool>? skip = null)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
            if (skip?.Invoke(f) != true) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)), skip);
    }

    /// <summary>Chunk listesini en fazla size×size'lık dikdörtgen gruplara böler.</summary>
    protected static List<(int MinX, int MinZ, int MaxX, int MaxZ)> Rectangles(IEnumerable<(int X, int Z)> chunks, int size)
    {
        return chunks.GroupBy(c => (Math.Floor(c.X / (double)size), Math.Floor(c.Z / (double)size)))
            .Select(g => (g.Min(c => c.X), g.Min(c => c.Z), g.Max(c => c.X), g.Max(c => c.Z)))
            .ToList();
    }

    /// <summary>Konsol girdisi/çıktısı yönlendirilmiş sunucu süreci.</summary>
    protected sealed class ServerProcess : IDisposable
    {
        private readonly Process _p;
        private readonly List<string> _lines = new();
        private readonly object _lock = new();
        private readonly IProgress<string> _progress;

        public ServerProcess(string exe, string args, string workDir, IProgress<string> progress)
        {
            _progress = progress;
            _p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                },
                EnableRaisingEvents = true,
            };
            _p.OutputDataReceived += (_, e) => OnLine(e.Data);
            _p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            _p.Start();
            _p.BeginOutputReadLine();
            _p.BeginErrorReadLine();
        }

        private void OnLine(string? line)
        {
            if (line is null) return;
            lock (_lock) _lines.Add(line);
            var t = line.Trim();
            if (t.Length > 0) _progress.Report("Sunucu: " + (t.Length > 140 ? t[..140] + "…" : t));
        }

        public bool HasExited => _p.HasExited;
        public int LineCount { get { lock (_lock) return _lines.Count; } }
        public string Tail(int n) { lock (_lock) return string.Join("\n", _lines.TakeLast(n)); }

        public void Send(string command)
        {
            if (_p.HasExited) throw new InvalidOperationException("Sunucu beklenmedik şekilde kapandı:\n" + Tail(15));
            _p.StandardInput.WriteLine(command);
            _p.StandardInput.Flush();
        }

        /// <summary>Verilen satırdan sonra koşulu sağlayan bir çıktı satırı gelene kadar bekler.</summary>
        public async Task WaitForAsync(Func<string, bool> match, int fromLine, TimeSpan timeout, CancellationToken ct, string what)
        {
            var end = DateTime.Now + timeout;
            while (DateTime.Now < end)
            {
                ct.ThrowIfCancellationRequested();
                lock (_lock)
                    for (int i = fromLine; i < _lines.Count; i++)
                        if (match(_lines[i])) return;
                if (_p.HasExited) throw new InvalidOperationException($"Sunucu {what} beklenirken kapandı:\n" + Tail(20));
                await Task.Delay(200, ct);
            }
            throw new TimeoutException($"Sunucu {what} için zaman aşımı:\n" + Tail(15));
        }

        public async Task WaitExitAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try { await _p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { _p.Kill(true); } catch { } }
        }

        public void Dispose()
        {
            try { if (!_p.HasExited) _p.Kill(true); } catch { }
            _p.Dispose();
        }
    }
}
