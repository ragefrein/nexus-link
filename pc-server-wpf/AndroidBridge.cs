using System.Diagnostics;
using System.IO;

namespace NexusLink;

/// <summary>Uses the existing, user-authorized ADB transport; no clipboard polling or focus stealing.</summary>
internal sealed class AndroidBridge : IDisposable
{
    private readonly Action<string> status;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private ClipboardChannel? clipboard;
    private readonly Action<string> onClipboard;
    private Process? mirror;
    private bool enabled;
    private string selector = "";
    private string mirrorProfile = "Wi-Fi cepat";
    private readonly string scrcpy;
    private readonly string adb;

    internal AndroidBridge(Action<string> status, Action<string> onClipboard)
    {
        this.status = status;
        this.onClipboard = onClipboard;
        string[] roots = { AppContext.BaseDirectory, Environment.CurrentDirectory };
        scrcpy = roots.SelectMany(root => new[] {
            Path.Combine(root, "scrcpy", "scrcpy-win64-v2.4", "scrcpy.exe"),
            Path.Combine(root, "scrcpy", "scrcpy.exe") }).FirstOrDefault(File.Exists) ?? "";
        adb = Path.Combine(Path.GetDirectoryName(scrcpy) ?? "", "adb.exe");
        _ = WatchAsync();
    }

    internal void Configure(bool autoClipboard, string device, string profile)
    {
        // Called on UI thread; process work is serialized by gate in the background.
        enabled = autoClipboard;
        selector = device.Trim();
        mirrorProfile = profile;
    }

    private async Task<string> AdbAsync(params string[] args)
    {
        var info = new ProcessStartInfo(adb) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("ADB could not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        string text = await output;
        string error = await errors;
        if (process.ExitCode != 0) throw new IOException(error.Trim());
        return text;
    }

    private async Task<string> SelectDeviceAsync()
    {
        if (!File.Exists(scrcpy) || !File.Exists(adb)) throw new IOException("Folder scrcpy belum tersedia di samping aplikasi.");
        string selected = selector;
        var devices = (await AdbAsync("devices")).Split('\n')
            .Select(line => line.Trim().Split('\t')).Where(parts => parts.Length == 2 && parts[1] == "device")
            .Select(parts => parts[0]).ToArray();
        if (!string.IsNullOrEmpty(selected))
        {
            if (!devices.Contains(selected) && selected.Contains(':'))
            {
                await AdbAsync("connect", selected);
                devices = (await AdbAsync("devices")).Split('\n').Select(line => line.Trim().Split('\t'))
                    .Where(parts => parts.Length == 2 && parts[1] == "device").Select(parts => parts[0]).ToArray();
            }
            if (!devices.Contains(selected)) throw new IOException("Perangkat ADB belum diotorisasi. Periksa dialog debugging di HP.");
            return selected;
        }
        if (devices.Length != 1) throw new IOException("Hubungkan satu HP via USB debugging, atau isi serial / IP:port Wireless debugging.");
        return devices[0];
    }

    private Process Start(string serial, params string[] arguments)
    {
        var info = new ProcessStartInfo(scrcpy) {
            WorkingDirectory = Path.GetDirectoryName(scrcpy)!, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        info.ArgumentList.Add("--serial=" + serial);
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new IOException("scrcpy could not start");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) => { if (e.Data?.StartsWith("ERROR:") == true) status(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task WatchAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await gate.WaitAsync(lifetime.Token);
                try
                {
                    if (!enabled) { clipboard?.Dispose(); clipboard = null; }
                    else if (clipboard == null || !clipboard.IsAlive || clipboard.Selector != selector)
                    {
                        clipboard?.Dispose(); clipboard = null;
                        var serial = await SelectDeviceAsync();
                        clipboard = await ClipboardChannel.ConnectAsync(adb, Path.Combine(Path.GetDirectoryName(scrcpy)!, "scrcpy-server"),
                            serial, selector, onClipboard, status, lifetime.Token);
                        status("Clipboard otomatis aktif melalui ADB");
                    }
                }
                finally { gate.Release(); }
                await Task.Delay(3000, lifetime.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                status(e.Message);
                try { await Task.Delay(5000, lifetime.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    internal async Task MirrorAsync()
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            if (mirror != null && !mirror.HasExited) { status("Screen mirror sudah terbuka"); return; }
            Stop(ref mirror);
            string serial = await SelectDeviceAsync();
            bool usb = mirrorProfile == "USB kualitas tinggi";
            mirror = Start(serial, "--window-title=NexusLink Mirror", "--video-codec=h264",
                usb ? "--video-bit-rate=12M" : "--video-bit-rate=4M",
                usb ? "--max-size=1920" : "--max-size=1280", "--max-fps=60",
                "--display-buffer=0", "--no-audio", "--stay-awake");
            status("Mirror aktif · H.264 / target 60 FPS / buffer 0 ms");
        }
        finally { gate.Release(); }
    }

    private static void Stop(ref Process? process)
    {
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        process.Dispose();
        process = null;
    }
    internal async Task SendClipboardAsync(string text)
    {
        var channel = clipboard;
        if (channel != null) await channel.SendAsync(text);
    }
    public void Dispose()
    {
        lifetime.Cancel();
        // Watch/Mirror may own gate; schedule cleanup after they unwind.
        _ = Task.Run(async () => {
            await gate.WaitAsync();
            try { clipboard?.Dispose(); clipboard = null; Stop(ref mirror); }
            finally { gate.Release(); }
        });
    }
}
