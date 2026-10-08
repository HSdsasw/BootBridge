using System.Security.Principal;

namespace IsoWriter;

public sealed class MainForm : Form
{
    private readonly ComboBox targets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DropDownWidth = 1000 };
    private readonly Button refresh = new() { Text = "Обновить", Dock = DockStyle.Fill };
    private readonly TextBox imagePath = new() { ReadOnly = true, Dock = DockStyle.Fill, PlaceholderText = "Выберите локальный ISO образ Linux" };
    private readonly Button browse = new() { Text = "Выбрать ISO…", Dock = DockStyle.Fill };
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(243, 246, 250), BorderStyle = BorderStyle.None };
    private readonly CheckBox consent = new() { Text = "Я понимаю: все разделы и данные выбранного физического диска будут потеряны", AutoSize = true };
    private readonly TextBox confirmation = new() { Dock = DockStyle.Fill, PlaceholderText = "Введите ДИСК N, где N — номер физического диска" };
    private readonly Button write = new() { Text = "Записать ISO на диск", Dock = DockStyle.Fill, BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button cancel = new() { Text = "Остановить", Dock = DockStyle.Fill, Enabled = false };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Maximum = 100 };
    private readonly Label status = new() { Text = "Загрузка списка дисков…", AutoSize = true, Dock = DockStyle.Fill };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    private readonly bool preview;
    private CancellationTokenSource? operation;
    private bool busy;
    private bool validImage;
    private bool destructive;
    private long imageSize;
    internal bool Busy => busy;

    public MainForm(bool preview = false)
    {
        this.preview = preview;
        Text = "ISO Writer — загрузочный Linux диск";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(980, 790);
        MinimumSize = new Size(880, 780);
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26), ColumnCount = 1, RowCount = 14 };
        foreach (int height in new[] { 48, 47, 28, 40, 144, 28, 40, 46, 38, 40, 48, 26, 32 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "ISO Writer", Font = new Font("Segoe UI", 24, FontStyle.Bold), AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = "Загрузочный носитель из hybrid ISO Linux • SSD / HDD / USB\nРазметка и загрузчик переносятся из образа на весь физический диск.", AutoSize = true }, 0, 1);
        layout.Controls.Add(Heading("1. Выберите раздел или диск"), 0, 2);
        layout.Controls.Add(Pair(targets, refresh, 130), 0, 3);
        layout.Controls.Add(details, 0, 4);
        layout.Controls.Add(Heading("2. Выберите ISO образ"), 0, 5);
        layout.Controls.Add(Pair(imagePath, browse, 150), 0, 6);
        layout.Controls.Add(new Label { Text = "ВНИМАНИЕ: выбор раздела означает выбор всего диска, которому он принадлежит.\nДругие разделы на этом диске также будут потеряны. Сделайте резервную копию.", ForeColor = Color.FromArgb(168, 48, 32), AutoSize = true }, 0, 7);
        layout.Controls.Add(consent, 0, 8);
        layout.Controls.Add(confirmation, 0, 9);
        layout.Controls.Add(Pair(write, cancel, 150), 0, 10);
        layout.Controls.Add(progress, 0, 11);
        layout.Controls.Add(status, 0, 12);
        layout.Controls.Add(log, 0, 13);

        refresh.Click += async (_, _) => await RefreshAsync();
        browse.Click += async (_, _) => await BrowseAsync();
        targets.SelectedIndexChanged += (_, _) => { consent.Checked = false; confirmation.Clear(); UpdateDetails(); UpdateActions(); };
        consent.CheckedChanged += (_, _) => UpdateActions();
        confirmation.TextChanged += (_, _) => UpdateActions();
        write.Click += async (_, _) => await WriteAsync();
        cancel.Click += (_, _) => CancelOperation();
        Shown += async (_, _) =>
        {
            if (preview)
            {
                var disk = new Disk { Number = 2, Model = "Пример: внешний SSD", Serial = "DEMO-ONLY", UniqueId = "DEMO", Size = 256L << 30, SectorSize = 512, Bus = "USB", Partitions = [new Partition { Number = 1, Letter = "E", Size = 256L << 30, Type = "Basic" }] };
                targets.Items.Add(new Target(disk, disk.Partitions[0])); targets.SelectedIndex = 0;
                status.Text = "Предпросмотр интерфейса — запись отключена";
                UpdateActions();
                using var bitmap = new Bitmap(Width, Height);
                DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, "preview.png"));
                Close();
            }
            else await RefreshAsync();
        };
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            if (operation is not null) CancelOperation();
        };
        UpdateActions();
    }

    private static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    private static TableLayoutPanel Pair(Control left, Control right, int width)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        panel.Controls.Add(left, 0, 0); panel.Controls.Add(right, 1, 0);
        return panel;
    }
    private void Log(string text) => log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    private Disk? SelectedDisk => (targets.SelectedItem as Target)?.Disk;
    private void UpdateDetails()
    {
        if (SelectedDisk is not { } disk) { details.Text = "Выберите устройство. Системные диски доступны только для просмотра."; return; }
        details.Text = $"ФИЗИЧЕСКИЙ ДИСК {disk.Number}  •  {disk.Model}  •  {Units.Size(disk.Size)}  •  {disk.Bus}\r\n"
            + $"Серийный номер: {disk.Serial}   |   Сектор: {disk.SectorSize} байт\r\n"
            + (disk.BlockReason is { } reason ? $"ЗАПИСЬ ЗАПРЕЩЕНА: {reason}\r\n" : $"Для подтверждения введите: ДИСК {disk.Number}\r\n")
            + "Будут заменены все разделы:\r\n" + (disk.Partitions.Length == 0 ? "Разделов нет" : string.Join("\r\n", disk.Partitions.Select(p => p.Caption)));
    }
    private void UpdateActions()
    {
        targets.Enabled = refresh.Enabled = browse.Enabled = !busy && !preview;
        consent.Enabled = confirmation.Enabled = !busy && !preview;
        write.Enabled = !busy && !preview && validImage && SelectedDisk is { BlockReason: null } d && imageSize <= d.Size
            && consent.Checked && confirmation.Text.Trim() == $"ДИСК {d.Number}";
        cancel.Enabled = operation is not null && !operation.IsCancellationRequested;
    }
    private async Task RefreshAsync()
    {
        busy = true; UpdateActions(); status.Text = "Поиск физических дисков и разделов…";
        try
        {
            targets.Items.Clear();
            foreach (var disk in (await DiskCatalog.LoadAsync()).OrderBy(d => d.Number))
            {
                targets.Items.Add(new Target(disk, null));
                foreach (var partition in disk.Partitions) targets.Items.Add(new Target(disk, partition));
            }
            targets.SelectedIndex = -1;
            UpdateDetails();
            status.Text = targets.Items.Count == 0 ? "Диски не найдены" : "Выберите диск и ISO образ";
        }
        catch (Exception ex) { status.Text = "Ошибка обнаружения дисков"; Log(ex.Message); }
        finally { busy = false; UpdateActions(); }
    }
    private async Task BrowseAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "ISO образы (*.iso)|*.iso", CheckFileExists = true, Title = "Выберите hybrid ISO Linux" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        imagePath.Text = dialog.FileName; validImage = false; consent.Checked = false;
        busy = true; UpdateActions();
        try
        {
            imageSize = await Task.Run(() => { using var stream = File.OpenRead(dialog.FileName); IsoImage.Validate(stream); return stream.Length; });
            validImage = true; status.Text = $"ISO: {Units.Size(imageSize)} • найдены признаки гибридного загрузочного образа";
            Log($"Образ: {dialog.FileName} ({Units.Size(imageSize)}). Совместимость загрузки зависит от ISO и BIOS/UEFI компьютера.");
        }
        catch (Exception ex) { status.Text = "Этот образ не поддерживается"; Log(ex.Message); }
        finally { busy = false; UpdateActions(); }
    }
    private void CancelOperation()
    {
        if (operation is null || operation.IsCancellationRequested) return;
        if (destructive && MessageBox.Show(this, "Диск уже изменён. Остановка может оставить неполный образ. Остановить?", "Остановка", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        operation.Cancel(); status.Text = "Остановка после текущей операции ввода-вывода…"; UpdateActions();
    }
    private async Task WriteAsync()
    {
        if (!write.Enabled || SelectedDisk is not { } disk) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show(this, "Запустите IsoWriter.exe от имени администратора.", "Нужны права администратора"); return;
        }
        string summary = $"Будет перезаписан ВЕСЬ физический диск {disk.Number}:\n{disk.Model}\nСерийный номер: {disk.Serial}\nРазмер: {Units.Size(disk.Size)}\n\n"
            + string.Join("\n", disk.Partitions.Select(p => p.Caption))
            + $"\n\nОбраз: {imagePath.Text}\n\nВсе существующие разделы и данные будут потеряны. Начать запись?";
        if (MessageBox.Show(this, summary, "Подтверждение удаления данных", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        busy = true; destructive = false; operation = new CancellationTokenSource(); progress.Value = 0; UpdateActions();
        try
        {
            Log($"Начало: диск {disk.Number}, {disk.Model}, серийный номер {disk.Serial}.");
            var reporter = new Progress<WriteProgress>(p => { destructive |= p.Destructive; progress.Value = p.Percent; status.Text = $"{p.Stage} • {p.Detail}"; });
            var result = await ImageWriter.WriteAsync(disk, imagePath.Text, reporter, operation.Token);
            status.Text = "Готово. Запись и проверка завершены."; progress.Value = 100;
            Log("SHA-256: " + result.Sha256);
            if (result.Warning is { } warning) Log(warning);
            Log("Носитель готов. Выберите его в Boot Menu компьютера. Если Windows предлагает форматирование, откажитесь.");
        }
        catch (OperationCanceledException) { status.Text = "Операция отменена до записи"; Log(status.Text); }
        catch (Exception ex) { status.Text = "Операция не завершена"; Log(ex.Message); MessageBox.Show(this, ex.Message, "ISO Writer", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            operation.Dispose(); operation = null; busy = false; consent.Checked = false; confirmation.Clear();
            targets.Items.Clear(); UpdateDetails(); UpdateActions();
            Log("Перед следующей записью нажмите «Обновить» и выберите диск заново.");
        }
    }
}
