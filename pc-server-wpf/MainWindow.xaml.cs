using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Linq;

namespace NexusLink
{
    public partial class MainWindow : Window
    {
        private UdpClient _udpClient = null!;
        private SecureTransport _security = null!;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<TcpClient, Stream> _commandStreams = new();
        private readonly SemaphoreSlim _clientSlots = new(16, 16);
        private TcpListener _tcpListener = null!;
        private TcpListener _fileListener = null!;
        private TcpListener _fileSendListener = null!;
        private string _lastClipboardText = "";
        private bool _isRunning = true;
        
        private FileTransfers _transfers = null!;
        private AndroidBridge _bridge = null!;
        private System.Windows.Forms.NotifyIcon _tray = null!;
        private List<TcpClient> _connectedClients = new List<TcpClient>();

        
        // Global variables for sync speed and storage
        private string _lastSyncSpeed = "0 KB/s";
        private string _lastStorageText = "-- Free";
        
        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _security = SecureTransport.LoadOrCreate();
            _transfers = new FileTransfers(_security, (category, text) => Dispatcher.BeginInvoke(() => AddLog(category, text)));
            _bridge = new AndroidBridge(text => Dispatcher.BeginInvoke(() => TxtBridgeStatus.Text = text),
                text => Dispatcher.BeginInvoke(() => {
                    try {
                        _lastClipboardText = text;
                        if (text.Length > 0) System.Windows.Clipboard.SetText(text);
                        AddLog("Clipboard", "Received clipboard from phone (ADB)");
                    } catch { }
                }));
            _tray = new System.Windows.Forms.NotifyIcon {
                Icon = System.Drawing.SystemIcons.Application, Text = "NexusLink", Visible = true
            };
            _tray.DoubleClick += (_, _) => { Show(); Activate(); };
            LoadSyncSettings();
            StartUdpListener();
            StartTcpListener();
            StartClipboardMonitor();
            StartSystemStatsMonitor();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _isRunning = false;
            _bridge?.Dispose();
            _tray?.Dispose();
            _security?.Dispose();
            if (_clipboardSource != null) {
                RemoveClipboardFormatListener(_clipboardSource.Handle);
                _clipboardSource.RemoveHook(ClipboardMessage);
            }
            _udpClient?.Close();
            _tcpListener?.Stop();
            _fileListener?.Stop();
            _fileSendListener?.Stop();
            foreach (var client in _connectedClients.ToArray()) { try { client.Close(); } catch { } }
        }

        private async void StartUdpListener()
        {
            try
            {
                _udpClient = new UdpClient(5051);
                while (_isRunning)
                {
                    var result = await _udpClient.ReceiveAsync();
                    string message = Encoding.UTF8.GetString(result.Buffer);
                    if (message == "NEXUS_DISCOVER")
                    {
                        byte[] reply = Encoding.UTF8.GetBytes("NEXUS_SERVER_HERE");
                        await _udpClient.SendAsync(reply, reply.Length, result.RemoteEndPoint);
                        
                        Dispatcher.Invoke(() => AddLog("Network", $"Discovery received from {result.RemoteEndPoint.Address}"));
                    }
                }
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    Dispatcher.Invoke(() => AddLog("Error", "UDP Error: " + ex.Message));
                }
            }
        }

        private async void StartTcpListener()
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, 5050);
                _tcpListener.Start();
                _fileListener = new TcpListener(IPAddress.Any, 5052);
                _fileListener.Start();
                _fileSendListener = new TcpListener(IPAddress.Any, 5053);
                _fileSendListener.Start();
                
                _ = AcceptFileClientsAsync();
                _ = AcceptFileDownloadClientsAsync();
                
                while (_isRunning)
                {
                    var client = await _tcpListener.AcceptTcpClientAsync();
                    client.NoDelay = true;
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    if (!_clientSlots.Wait(0)) { client.Close(); continue; }
                    _ = AuthenticateClientAsync(client);
                }
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    Dispatcher.Invoke(() => AddLog("Error", "TCP Error: " + ex.Message));
                }
            }
        }

        private async Task AuthenticateClientAsync(TcpClient client)
        {
            try {
                var stream = await _security.AcceptAsync(client);
                _commandStreams[client] = stream;
                lock (_connectedClients) _connectedClients.Add(client);
                await HandleClientAsync(client, stream);
            }
            catch { client.Close(); }
            finally { _commandStreams.TryRemove(client, out _); _clientSlots.Release(); }
        }

        private Stream CommandStream(TcpClient client) => _commandStreams.TryGetValue(client, out var stream)
            ? stream : throw new IOException("Device is not paired");

        private async Task HandleClientAsync(TcpClient client, Stream secureStream)
        {
            string remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
            Dispatcher.Invoke(() => AddLog("Connection", $"Device connected: {remoteIp}"));
            
            CancellationTokenSource cts = new CancellationTokenSource();
            _ = Task.Run(async () => {
                while (_isRunning && client.Connected && !cts.Token.IsCancellationRequested)
                {
                    try {
                        await Task.Delay(5000, cts.Token);
                        string hostname = System.Net.Dns.GetHostName();
                    
                        // Default values
                        string deviceType = "Desktop";
                        int batteryLevel = 100;
                        int isCharging = 1;
                        
                        try {
                            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                            if (ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) {
                                deviceType = "Laptop";
                                batteryLevel = (int)(ps.BatteryLifePercent * 100);
                                isCharging = (ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online) ? 1 : 0;
                            }
                        } catch { }

                        string infoPacket = $"INFO:{hostname}:{batteryLevel}:{isCharging}:{deviceType}:{_lastStorageText}:{_lastSyncSpeed}\n";
                        await Wire.SendAsync(CommandStream(client), infoPacket.TrimEnd('\n'), cts.Token);
                    } catch { break; }
                }
            });

            try
            {
                using (var stream = secureStream)
                {
                    while (_isRunning)
                    {
                        using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        string data = await Wire.ReadAsync(stream, idle.Token);
                        ProcessCommand(data);
                    }
                }
            }
            catch { }
            finally
            {
                lock (_connectedClients) { _connectedClients.Remove(client); }
                cts.Cancel();
                client.Close();
                Dispatcher.Invoke(() => {
                    if (_connectedClients.Count == 0)
                    {
                        TxtDeviceName.Text = "Waiting for device...";
                        IconDevice.Visibility = Visibility.Collapsed;
                        TxtBatteryLvl.Text = "--";
                        TxtBatteryState.Text = "Disconnected";
                    }
                });
            }
        }

        private void ProcessCommand(string data)
        {
            if (data.StartsWith("INFO:"))
            {
                // INFO:hostname:battery:isCharging:type
                var parts = data.Split(':');
                if (parts.Length >= 5)
                {
                    string hostname = parts[1];
                    string batteryStr = parts[2];
                    string isChargingStr = parts[3];
                    string type = parts[4];
                    
                    Dispatcher.Invoke(() => {
                        TxtDeviceName.Text = hostname;
                        IconDevice.Visibility = Visibility.Visible;
                        
                        string hostLower = hostname.ToLower();
                        if (hostLower.Contains("pc") || hostLower.Contains("desktop"))
                            IconDevice.Kind = MaterialDesignThemes.Wpf.PackIconKind.Monitor;
                        else if (hostLower.Contains("laptop") || hostLower.Contains("macbook") || hostLower.Contains("thinkpad"))
                            IconDevice.Kind = MaterialDesignThemes.Wpf.PackIconKind.Laptop;
                        else
                            IconDevice.Kind = MaterialDesignThemes.Wpf.PackIconKind.Cellphone;

                        bool isCharging = isChargingStr == "1";
                        if (int.TryParse(batteryStr, out int batLvl))
                        {
                            TxtBatteryLvl.Text = $"{batLvl}%";
                            BatteryFill.Width = 56.0 * (batLvl / 100.0);
                            
                            // Set battery color (always blue)
                            BatteryFill.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#00459A"));
                        }
                        
                        TxtBatteryState.Text = isCharging ? "Charging via USB/AC" : "On Battery";
                    });
                }
            }

            else if (data.StartsWith("STATS:"))
            {
                var parts = data.Split(':');
                if (parts.Length >= 4)
                {
                    string storageText = parts[1];
                    string syncSpeed = parts[3];
                    if (int.TryParse(parts[2], out int storagePercent))
                    {
                        Dispatcher.Invoke(() => {
                            TxtStorageFree.Text = storageText;
                            ProgStorage.Value = storagePercent;
                            TxtSyncSpeed.Text = syncSpeed;
                        });
                    }
                }
            }
            else if (data.StartsWith("CLIP:"))
            {
                string text = data.Substring(5);
                Dispatcher.Invoke(() => {
                    _lastClipboardText = text;
                    try { if (text.Length > 0) System.Windows.Clipboard.SetText(text); } catch { }
                    AddLog("Clipboard", "Received clipboard from phone");
                });
            }
            else if (data.StartsWith("NOTIFY:"))
            {
                using var notification = System.Text.Json.JsonDocument.Parse(data.Substring(7));
                string app = notification.RootElement.GetProperty("app").GetString() ?? "Android";
                string title = notification.RootElement.GetProperty("title").GetString() ?? "";
                string text = notification.RootElement.GetProperty("text").GetString() ?? "";
                Dispatcher.BeginInvoke(() => {
                    AddLog("Notification", $"{app}: {title} — {text}");
                    if (ChkDesktopNotifications.IsChecked == true)
                        _tray.ShowBalloonTip(5000, (app + " · " + title).Substring(0, Math.Min(63, app.Length + title.Length + 3)),
                            string.IsNullOrEmpty(text) ? "New phone notification" : text.Substring(0, Math.Min(255, text.Length)), System.Windows.Forms.ToolTipIcon.Info);
                });
            }
        }

        private async Task AcceptFileClientsAsync()
        {
            while (_isRunning)
            {
                try { var client = await _fileListener.AcceptTcpClientAsync(); _ = _transfers.ReceiveAsync(client); }
                catch when (!_isRunning) { break; }
                catch (Exception e) { _ = Dispatcher.BeginInvoke(() => AddLog("Error", e.Message)); }
            }
        }
        private async Task AcceptFileDownloadClientsAsync()
        {
            while (_isRunning)
            {
                try { var client = await _fileSendListener.AcceptTcpClientAsync(); _ = _transfers.SendAsync(client); }
                catch when (!_isRunning) { break; }
                catch (Exception e) { _ = Dispatcher.BeginInvoke(() => AddLog("Error", e.Message)); }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AddClipboardFormatListener(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RemoveClipboardFormatListener(IntPtr handle);
        private System.Windows.Interop.HwndSource? _clipboardSource;
        private void StartClipboardMonitor()
        {
            _clipboardSource = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            _clipboardSource.AddHook(ClipboardMessage);
            AddClipboardFormatListener(_clipboardSource.Handle);
        }
        private IntPtr ClipboardMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x031D) _ = PublishClipboardAsync();
            return IntPtr.Zero;
        }
        private async Task PublishClipboardAsync()
        {
            // Clipboard can briefly be locked by the copying app; retry off the message handler.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try {
                    if (!System.Windows.Clipboard.ContainsText()) return;
                    string text = System.Windows.Clipboard.GetText();
                    if (string.IsNullOrEmpty(text) || text == _lastClipboardText) return;
                    _lastClipboardText = text;
                    try { await _bridge.SendClipboardAsync(text); } catch { }
                    await BroadcastAsync("CLIP:" + text);
                    AddLog("Clipboard", "Clipboard synchronized");
                    return;
                }
                catch { await Task.Delay(30); }
            }
        }
        private async Task BroadcastAsync(string packet)
        {
            TcpClient[] clients;
            lock (_connectedClients) clients = _connectedClients.ToArray();
            await Task.WhenAll(clients.Select(async client => {
                try {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await Wire.SendAsync(CommandStream(client), packet, timeout.Token);
                } catch { client.Close(); }
            }));
        }

        private async void StartSystemStatsMonitor()
        {
            long lastBytesSent = 0;
            long lastBytesReceived = 0;
            bool firstRun = true;

            while (_isRunning)
            {
                try
                {
                    NetworkInterface? activeAdapter = null;
                    long currentSent = 0;
                    long currentReceived = 0;
                    
                    foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                        {
                            if (activeAdapter == null) activeAdapter = adapter;
                            
                            var stats = adapter.GetIPv4Statistics();
                            currentSent += stats.BytesSent;
                            currentReceived += stats.BytesReceived;
                        }
                    }

                    long diffSent = currentSent - lastBytesSent;
                    long diffRecv = currentReceived - lastBytesReceived;
                    lastBytesSent = currentSent;
                    lastBytesReceived = currentReceived;

                    string netName = activeAdapter != null ? activeAdapter.Name : "No Network";
                    
                    double speedKbps = (diffSent + diffRecv) / 1024.0;
                    string speedText = speedKbps > 1024 ? $"{(speedKbps / 1024.0):F1} MB/s" : $"{speedKbps:F0} KB/s";
                    if (firstRun) { speedText = "0 KB/s"; firstRun = false; }

                    // Storage
                    DriveInfo drive = new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory)!);
                    string storageText = "-- Free";
                    double storagePercent = 0;
                    if (drive.IsReady)
                    {
                        double freeGB = drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
                        double totalGB = drive.TotalSize / 1024.0 / 1024.0 / 1024.0;
                        double percent = (freeGB / totalGB) * 100;
                        storageText = $"{percent:F0}% Free ({freeGB:F1} GB)";
                        storagePercent = 100 - percent; // Value for ProgressBar (used space)
                    }

                    _lastSyncSpeed = speedText;
                    _lastStorageText = storageText;

                    Dispatcher.Invoke(() =>
                    {
                        TxtNetworkName.Text = netName;
                        // TxtSyncSpeed.Text = speedText;
                        // TxtStorageFree.Text = storageText;
                        // ProgStorage.Value = storagePercent;
                    });
                }
                catch { }

                await Task.Delay(1000);
            }
        }

        private void AddLog(string category, string message)
        {
            if (category != "File" && category != "Clipboard" && category != "Notification" && category != "Error") return;
            
            if (ListActivity.Children.Count > 0 && ListActivity.Children[0] is System.Windows.Controls.TextBlock tb && tb.Text == "No activity yet.")
            {
                ListActivity.Children.Clear();
            }

            var item = new System.Windows.Controls.Border {
                Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F3F4F5")),
                CornerRadius = new System.Windows.CornerRadius(8),
                Padding = new System.Windows.Thickness(12),
                Margin = new System.Windows.Thickness(0, 0, 0, 8)
            };
            
            var sp = new System.Windows.Controls.StackPanel();
            sp.Children.Add(new System.Windows.Controls.TextBlock { Text = $"{category}", FontWeight = System.Windows.FontWeights.Medium, Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#191C1D")) });
            sp.Children.Add(new System.Windows.Controls.TextBlock { Text = message, Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#727785")), FontSize = 12, Margin = new System.Windows.Thickness(0, 4, 0, 0) });
            item.Child = sp;
            var border = new System.Windows.Controls.Border {
                Height = 1,
                Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E1E3E4")),
                Margin = new System.Windows.Thickness(0, 8, 0, 8)
            };
            
            ListActivity.Children.Insert(0, border);
            ListActivity.Children.Insert(0, item);
            if (ListActivity.Children.Count > 10)
            {
                ListActivity.Children.RemoveRange(10, ListActivity.Children.Count - 10);
            }
        }

        private async void BtnOpenGallery_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog();
            ofd.Title = "Select File to Send";
            if (ofd.ShowDialog() == true)
            {
                string filePath = ofd.FileName;
                var fi = new FileInfo(filePath);
                TcpClient[] clients;
                lock (_connectedClients) clients = _connectedClients.ToArray();
                if (clients.Length != 1) {
                    System.Windows.MessageBox.Show("Hubungkan tepat satu HP untuk memilih tujuan transfer.", "NexusLink");
                    return;
                }
                try {
                    var client = clients[0];
                    string ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await Wire.SendAsync(CommandStream(client), _transfers.Offer(filePath, ip), timeout.Token);
                } catch (Exception ex) { AddLog("Error", ex.Message); }
            }
        }

        private async void BtnMirrorScreen_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { await _bridge.MirrorAsync(); }
            catch (Exception ex) { TxtBridgeStatus.Text = ex.Message; }
        }

        private void CopyPairing_Click(object sender, RoutedEventArgs e)
        {
            try {
                string code = _security.PairingCode;
                _lastClipboardText = code; // Never broadcast pairing credentials through clipboard sync.
                System.Windows.Clipboard.SetText(code);
                TxtBridgeStatus.Text = "Kode pairing disalin. Tempel di Settings NexusLink HP melalui jalur tepercaya.";
            } catch (Exception ex) { AddLog("Error", ex.Message); }
        }

        private bool _settingsLoaded;
        private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusLink", "sync.json");
        private void LoadSyncSettings()
        {
            try {
                if (File.Exists(SettingsPath)) {
                    using var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath));
                    var root = settings.RootElement;
                    ChkAutoClipboard.IsChecked = root.GetProperty("clipboard").GetBoolean();
                    TxtAdbDevice.Text = root.GetProperty("device").GetString();
                    ChkDesktopNotifications.IsChecked = root.GetProperty("notifications").GetBoolean();
                    CmbMirrorProfile.SelectedIndex = root.GetProperty("profile").GetInt32() == 1 ? 1 : 0;
                }
            } catch { }
            _settingsLoaded = true;
            ApplySyncSettings(null, null);
        }
        private void ApplySyncSettings(object? sender, RoutedEventArgs? e)
        {
            if (!_settingsLoaded || _bridge == null) return;
            string profile = (CmbMirrorProfile.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Wi-Fi cepat";
            _bridge.Configure(ChkAutoClipboard.IsChecked == true, TxtAdbDevice.Text, profile);
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(new {
                    clipboard = ChkAutoClipboard.IsChecked == true, device = TxtAdbDevice.Text,
                    notifications = ChkDesktopNotifications.IsChecked == true, profile = CmbMirrorProfile.SelectedIndex
                }));
            } catch (Exception ex) { AddLog("Error", ex.Message); }
        }
    }
}
