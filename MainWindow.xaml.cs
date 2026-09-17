using System.Management;
using System.Windows;
using System.IO;
using System.Text.Json;
using System.IO.Ports;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using DigiRigControlCenter.Services;

namespace DigiRigControlCenter;

public partial class MainWindow : Window
{
    private readonly AudioService audio = new();
    private readonly Cm108PttService ptt = new();
    private readonly CatService cat = new();
    private MMDevice? rx;
    private MMDevice? tx;
    private CatProfilesFile? catProfiles;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { LoadCatProfiles(); InitializeCatControls(); Refresh(); };
        Closing += (_, _) => { audio.Dispose(); };
        audio.InputLevelChanged += Audio_InputLevelChanged;
        audio.OutputStereoLevelChanged += Audio_OutputStereoLevelChanged;
        audio.MonitorError += Audio_MonitorError;
    }


    private void LoadCatProfiles()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "cat-profiles.json");
            if (!File.Exists(path))
            {
                ProfileStatusText.Text = "cat-profiles.json not found: " + path;
                return;
            }

            catProfiles = JsonSerializer.Deserialize<CatProfilesFile>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            BrandComboBox.Items.Clear();
            RadioComboBox.Items.Clear();
            foreach (var brand in catProfiles?.Brands ?? [])
                BrandComboBox.Items.Add(brand.Brand);

            if (BrandComboBox.Items.Count > 0) BrandComboBox.SelectedIndex = 0;
            ProfileStatusText.Text = $"Loaded {BrandComboBox.Items.Count} brand(s) from cat-profiles.json";
        }
        catch (Exception ex)
        {
            ProfileStatusText.Text = "Profile load error: " + ex.Message;
        }
    }

    private void BrandComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RadioComboBox == null || catProfiles == null) return;
        RadioComboBox.Items.Clear();
        string? selectedBrand = BrandComboBox.SelectedItem as string;
        if (selectedBrand == null) return;
        var brand = catProfiles.Brands.FirstOrDefault(b => b.Brand == selectedBrand);
        if (brand == null) return;
        foreach (var model in brand.Models) RadioComboBox.Items.Add(model.Model);
        if (RadioComboBox.Items.Count > 0) RadioComboBox.SelectedIndex = 0;
    }

    private void RadioComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplySelectedCatProfile();
    }

    private CatModelProfile? GetSelectedCatProfile()
    {
        if (catProfiles == null) return null;
        string? brandName = BrandComboBox?.SelectedItem as string;
        string? modelName = RadioComboBox?.SelectedItem as string;
        return catProfiles.Brands.FirstOrDefault(b => b.Brand == brandName)?.Models.FirstOrDefault(m => m.Model == modelName);
    }

    private void ApplySelectedCatProfile()
    {
        if (CatSpeedComboBox == null || DataBitsComboBox == null || StopBitsComboBox == null || CatCommandTextBox == null) return;
        var profile = GetSelectedCatProfile();
        if (profile == null) return;
        CatSpeedComboBox.SelectedItem = profile.Baud.ToString();
        DataBitsComboBox.SelectedItem = profile.DataBits.ToString();
        StopBitsComboBox.SelectedItem = profile.StopBits.ToString();
        CatCommandTextBox.Text = profile.CATprotocol.Equals("civ", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(profile.CivAddress)
            ? $"FE FE {profile.CivAddress.ToUpperInvariant()} E0 03 FD"
            : profile.TestCommand;
    }

    private void DigiRigType_Changed(object sender, RoutedEventArgs e)
    {
        if (CatPanel != null)
            CatPanel.Visibility = MobileRadioButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (PttStatus != null)
            PttStatus.Text = MobileRadioButton.IsChecked == true ? "RTS PTT: OFF" : "GPIO3 PTT: OFF";
        if (IsLoaded) Refresh();
    }

    private void InitializeCatControls()
    {
        CatSpeedComboBox.ItemsSource = new[] { "4800", "9600", "19200", "38400", "57600", "115200" };
        CatSpeedComboBox.SelectedItem = "9600";
        DataBitsComboBox.ItemsSource = new[] { "7", "8" };
        DataBitsComboBox.SelectedItem = "8";
        StopBitsComboBox.ItemsSource = new[] { "1", "2" };
        StopBitsComboBox.SelectedItem = "1";
        RefreshCatPorts();
        ApplySelectedCatProfile();
        DigiRigType_Changed(this, new RoutedEventArgs());
    }

    private void RefreshCatPorts()
    {
        if (ComPortComboBox == null) return;
        string? selected = ComPortComboBox.SelectedItem as string;
        var ports = cat.GetAvailablePorts();
        ComPortComboBox.ItemsSource = ports;
        if (selected != null && ports.Contains(selected))
            ComPortComboBox.SelectedItem = selected;
        else if (ports.Length > 0)
            ComPortComboBox.SelectedIndex = 0;
    }

    private async void TestCat_Click(object sender, RoutedEventArgs e)
    {
        string? portName = ComPortComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(portName))
        {
            CatStatusText.Text = "Status: Select a COM port";
            CatStatusText.Foreground = (Brush)FindResource("BadBrush");
            return;
        }

        int baud = int.TryParse(CatSpeedComboBox.SelectedItem as string, out var b) ? b : 9600;
        int dataBits = int.TryParse(DataBitsComboBox.SelectedItem as string, out var d) ? d : 8;
        StopBits stopBits = (StopBitsComboBox.SelectedItem as string) == "2" ? StopBits.Two : StopBits.One;
        string command = CatCommandTextBox.Text;
        var profile = GetSelectedCatProfile();
        string protocol = profile?.CATprotocol ?? "none";

        TestCatButton.IsEnabled = false;
        CatStatusText.Text = $"Status: Testing {portName}…";
        CatStatusText.Foreground = (Brush)FindResource("MutedBrush");
        try
        {
            byte[] response = await Task.Run(() => cat.TestCommand(portName, baud, dataBits, stopBits, protocol, command));
            if (response.Length == 0)
            {
                CatStatusText.Text = "Status: No CAT response received";
                CatStatusText.Foreground = (Brush)FindResource("WarnBrush");
            }
            else
            {
                string printable = (protocol.Equals("civ", StringComparison.OrdinalIgnoreCase) || protocol.Equals("icom-civ", StringComparison.OrdinalIgnoreCase))
                    ? BitConverter.ToString(response).Replace("-", " ")
                    : System.Text.Encoding.ASCII.GetString(response).Replace("\r", " ").Replace("\n", " ").Trim();
                CatStatusText.Text = $"Status: CAT response received — {printable}";
                CatStatusText.Foreground = (Brush)FindResource("GoodBrush");
            }
        }
        catch (Exception ex)
        {
            CatStatusText.Text = "Status: CAT error — " + ex.Message;
            CatStatusText.Foreground = (Brush)FindResource("BadBrush");
        }
        finally
        {
            TestCatButton.IsEnabled = true;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        try
        {
            RefreshCatPorts();
            var inputs = audio.GetInputs();
            var outputs = audio.GetOutputs();
            InputsList.ItemsSource = inputs.Select(x => $"{(x.IsDigiRigCandidate ? "● DIGIRIG  " : "    ")}{x.Name}   {x.SampleFormat}   {(x.IsDefault ? (x.IsDigiRigCandidate ? "[DEFAULT — NOT RECOMMENDED]" : "[DEFAULT]") : "")}");
            OutputsList.ItemsSource = outputs.Select(x => $"{(x.IsDigiRigCandidate ? "● DIGIRIG  " : "    ")}{x.Name}   {x.SampleFormat}   {(x.IsDefault ? (x.IsDigiRigCandidate ? "[DEFAULT — NOT RECOMMENDED]" : "[DEFAULT]") : "")}");

            rx = audio.FindDigiRigInput();
            tx = audio.FindDigiRigOutput();
            UpdateDefaultAudioWarnings(inputs, outputs);
            if (tx != null)
                audio.EnsureOutputMeter(tx);
            else
                audio.StopOutputMeter();
            bool found = rx != null && tx != null;
            bool mobile = MobileRadioButton.IsChecked == true;
            string deviceName = mobile ? "DigiRig Mobile" : "DigiRig Lite";
            DeviceStatus.Text = found ? $"● {deviceName} detected" : $"✗ {deviceName} not detected";
            DeviceStatus.Foreground = found ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("BadBrush");
            var catPorts = cat.GetAvailablePorts();
            RefreshComDeviceDetails();
            ComPortDeviceStatus.Text = mobile
                ? (catPorts.Length > 0 ? $"COM port available: {string.Join(", ", catPorts)}" : "No COM port detected")
                : "";
            ComPortDeviceStatus.Foreground = mobile && catPorts.Length == 0
                ? (Brush)FindResource("WarnBrush")
                : (Brush)FindResource("MutedBrush");
            ptt.Refresh();
            PttDeviceText.Text = mobile
                ? "PTT: RTS via selected DigiRig COM port"
                : "PTT HID: " + ptt.DeviceDescription;

            RxText.Text = rx == null ? "Not found" : rx.FriendlyName;
            TxText.Text = tx == null ? "Not found" : tx.FriendlyName;
            EnhText.Text = (rx != null && GetEnhancementState(rx)) && (tx != null && GetEnhancementState(tx)) ? "✓ Disabled" : "⚠ Check / fix";
            EnhText.Foreground = EnhText.Text.StartsWith("✓") ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("WarnBrush");
            AgcText.Text = "Driver control — use Custom tab";
            AgcText.Foreground = (Brush)FindResource("WarnBrush");
            if (rx != null) InputLevelSlider.Value = audio.GetInputVolume() * 100.0;
            if (tx != null) OutputLevelSlider.Value = audio.GetOutputVolume() * 100.0;
            AudioMessage.Text = found ? "DigiRig audio endpoints are ready. Set RX input and TX output levels below. The meter shows live RX audio." : $"Connect the {(MobileRadioButton.IsChecked == true ? "DigiRig Mobile" : "DigiRig Lite")} and press Refresh.";
        }
        catch (Exception ex)
        {
            AudioMessage.Text = "Refresh error: " + ex.Message;
        }
    }

    private void RefreshComDeviceDetails()
    {
        var devices = new List<string>();
        try
        {
            // Win32_PnPEntity includes USB-to-serial virtual COM ports that are not
            // always returned by Win32_SerialPort. The Name field contains "(COMx)".
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Manufacturer, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            foreach (ManagementObject item in searcher.Get())
            {
                string name = item["Name"]?.ToString() ?? "Unknown COM device";
                string manufacturer = item["Manufacturer"]?.ToString() ?? "Unknown manufacturer";
                string pnpId = item["PNPDeviceID"]?.ToString() ?? "";

                string usbId = "";
                var vid = System.Text.RegularExpressions.Regex.Match(pnpId, @"VID_[0-9A-Fa-f]{4}");
                var pid = System.Text.RegularExpressions.Regex.Match(pnpId, @"PID_[0-9A-Fa-f]{4}");
                if (vid.Success || pid.Success)
                    usbId = $"   USB: {vid.Value.ToUpperInvariant()} {pid.Value.ToUpperInvariant()}".TrimEnd();

                devices.Add($"{name}\n   Manufacturer: {manufacturer}{usbId}\n   PNP ID: {pnpId}");
            }
        }
        catch (Exception ex)
        {
            devices.Add("Unable to read COM device details: " + ex.Message);
        }

        ComDevicesList.ItemsSource = devices;
        ComDevicesEmptyText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDefaultAudioWarnings(IReadOnlyList<DigiRigControlCenter.Models.AudioEndpointInfo> inputs, IReadOnlyList<DigiRigControlCenter.Models.AudioEndpointInfo> outputs)
    {
        if (AudioDefaultWarningPanel == null || AudioDefaultWarningText == null) return;

        bool badPlayback = outputs.Any(x => x.IsDefault && x.IsDigiRigCandidate);
        bool badRecording = inputs.Any(x => x.IsDefault && x.IsDigiRigCandidate);
        var warnings = new List<string>();

        if (badPlayback)
            warnings.Add("DigiRig Out is set as the Windows Default playback device. Use Windows Audio Properties to assign your computer speakers/headphones as the Playback Default. Then click Refresh. Otherwise normal Windows alert sounds can be routed to the radio.");
        if (badRecording)
            warnings.Add("DigiRig In is the Windows default recording device. Use Windows Audio Properties to assign your normal microphone/input as the Default recording device; applications that need DigiRig In should select it explicitly.");

        AudioDefaultWarningText.Text = string.Join("\n", warnings);
        AudioDefaultWarningPanel.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsDigiRigDefaultPlayback()
    {
        return audio.GetOutputs().Any(x => x.IsDefault && x.IsDigiRigCandidate);
    }

    private bool GetEnhancementState(MMDevice d)
    {
        // Re-read by comparing the endpoint's property through the public service data.
        return audio.GetInputs().Concat(audio.GetOutputs()).FirstOrDefault(x => x.Id == d.ID)?.EnhancementsDisabled ?? false;
    }

    private void FixEnhancements_Click(object sender, RoutedEventArgs e)
    {
        int ok = 0;
        if (rx != null && audio.TryDisableEnhancements(rx, out var rmsg)) ok++;
        if (tx != null && audio.TryDisableEnhancements(tx, out var tmsg)) ok++;
        AudioMessage.Text = ok > 0
            ? "Enhancement/system-effects disable request applied. Refresh to verify. AGC is driver-specific and is handled from the native Custom tab."
            : "Windows did not expose the endpoint effects control. Use the native Windows audio properties to disable enhancements.";
        Refresh();
    }

    private void OpenProperties_Click(object sender, RoutedEventArgs e) => audio.OpenLegacySoundProperties();

    private void FixAll_Click(object sender, RoutedEventArgs e)
    {
        FixEnhancements_Click(sender, e);
        audio.OpenLegacySoundProperties();
    }

    private void RenameDigiRig_Click(object sender, RoutedEventArgs e)
    {
        if (audio.TryRenameDigiRigDevices(out var message))
        {
            AudioMessage.Text = message + " Refreshing device list…";
        }
        else
        {
            AudioMessage.Text = message;
        }
        Refresh();
    }

    private void InputLevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // During InitializeComponent(), WPF can raise ValueChanged before all
        // x:Name fields have been assigned. Do not touch the value label until
        // the XAML object tree is fully constructed.
        if (InputLevelValue != null)
            InputLevelValue.Text = $"{e.NewValue:0}%";

        if (IsLoaded && rx != null && Math.Abs(e.NewValue - audio.GetInputVolume() * 100) > 0.5)
            audio.TrySetInputVolume((float)(e.NewValue / 100.0), out _);
    }

    private void OutputLevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // During InitializeComponent(), WPF can raise ValueChanged before all
        // x:Name fields have been assigned. Do not touch the value label until
        // the XAML object tree is fully constructed.
        if (OutputLevelValue != null)
            OutputLevelValue.Text = $"{e.NewValue:0}%";

        if (IsLoaded && tx != null && Math.Abs(e.NewValue - audio.GetOutputVolume() * 100) > 0.5)
            audio.TrySetOutputVolume((float)(e.NewValue / 100.0), out _);
    }

    private void Audio_InputLevelChanged(float level)
    {
        Dispatcher.BeginInvoke(() => InputMeter.Value = level);
    }

    private void Audio_OutputStereoLevelChanged(float left, float right)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (OutputLeftMeter != null) OutputLeftMeter.Value = left;
            if (OutputRightMeter != null) OutputRightMeter.Value = right;
            if (TxAudioStatus != null)
                TxAudioStatus.Text = left < 0.5 && right < 0.5
                    ? "TX audio: idle"
                    : $"TX audio: LEFT {left:0}%  |  RIGHT {right:0}%";

            if (VoxStatus != null)
            {
                VoxStatus.Text = right >= 5.0f
                    ? "● Right-channel VOX: audio present (may key PTT)"
                    : "Right-channel VOX: inactive";
                VoxStatus.Foreground = right >= 5.0f
                    ? (Brush)FindResource("BadBrush")
                    : (Brush)FindResource("MutedBrush");
            }
        });
    }

    private void Audio_MonitorError(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            MonitorStatus.Text = "Monitor error: " + message;
            MonitorStatus.Foreground = (Brush)FindResource("BadBrush");
        });
    }

    private void StartMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (rx == null) { MonitorStatus.Text = "DigiRig RX not found."; return; }
        if (IsDigiRigDefaultPlayback())
        {
            MonitorStatus.Text = "Input Monitor blocked: DigiRig Out is set as the Windows Default playback device. Use Windows Audio Properties to assign your computer speakers/headphones as the Playback Default.";
            MonitorStatus.Foreground = (Brush)FindResource("BadBrush");
            return;
        }
        try
        {
            using var deviceEnumerator = new MMDeviceEnumerator();
            var defaultOut = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            audio.StartMonitor(rx, defaultOut, tx);
            MonitorStatus.Text = tx != null
                ? $"● Monitoring {rx.FriendlyName} → {defaultOut.FriendlyName}  |  TX meter: {tx.FriendlyName}"
                : $"● Monitoring {rx.FriendlyName} → {defaultOut.FriendlyName}  |  DigiRig Out not found";
            MonitorStatus.Foreground = (Brush)FindResource("GoodBrush");
        }
        catch (Exception ex) { MonitorStatus.Text = "Monitor error: " + ex.Message; }
    }

    private void StopMonitor_Click(object sender, RoutedEventArgs e)
    {
        audio.StopMonitor();
        MonitorStatus.Text = "Stopped";
        MonitorStatus.Foreground = (Brush)FindResource("MutedBrush");
    }

    private void TestTone_Click(object sender, RoutedEventArgs e)
    {
        if (tx == null)
        {
            TestToneStatus.Text = "DigiRig Out not found.";
            TestToneStatus.Foreground = (Brush)FindResource("BadBrush");
            return;
        }

        try
        {
            if (audio.IsTestToneRunning)
            {
                audio.StopTestTone();
                TestToneButton.Content = "TEST TONE";
                TestToneStatus.Text = "Test tone off";
                TestToneStatus.Foreground = (Brush)FindResource("MutedBrush");
            }
            else
            {
                audio.StartTestTone(tx);
                TestToneButton.Content = "STOP TONE";
                TestToneStatus.Text = "● 1 kHz test tone active";
                TestToneStatus.Foreground = (Brush)FindResource("GoodBrush");
            }
        }
        catch (Exception ex)
        {
            TestToneStatus.Text = "Test tone error: " + ex.Message;
            TestToneStatus.Foreground = (Brush)FindResource("BadBrush");
        }
    }

    private async void TestPtt_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(PttSeconds.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            seconds = 3.0;
        seconds = Math.Clamp(seconds, 0.1, 10.0);
        TestPttButton.IsEnabled = false;

        if (MobileRadioButton.IsChecked == true)
        {
            string? portName = ComPortComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(portName))
            {
                PttStatus.Text = "PTT error: select the DigiRig Mobile COM port";
                PttStatus.Foreground = (Brush)FindResource("BadBrush");
                TestPttButton.IsEnabled = true;
                return;
            }

            PttStatus.Text = $"● PTT ON — RTS asserted on {portName}";
            PttStatus.Foreground = (Brush)FindResource("BadBrush");
            try
            {
                await Task.Run(() =>
                {
                    using var serial = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
                    {
                        Handshake = Handshake.None,
                        RtsEnable = false,
                        DtrEnable = false
                    };
                    serial.Open();
                    serial.RtsEnable = true;
                    Thread.Sleep(TimeSpan.FromSeconds(seconds));
                    serial.RtsEnable = false;
                });
                PttStatus.Text = $"PTT OFF — RTS released on {portName}";
                PttStatus.Foreground = (Brush)FindResource("GoodBrush");
            }
            catch (Exception ex)
            {
                PttStatus.Text = "PTT error: " + ex.Message;
                PttStatus.Foreground = (Brush)FindResource("BadBrush");
            }
        }
        else
        {
            if (!ptt.Refresh())
            {
                PttStatus.Text = "DigiRig Lite HID PTT not detected.";
                PttStatus.Foreground = (Brush)FindResource("BadBrush");
                TestPttButton.IsEnabled = true;
                return;
            }
            PttStatus.Text = "● PTT ON — GPIO3 asserted by C-Media HID";
            PttStatus.Foreground = (Brush)FindResource("BadBrush");
            string err = "";
            bool success = await Task.Run(() => ptt.TestPtt(TimeSpan.FromSeconds(seconds), out err));
            if (success)
            {
                PttStatus.Text = "PTT OFF — GPIO3 released";
                PttStatus.Foreground = (Brush)FindResource("GoodBrush");
            }
            else
            {
                PttStatus.Text = string.IsNullOrWhiteSpace(err) ? "PTT error — check connection" : "PTT error: " + err;
                PttStatus.Foreground = (Brush)FindResource("BadBrush");
            }
        }
        TestPttButton.IsEnabled = true;
    }
}
