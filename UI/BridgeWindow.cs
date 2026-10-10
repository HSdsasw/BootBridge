using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using IsoWriter.InternalBoot;
using IsoWriter.Platform;

namespace IsoWriter.UI;

internal sealed class BridgeWindow : Window
{
    private readonly ComboBox volumes = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox disks = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox iso = new() { IsReadOnly = true, PlaceholderText = "Select an Ubuntu / Mint / Debian Live ISO" };
    private readonly TextBox rawIso = new() { IsReadOnly = true, PlaceholderText = "Select a hybrid Linux ISO" };
    private readonly TextBox planText = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 190 };
    private readonly TextBox diskText = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 170 };
    private readonly CheckBox consent = new() { Content = "I have reviewed the plan and will preserve the ISO partition during installation" };
    private readonly CheckBox rawConsent = new() { Content = "All partitions and data on the physical disk will be lost" };
    private readonly TextBox rawConfirm = new() { PlaceholderText = "Enter DISK N from the list" };
    private readonly Button inspect = Button("Validate and show plan");
    private readonly Button install = Button("Prepare ISO boot");
    private readonly Button rawWrite = Button("Overwrite disk");
    private readonly Button next = Button("Boot Linux next");
    private readonly Button remove = Button("Remove boot entry");
    private readonly Button cancel = Button("Cancel");
    private readonly TextBlock status = Label("Ready", 13);
    private readonly TextBox log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 100 };
    private readonly NumericUpDown stagingSize = new() { Minimum = 4, Maximum = 128, Value = 12, Width = 130 };
    private readonly NumericUpDown linuxSize = new() { Minimum = 16, Maximum = 4096, Value = 40, Width = 130 };
    private readonly TabControl tabs = new();
    private readonly bool preview;
    private bool busy;
    private CancellationTokenSource? cancellation;
    private BootPlan? plan;
    private string? receipt;

    internal BridgeWindow(bool preview = false)
    {
        this.preview = preview; Title = "BootBridge — Linux installation"; Width = 1080; Height = 930; MinWidth = 860; MinHeight = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var heading = new StackPanel { Spacing = 4, Margin = new Thickness(24, 18, 24, 12) };
        heading.Children.Add(Label("BootBridge", 29, FontWeight.Bold));
        heading.Children.Add(Label($"Install Linux without a USB drive   •   {(OperatingSystem.IsLinux() ? "Linux" : "Windows")}   •   version 0.2", 14));
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") }; root.Children.Add(heading);
        tabs.ItemsSource = new[] { new TabItem { Header = "Install from internal disk", Content = InternalTab() }, new TabItem { Header = "Overwrite a separate disk", Content = RawTab() } };
        Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(24, 8, 24, 20) };
        footer.Children.Add(Row(status, cancel)); footer.Children.Add(log); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        inspect.Click += async (_, _) => await Run(async token =>
        {
            plan = null; consent.IsChecked = false;
            plan = await Work<BootPlan>("boot-plan", new BootRequest(iso.Text!, (BootPartition)volumes.SelectedItem!), token);
            planText.Text = plan.Summary; status.Text = "The plan is ready. Review it before preparing boot files.";
        });
        install.Click += async (_, _) =>
        {
            if (plan is not { } chosen || !await Confirm("Prepare boot files?", chosen.Summary)) return;
            await Run(async token =>
            {
                receipt = await Work<string>("boot-install", chosen, token);
                Log("Done. Installation record: " + receipt); Log("Schedule the next boot, then restart normally. In GRUB, select Start Linux live installer.");
                InvalidatePlan(); status.Text = "Boot files prepared";
            });
        };
        next.Click += async (_, _) =>
        {
            if (!await Confirm("Next boot", "Verify the files and schedule Linux for the next boot? The computer will not restart automatically.")) return;
            await Run(async _ => { await Work<bool>("manage", new ManageRequest(receipt!, true), default); Log("Linux is scheduled for the next boot. You can now restart the computer."); }, false);
        };
        remove.Click += async (_, _) =>
        {
            if (!await Confirm("Remove boot entry?", "Only this installation's boot entry and unchanged EFI files will be removed. The ISO and installation record will be kept.")) return;
            await Run(async _ => { await Work<bool>("manage", new ManageRequest(receipt!, false), default); Log("Boot entry removed; ISO preserved."); receipt = null; }, false);
        };
        rawWrite.Click += async (_, _) =>
        {
            if (disks.SelectedItem is not Target target || !await Confirm("Erase all data", diskText.Text + "\n\nImage: " + rawIso.Text + "\nOverwrite the ENTIRE physical disk?")) return;
            await Run(async token =>
            {
                var result = await Work<WriteResult>("raw-write", new RawRequest(target.Disk, rawIso.Text!), token);
                Log("Write verified. SHA-256: " + result.Sha256); if (result.Warning is { } warning) Log(warning);
                rawConsent.IsChecked = false; rawConfirm.Text = ""; disks.ItemsSource = null; status.Text = "Writing completed";
            });
        };
        cancel.Click += async (_, _) =>
        {
            if (cancellation is null) return;
            if (await Confirm("Stop the operation?", "File preparation will attempt a rollback. If a disk is being overwritten, the original data cannot be restored; you will need to write the image again."))
            { cancellation.Cancel(); cancel.IsEnabled = false; status.Text = "Waiting for a safe stop…"; }
        };
        volumes.SelectionChanged += (_, _) => InvalidatePlan();
        disks.SelectionChanged += (_, _) => { rawConsent.IsChecked = false; rawConfirm.Text = ""; ShowDisk(); Update(); };
        consent.IsCheckedChanged += (_, _) => Update(); rawConsent.IsCheckedChanged += (_, _) => Update(); rawConfirm.TextChanged += (_, _) => Update();
        Closing += (_, e) => { if (busy) { e.Cancel = true; status.Text = "Wait for the operation to finish or click Cancel."; } };
        Opened += async (_, _) =>
        {
            if (!preview) { await Refresh(); return; }
            planText.Text = "Preparation plan\n\nThe ISO will be stored as a regular file on the selected partition.\nGRUB will be placed in a separate EFI directory.\nYour current OS remains the default.\n\nPreserve the ISO partition during installation. Select another partition or unallocated space for Linux.\n\nPreview: disks and boot settings are not modified.";
            iso.Text = OperatingSystem.IsLinux() ? "/home/user/Downloads/ubuntu.iso" : @"C:\Downloads\ubuntu.iso";
            status.Text = "Avalonia preview • writing disabled";
            await Task.Delay(300);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
            bitmap.Render(this); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "preview-avalonia.png"), PngBitmapEncoderOptions.Default); Close();
        };
        Update();
    }
    private Control InternalTab()
    {
        var panel = Panel();
        panel.Children.Add(Label("Boot the installer from the disk that contains your current OS.", 16, FontWeight.SemiBold));
        panel.Children.Add(Label(OperatingSystem.IsLinux() ? "UEFI x64 • Secure Boot off • mounted, unencrypted ext4/NTFS • system ESP mounted" : "UEFI x64 / GPT • NTFS without BitLocker • Secure Boot off", 13));
        var refresh = Button("Refresh"); refresh.Click += async (_, _) => await Refresh();
        panel.Children.Add(Label("1. ISO storage partition", 14, FontWeight.SemiBold)); panel.Children.Add(Row(volumes, refresh));
        var create = Button("Create partition…"); create.Click += async (_, _) => await CreatePartition();
        var partitionRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { Label("ISO size, GiB:"), stagingSize, Label("  Space for Linux, GiB:"), linuxSize, create }) { control.Margin = new Thickness(0, 0, 10, 0); partitionRow.Children.Add(control); }
        panel.Children.Add(partitionRow);
        if (OperatingSystem.IsLinux()) panel.Children.Add(Label("The wizard only shrinks unmounted ext4 on a fully unmounted disk. Use a live environment for the system disk.", 12));
        var browse = Button("Browse ISO…"); browse.Click += async (_, _) => { if (await Pick("ISO images", "*.iso") is { } path) { iso.Text = path; InvalidatePlan(); } };
        panel.Children.Add(Row(iso, browse)); panel.Children.Add(inspect); panel.Children.Add(planText); panel.Children.Add(consent); panel.Children.Add(install);
        var open = Button("Open installation…"); open.Click += async (_, _) =>
        {
            string? file = await Pick("Installation record", "installation.json"); if (file is null) return;
            await Run(async token => { var loaded = await Work<BootReceipt>("receipt", file, token); receipt = file; Log($"Installation {loaded.Id[..8]}: {loaded.State}"); });
        };
        var actions = new WrapPanel(); foreach (var button in new[] { open, next, remove }) { button.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(button); } panel.Children.Add(actions);
        return new ScrollViewer { Content = panel };
    }
    private Control RawTab()
    {
        var panel = Panel(); panel.Children.Add(Label("All partitions on the selected physical disk will be erased", 17, FontWeight.Bold));
        panel.Children.Add(Label("This mode is for a separate drive. Use the first tab to install from the system disk."));
        var refresh = Button("Refresh disks"); refresh.Click += async (_, _) => await Run(async token =>
        {
            var list = await DiskCatalog.LoadAsync(token); disks.ItemsSource = list.SelectMany(d => new[] { new Target(d, null) }.Concat(d.Partitions.Select(p => new Target(d, p)))).ToArray();
        });
        panel.Children.Add(Row(disks, refresh)); panel.Children.Add(diskText);
        var browse = Button("Browse ISO…"); browse.Click += async (_, _) =>
        {
            if (await Pick("Hybrid ISO", "*.iso") is not { } path) return;
            await Run(async token => { await Task.Run(() => { using var file = File.OpenRead(path); IsoImage.Validate(file); }, token); rawIso.Text = path; rawConsent.IsChecked = false; });
        };
        panel.Children.Add(Row(rawIso, browse)); panel.Children.Add(rawConsent); panel.Children.Add(rawConfirm); panel.Children.Add(rawWrite);
        return new ScrollViewer { Content = panel };
    }
    private async Task Refresh() => await Run(async token =>
    {
        var env = await BootStorage.ReadAsync(token); volumes.ItemsSource = env.Partitions.Where(p => !p.IsEsp && (p.LinuxDevice.Length > 0 || p.Letter.Length == 1)).ToArray();
        if (env.DetectionWarning.Length > 0) Log(env.DetectionWarning);
        Log(env.SecureBoot == 0 ? "Secure Boot is off." : "Secure Boot status will be checked with elevated privileges before preparation.");
        status.Text = "Select a partition and ISO";
    });
    private async Task CreatePartition()
    {
        if (volumes.SelectedItem is not BootPartition source) { Log("Select the source partition first."); return; }
        ShrinkPlan? proposal = null;
        await Run(async token => { proposal = await Work<ShrinkPlan>("shrink-plan", new ShrinkRequest(source, (int)stagingSize.Value!, (int)linuxSize.Value!), token); });
        if (proposal is null || !await Confirm("Partition changes", proposal.Summary)) return;
        await Run(async _ => { string result = await Work<string>("shrink-apply", proposal, default); Log("Partition created: " + result); InvalidatePlan(); }, false);
        await Refresh();
    }
    private Task<T> Work<T>(string operation, object payload, CancellationToken token) =>
        Task.Run(() => PrivilegedWork.RunAsync<T>(operation, payload, new ActionProgress<string>(text => Avalonia.Threading.Dispatcher.UIThread.Post(() => status.Text = text)), token));
    private async Task Run(Func<CancellationToken, Task> action, bool cancellable = true)
    {
        if (busy || preview) return; busy = true; cancellation = cancellable ? new CancellationTokenSource() : null; Update();
        try { await action(cancellation?.Token ?? default); }
        catch (OperationCanceledException) { Log("Operation canceled."); InvalidatePlan(); }
        catch (Exception ex) { Log(ex.Message); status.Text = "Operation did not complete — see the log"; InvalidatePlan(); await Notice(ex.Message); }
        finally { cancellation?.Dispose(); cancellation = null; busy = false; Update(); }
    }
    private void ShowDisk()
    {
        if (disks.SelectedItem is not Target t) { diskText.Text = ""; return; }
        diskText.Text = $"DISK {t.Disk.Number} • {t.Disk.DevicePath} • {t.Disk.Model}\nSerial number: {t.Disk.Serial}\nCapacity: {Units.Size(t.Disk.Size)}\n"
            + (t.Disk.BlockReason is { } reason ? "BLOCKED: " + reason : "To confirm, enter: DISK " + t.Disk.Number)
            + "\nALL partitions will be replaced:\n" + string.Join('\n', t.Disk.Partitions.Select(p => p.Caption));
    }
    private void InvalidatePlan() { plan = null; consent.IsChecked = false; planText.Text = ""; Update(); }
    private void Update()
    {
        tabs.IsEnabled = !busy && !preview; cancel.IsEnabled = cancellation is not null && !cancellation.IsCancellationRequested;
        inspect.IsEnabled = volumes.SelectedItem is not null && !string.IsNullOrEmpty(iso.Text);
        install.IsEnabled = plan is not null && consent.IsChecked == true;
        next.IsEnabled = remove.IsEnabled = receipt is not null;
        rawWrite.IsEnabled = disks.SelectedItem is Target { Disk.BlockReason: null } t && !string.IsNullOrEmpty(rawIso.Text) && rawConsent.IsChecked == true && rawConfirm.Text?.Trim() == $"DISK {t.Disk.Number}";
    }
    private void Log(string text) => log.Text += $"[{DateTime.Now:HH:mm:ss}] {text}\n";
    private async Task<string?> Pick(string title, string pattern)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(title) { Patterns = [pattern] }] });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    private async Task<bool> Confirm(string title, string message)
    {
        var dialog = new Window { Title = title, Width = 710, Height = 510, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = true };
        var yes = Button("Confirm"); var no = Button("Cancel"); yes.Click += (_, _) => dialog.Close(true); no.Click += (_, _) => dialog.Close(false);
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(22) };
        grid.Children.Add(new ScrollViewer { Content = Label(message, 14) }); var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(no); buttons.Children.Add(yes); Grid.SetRow(buttons, 1); grid.Children.Add(buttons); dialog.Content = grid;
        dialog.Opened += (_, _) => no.Focus(); return await dialog.ShowDialog<bool>(this);
    }
    private async Task Notice(string message)
    {
        var dialog = new Window { Title = "BootBridge", Width = 680, Height = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var close = Button("Close"); close.Click += (_, _) => dialog.Close(); var panel = new DockPanel { Margin = new Thickness(20) }; DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close); panel.Children.Add(new ScrollViewer { Content = Label(message) }); dialog.Content = panel; await dialog.ShowDialog(this);
    }
    private static Button Button(string text) => new() { Content = text, Padding = new Thickness(14, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
    private static TextBlock Label(string text, double size = 13, FontWeight? weight = null) => new() { Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static StackPanel Panel() => new() { Spacing = 10, Margin = new Thickness(24, 18) };
    private static Grid Row(Control left, Control right) { var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 }; grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right); return grid; }
}
