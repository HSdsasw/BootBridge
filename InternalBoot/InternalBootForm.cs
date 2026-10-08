namespace IsoWriter.InternalBoot;

internal sealed class InternalBootForm : Form
{
    private readonly ComboBox volumes = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button refresh = new() { Text = "Обновить", Dock = DockStyle.Fill };
    private readonly Button partition = new() { Text = "Создать раздел…", Dock = DockStyle.Fill };
    private readonly TextBox iso = new() { ReadOnly = true, Dock = DockStyle.Fill, PlaceholderText = "Выберите ISO Ubuntu / Linux Mint / Debian Live" };
    private readonly Button browse = new() { Text = "Выбрать ISO…", Dock = DockStyle.Fill };
    private readonly Button inspect = new() { Text = "Проверить и показать план", Dock = DockStyle.Fill };
    private readonly TextBox planText = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.FromArgb(243, 246, 250), BorderStyle = BorderStyle.FixedSingle };
    private readonly CheckBox consent = new() { Text = "Сохраню раздел с ISO во время установки Linux; план подготовки проверен", AutoSize = true };
    private readonly Button install = new() { Text = "Подготовить загрузку ISO", Dock = DockStyle.Fill, BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button cancel = new() { Text = "Отменить", Dock = DockStyle.Fill };
    private readonly Label status = new() { Text = "Обновите список разделов", AutoSize = true, Dock = DockStyle.Fill };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly Button open = new() { Text = "Открыть установку…", Dock = DockStyle.Fill };
    private readonly Button next = new() { Text = "Следующая загрузка — Linux", Dock = DockStyle.Fill };
    private readonly Button remove = new() { Text = "Убрать пункт загрузки", Dock = DockStyle.Fill };
    private readonly bool preview;
    private BootPlan? plan;
    private string? receipt;
    private CancellationTokenSource? cancellation;
    internal bool Busy { get; private set; }

    internal InternalBootForm(bool preview = false)
    {
        this.preview = preview;
        Font = new Font("Segoe UI", 10); BackColor = Color.White;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 13 };
        foreach (int height in new[] { 45, 65, 28, 39, 39, 43 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        foreach (int height in new[] { 34, 46, 32, 42 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Установка Linux без флешки", Font = new Font("Segoe UI", 22, FontStyle.Bold), AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = "ISO хранится файлом на выбранном разделе — можно на том же диске, где Windows.\nUEFI x64 • GPT • NTFS без BitLocker • Secure Boot выключен\nUbuntu / Linux Mint / Debian Live. Для нового раздела есть отдельный мастер.", AutoSize = true }, 0, 1);
        layout.Controls.Add(new Label { Text = "1. Раздел для хранения ISO (его нельзя удалять в установщике)", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 2);
        layout.Controls.Add(Row([volumes, refresh, partition], [64, 14, 22]), 0, 3);
        layout.Controls.Add(Row([iso, browse], [80, 20]), 0, 4);
        layout.Controls.Add(inspect, 0, 5);
        layout.Controls.Add(planText, 0, 6);
        layout.Controls.Add(consent, 0, 7);
        layout.Controls.Add(Row([install, cancel], [80, 20]), 0, 8);
        layout.Controls.Add(status, 0, 9);
        layout.Controls.Add(Row([open, next, remove], [27, 40, 33]), 0, 10);
        layout.Controls.Add(log, 0, 11);
        refresh.Click += async (_, _) => await RefreshAsync();
        partition.Click += async (_, _) => await CreatePartitionAsync();
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "ISO образы|*.iso", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) { iso.Text = dialog.FileName; InvalidatePlan(); }
        };
        volumes.SelectedIndexChanged += (_, _) => InvalidatePlan();
        consent.CheckedChanged += (_, _) => UpdateActions();
        inspect.Click += async (_, _) => await Execute(async token =>
        {
            plan = null; consent.Checked = false;
            plan = await BootInstaller.PlanAsync(iso.Text, (BootPartition)volumes.SelectedItem!, token);
            planText.Text = plan.Summary; status.Text = "План готов. Проверьте разделы и подтвердите подготовку.";
        });
        install.Click += async (_, _) =>
        {
            if (plan is not { } selected) return;
            if (MessageBox.Show(this, selected.Summary + "\r\n\r\nПодготовить загрузку?", "Изменение загрузочной конфигурации", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await Execute(async token =>
            {
                receipt = await BootInstaller.InstallAsync(selected, new Progress<string>(message => status.Text = message), token);
                Log("Готово. Журнал установки: " + receipt);
                Log("Для запуска нажмите «Следующая загрузка — Linux» и выполните обычную перезагрузку Windows. В меню GRUB выберите Start Linux live installer.");
                plan = null; consent.Checked = false;
            });
        };
        cancel.Click += (_, _) => { cancellation?.Cancel(); cancel.Enabled = false; status.Text = "Отмена и откат незавершённой подготовки…"; };
        open.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Журнал ISO Writer (installation.json)|installation.json", CheckFileExists = true, Title = "Выберите installation.json в папке IsoWriter-…" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { var saved = BootInstaller.LoadReceipt(dialog.FileName); receipt = dialog.FileName; Log($"Открыта установка {saved.Id[..8]} • {saved.State}"); UpdateActions(); }
            catch (Exception ex) { Log(ex.Message); }
        };
        next.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "Назначить Linux на следующую загрузку? После проверки файлов перезагрузите Windows через «Перезагрузка». Автоматической перезагрузки не будет.", "Следующая загрузка", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await Execute(async _ => { status.Text = "Проверка файлов и назначение следующей загрузки…"; await Task.Run(() => BootInstaller.ManageAsync(receipt!, true)); Log("Назначена следующая загрузка Linux. Теперь перезагрузите Windows."); status.Text = "Следующая загрузка — установщик Linux"; }, false);
        };
        remove.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "Удалить созданный пункт UEFI и его неизменённые файлы GRUB? ISO и журнал останутся; папку с ними можно удалить вручную после завершения установки Linux.", "Удаление пункта загрузки", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await Execute(async _ => { await Task.Run(() => BootInstaller.ManageAsync(receipt!, false)); Log("Пункт загрузки убран. ISO сохранён."); status.Text = "Пункт загрузки удалён"; receipt = null; }, false);
        };
        if (!preview) Shown += async (_, _) => await RefreshAsync();
        else
        {
            var target = new BootPartition { Letter = "D", DiskNumber = 0, Number = 4, Model = "Пример: системный NVMe SSD", Free = 80L << 30 };
            volumes.Items.Add(target); volumes.SelectedIndex = 0; iso.Text = @"C:\Downloads\ubuntu-desktop-amd64.iso";
            planText.Text = "План подготовки\r\n\r\nISO будет скопирован на D: в отдельную папку.\r\nGRUB будет добавлен в отдельную папку EFI.\r\nWindows останется загрузкой по умолчанию.\r\n\r\nВо время установки Linux сохраните D: с ISO.\r\nДля системы выберите другой раздел или незанятое пространство.\r\n\r\nЭто предпросмотр: никаких изменений на дисках не выполняется.";
            status.Text = "Предпросмотр интерфейса";
        }
        UpdateActions();
    }
    private static TableLayoutPanel Row(Control[] controls, int[] widths)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = controls.Length, Margin = Padding.Empty };
        for (int i = 0; i < controls.Length; i++) { row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, widths[i])); row.Controls.Add(controls[i], i, 0); }
        return row;
    }
    private void InvalidatePlan() { plan = null; consent.Checked = false; planText.Clear(); UpdateActions(); }
    private void Log(string text) => log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");
    private void UpdateActions()
    {
        volumes.Enabled = refresh.Enabled = browse.Enabled = open.Enabled = !Busy && !preview;
        partition.Enabled = !Busy && !preview && volumes.SelectedItem is BootPartition;
        inspect.Enabled = !Busy && !preview && volumes.SelectedItem is not null && File.Exists(iso.Text);
        consent.Enabled = !Busy && plan is not null;
        install.Enabled = !Busy && !preview && plan is not null && consent.Checked;
        next.Enabled = remove.Enabled = !Busy && !preview && receipt is not null;
        cancel.Enabled = cancellation is not null && !cancellation.IsCancellationRequested;
    }
    private async Task Execute(Func<CancellationToken, Task> work, bool cancellable = true)
    {
        if (Busy) return;
        Busy = true; if (cancellable) cancellation = new CancellationTokenSource(); UpdateActions();
        try { await work(cancellation?.Token ?? default); }
        catch (OperationCanceledException) { status.Text = "Подготовка отменена, незавершённые изменения убраны"; Log(status.Text); plan = null; }
        catch (Exception ex) { status.Text = "Операция не завершена"; Log(ex.Message); plan = null; MessageBox.Show(this, ex.Message, "ISO Writer", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { cancellation?.Dispose(); cancellation = null; Busy = false; UpdateActions(); }
    }
    private async Task RefreshAsync() => await Execute(async token =>
    {
        status.Text = "Чтение разделов и состояния BitLocker…"; volumes.Items.Clear();
        var environment = await BootStorage.ReadAsync(token);
        foreach (var partition in environment.Partitions.Where(p => p.Letter.Length == 1 && !p.IsEsp)) volumes.Items.Add(partition);
        if (!string.IsNullOrEmpty(environment.DetectionWarning)) Log("Для проверки BitLocker запустите IsoWriter.exe от имени администратора. " + environment.DetectionWarning);
        Log(environment.SecureBoot == 0 ? "Secure Boot выключен." : "Secure Boot включён или недоступен. Подготовка требует выключенного Secure Boot.");
        status.Text = "Выберите раздел, ISO и нажмите «Проверить и показать план»";
    });
    private async Task CreatePartitionAsync()
    {
        if (volumes.SelectedItem is not BootPartition source) return;
        using var dialog = new Form { Text = $"Освободить место из {source.Letter}:", ClientSize = new Size(540, 235), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Font = Font };
        var staging = new NumericUpDown { Minimum = 4, Maximum = 128, Value = 12, Left = 360, Top = 55, Width = 120 };
        var linux = new NumericUpDown { Minimum = 16, Maximum = 4096, Value = 40, Left = 360, Top = 100, Width = 120 };
        dialog.Controls.Add(new Label { Text = "Windows проверит возможность уменьшения раздела.\nПеред изменением разметки будет показан точный план.", AutoSize = true, Left = 18, Top = 12 });
        dialog.Controls.Add(new Label { Text = "Новый NTFS-раздел для ISO, GiB:", AutoSize = true, Left = 18, Top = 60 }); dialog.Controls.Add(staging);
        dialog.Controls.Add(new Label { Text = "Незанятое место для Linux, GiB:", AutoSize = true, Left = 18, Top = 105 }); dialog.Controls.Add(linux);
        var ok = new Button { Text = "Рассчитать план", DialogResult = DialogResult.OK, Left = 280, Top = 160, Width = 200, Height = 40 }; dialog.Controls.Add(ok); dialog.AcceptButton = ok;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ShrinkPlan? proposal = null;
        await Execute(async token => { status.Text = "Проверка возможности уменьшения…"; proposal = await PartitionPreparation.PlanAsync(source, (int)staging.Value, (int)linux.Value, token); });
        if (proposal is null) return;
        if (MessageBox.Show(this, proposal.Summary + "\r\n\r\nВыполнить этот план?", "Изменение разделов", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await Execute(async _ =>
        {
            InvalidatePlan(); status.Text = "Изменение разделов. Не выключайте компьютер; дождитесь завершения…";
            string letter = await PartitionPreparation.ApplyAsync(proposal);
            Log($"Создан раздел {letter}: ISO_INSTALL. Для Linux оставлено {Units.Size(proposal.Unallocated)} незанятого места.");
            var environment = await BootStorage.ReadAsync(); volumes.Items.Clear();
            foreach (var p in environment.Partitions.Where(p => p.Letter.Length == 1 && !p.IsEsp)) volumes.Items.Add(p);
            volumes.SelectedItem = volumes.Items.Cast<BootPartition>().FirstOrDefault(p => p.Letter == letter);
            status.Text = "Раздел создан. Выберите ISO и проверьте план загрузки.";
        }, false);
    }
}
