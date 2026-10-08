using IsoWriter.InternalBoot;

namespace IsoWriter;

internal sealed class ShellForm : Form
{
    internal ShellForm(bool preview)
    {
        Text = "ISO Writer — установка Linux"; ClientSize = new Size(1020, 860); MinimumSize = new Size(960, 830);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var internalPage = new TabPage("Установка с внутреннего диска"); var rawPage = new TabPage("Перезапись отдельного диска (удаление данных)");
        tabs.TabPages.Add(internalPage); tabs.TabPages.Add(rawPage); Controls.Add(tabs);
        var preparation = new InternalBootForm(preview) { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill };
        internalPage.Controls.Add(preparation); preparation.Show();
        MainForm? raw = null;
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedIndex != 1 || raw is not null || preview) return;
            raw = new MainForm { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill };
            rawPage.Controls.Add(raw); raw.Show();
        };
        tabs.Selecting += (_, e) => { if (preparation.Busy || raw?.Busy == true) e.Cancel = true; };
        FormClosing += (_, e) =>
        {
            if (!preparation.Busy && raw?.Busy != true) return;
            e.Cancel = true; MessageBox.Show(this, "Дождитесь завершения операции или нажмите «Отменить» в активном режиме.", "Операция выполняется");
        };
        if (preview) Shown += (_, _) =>
        {
            using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, "preview-internal.png")); Close();
        };
    }
}
