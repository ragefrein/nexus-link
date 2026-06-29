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

namespace SyncLinkServer
{
    public partial class MainWindow : Window
    {
        private UdpClient _udpClient;
        private TcpListener _tcpListener;
        private TcpListener _fileListener;
        private TcpListener _fileSendListener;
        private string _lastClipboardText = "";
        private bool _isRunning = true;
        
        private string _expectedFilename = "";
        private long _expectedFilesize = 0;
        private List<TcpClient> _connectedClients = new List<TcpClient>();
        private string _fileToSend = "";
        
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
            StartUdpListener();
            StartTcpListener();
            StartClipboardMonitor();
            StartSystemStatsMonitor();
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _isRunning = false;
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
                    lock (_connectedClients) { _connectedClients.Add(client); }
                    _ = HandleClientAsync(client);
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

        private async Task HandleClientAsync(TcpClient client)
        {
            string remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
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
                        byte[] infoBytes = Encoding.UTF8.GetBytes(infoPacket);
                        await client.GetStream().WriteAsync(infoBytes, 0, infoBytes.Length, cts.Token);
                    } catch { break; }
                }
            });

            try
            {
                using (var stream = client.GetStream())
                {
                    byte[] buffer = new byte[4096];
                    while (_isRunning)
                    {
                        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead == 0) break;
                        
                        string data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        ProcessCommand(data, stream);
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

        private void ProcessCommand(string data, NetworkStream stream)
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
                    try { System.Windows.Clipboard.SetText(text); } catch { }
                    AddLog("Clipboard", $"Received: {text.Substring(0, Math.Min(20, text.Length))}...");
                });
            }
            else if (data.StartsWith("FILE_REQ:"))
            {
                var parts = data.Split(':');
                if (parts.Length >= 3)
                {
                    _expectedFilename = parts[1];
                    if (long.TryParse(parts[2], out long size))
                    {
                        _expectedFilesize = size;
                        byte[] reply = Encoding.UTF8.GetBytes("FILE_ACCEPT");
                        stream.Write(reply, 0, reply.Length);
                    }
                }
            }
        }
        
        private async Task AcceptFileClientsAsync()
        {
            while (_isRunning)
            {
                try
                {
                    var fileClient = await _fileListener.AcceptTcpClientAsync();
                    if (!string.IsNullOrEmpty(_expectedFilename) && _expectedFilesize > 0)
                    {
                        string targetDir = Path.Combine(Environment.CurrentDirectory, "received_files");
                        Directory.CreateDirectory(targetDir);
                        string filePath = Path.Combine(targetDir, _expectedFilename);
                        long expectedSize = _expectedFilesize;
                        
                        _expectedFilename = "";
                        _expectedFilesize = 0;
                        
                        _ = Task.Run(async () => {
                            try {
                                using (var ns = fileClient.GetStream())
                                using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                                {
                                    byte[] buffer = new byte[65536];
                                    long totalRead = 0;
                                    while (totalRead < expectedSize)
                                    {
                                        int read = await ns.ReadAsync(buffer, 0, buffer.Length);
                                        if (read == 0) break;
                                        await fs.WriteAsync(buffer, 0, read);
                                        totalRead += read;
                                    }
                                }
                                Dispatcher.Invoke(() => AddLog("File", $"Received: {Path.GetFileName(filePath)}"));
                            }
                            catch (Exception ex) {
                                Dispatcher.Invoke(() => AddLog("Error", $"File transfer failed: {ex.Message}"));
                            }
                            finally {
                                fileClient.Close();
                            }
                        });
                    }
                    else
                    {
                        fileClient.Close();
                    }
                }
                catch { }
            }
        }
        
        private async Task AcceptFileDownloadClientsAsync()
        {
            while (_isRunning)
            {
                try
                {
                    var fileClient = await _fileSendListener.AcceptTcpClientAsync();
                    if (!string.IsNullOrEmpty(_fileToSend))
                    {
                        string file = _fileToSend;
                        _fileToSend = ""; // Clear it for next transfer
                        
                        _ = Task.Run(async () => {
                            try {
                                using (var ns = fileClient.GetStream())
                                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read))
                                {
                                    await fs.CopyToAsync(ns);
                                }
                                Dispatcher.Invoke(() => AddLog("File", $"Sent: {Path.GetFileName(file)}"));
                            }
                            catch (Exception ex) {
                                Dispatcher.Invoke(() => AddLog("Error", $"File sending failed: {ex.Message}"));
                            }
                            finally {
                                fileClient.Close();
                            }
                        });
                    }
                    else
                    {
                        fileClient.Close();
                    }
                }
                catch { }
            }
        }

        private async void StartClipboardMonitor()
        {
            while (_isRunning)
            {
                await Task.Delay(1000);
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (System.Windows.Clipboard.ContainsText())
                        {
                            string currentClip = System.Windows.Clipboard.GetText();
                            if (currentClip != _lastClipboardText && !string.IsNullOrEmpty(currentClip))
                            {
                                _lastClipboardText = currentClip;
                                byte[] clipData = Encoding.UTF8.GetBytes($"CLIP:{currentClip}");
                                lock (_connectedClients)
                                {
                                    foreach (var c in _connectedClients)
                                    {
                                        try { c.GetStream().Write(clipData, 0, clipData.Length); } catch { }
                                    }
                                }
                                AddLog("Clipboard", "Copied to PC clipboard.");
                            }
                        }
                    }
                    catch { }
                });
            }
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
                    NetworkInterface activeAdapter = null;
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
                    DriveInfo drive = new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory));
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
            if (category != "File" && category != "Clipboard") return;
            
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

        private void BtnOpenGallery_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog();
            ofd.Title = "Select File to Send";
            if (ofd.ShowDialog() == true)
            {
                string filePath = ofd.FileName;
                var fi = new FileInfo(filePath);
                _fileToSend = filePath;
                
                byte[] packet = Encoding.UTF8.GetBytes($"FILE_SEND:{fi.Name}:{fi.Length}");
                lock (_connectedClients)
                {
                    foreach (var c in _connectedClients)
                    {
                        try { c.GetStream().Write(packet, 0, packet.Length); } catch { }
                    }
                }
            }
        }
    }
}