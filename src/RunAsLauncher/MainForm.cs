using System.ComponentModel;
using System.Diagnostics;
using RunAsLauncher.Ipc;
using RunAsLauncher.Policies;

namespace RunAsLauncher;

/// <summary>
/// Lista dos programas liberados para o usuário (vinda do serviço), com busca por título.
/// Duplo clique ou Enter executa; o menu de contexto cria atalho, copia o comando ou abre a pasta do alvo.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly BrokerClient _broker;

    private readonly TextBox _search = new() { Dock = DockStyle.Fill };
    private readonly Button _refresh = new() { AutoSize = true };
    private readonly Button _toggleView = new() { AutoSize = true };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.LargeIcon,
        MultiSelect = false,
        HideSelection = false,
        FullRowSelect = true,
        Sorting = SortOrder.Ascending,
    };
    private readonly ImageList _largeImages = new() { ColorDepth = ColorDepth.Depth32Bit };
    private readonly ImageList _smallImages = new() { ColorDepth = ColorDepth.Depth32Bit };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _menuRun = new();
    private readonly ToolStripMenuItem _menuShortcut = new();
    private readonly ToolStripMenuItem _menuCopy = new();
    private readonly ToolStripMenuItem _menuOpenLocation = new();

    private IReadOnlyList<ItemView> _items = [];
    // ImageList guarda referências aos originais até criar o handle: só liberar depois de Clear.
    private List<Image> _ownedImages = [];
    private bool _busy;

    public MainForm(BrokerClient broker)
    {
        _broker = broker;

        Text = Strings.AppName;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(440));
        MinimumSize = new Size(LogicalToDeviceUnits(360), LogicalToDeviceUnits(260));
        KeyPreview = true;
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // Sem ícone na janela: não impede o uso.
        }

        _largeImages.ImageSize = new Size(LogicalToDeviceUnits(32), LogicalToDeviceUnits(32));
        _smallImages.ImageSize = new Size(LogicalToDeviceUnits(16), LogicalToDeviceUnits(16));
        _list.LargeImageList = _largeImages;
        _list.SmallImageList = _smallImages;
        _list.Columns.Add(Strings.ColumnTitle, LogicalToDeviceUnits(220));
        _list.Columns.Add(Strings.ColumnMode, LogicalToDeviceUnits(110));
        _list.Columns.Add(Strings.ColumnPath, LogicalToDeviceUnits(280));
        _list.ContextMenuStrip = _menu;

        _search.PlaceholderText = Strings.SearchPlaceholder;
        _refresh.Text = Strings.Refresh;
        _toggleView.Text = Strings.ViewDetails;

        _menuRun.Text = Strings.MenuRun;
        _menuRun.Font = new Font(_menuRun.Font, FontStyle.Bold);
        _menuShortcut.Text = Strings.MenuShortcut;
        _menuCopy.Text = Strings.MenuCopyCommand;
        _menuOpenLocation.Text = Strings.MenuOpenLocation;
        _menu.Items.AddRange([_menuRun, new ToolStripSeparator(), _menuShortcut, _menuCopy, _menuOpenLocation]);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(LogicalToDeviceUnits(6)),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(_search, 0, 0);
        top.Controls.Add(_toggleView, 1, 0);
        top.Controls.Add(_refresh, 2, 0);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);

        // Ordem de Dock: o controle Fill entra primeiro para ocupar o espaço que sobra.
        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(statusStrip);

        _search.TextChanged += (_, _) => Populate();
        _search.KeyDown += OnSearchKeyDown;
        _refresh.Click += async (_, _) => await RefreshItemsAsync();
        _toggleView.Click += (_, _) => ToggleView();
        _list.ItemActivate += async (_, _) => await RunSelectedAsync();
        _menu.Opening += OnMenuOpening;
        _menuRun.Click += async (_, _) => await RunSelectedAsync();
        _menuShortcut.Click += (_, _) => CreateShortcut();
        _menuCopy.Click += (_, _) => CopyCommand();
        _menuOpenLocation.Click += (_, _) => OpenLocation();
        KeyDown += OnFormKeyDown;
        Shown += async (_, _) => await RefreshItemsAsync();
    }

    private ItemView? Selected =>
        _list.SelectedItems.Count == 1 ? (ItemView)_list.SelectedItems[0].Tag! : null;

    private async Task RefreshItemsAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        SetStatus(Strings.Loading, error: false);
        try
        {
            Response response = await _broker.ListAsync();
            IReadOnlyList<ItemView> items = response.Items ?? [];
            // Extrair ícones toca o disco (às vezes a rede): fora da thread de UI.
            Size large = _largeImages.ImageSize, small = _smallImages.ImageSize;
            var images = await Task.Run(() => items.Select(i => BuildImages(i, large, small)).ToList());
            ReplaceItems(items, images);

            if (response.Code != ResultCode.Ok)
            {
                SetStatus(Strings.ForResult(response.Code), error: true);
            }
            else
            {
                SetStatus(items.Count == 0 ? Strings.NoItems : string.Format(Strings.ItemCount, items.Count), error: false);
            }
        }
        catch (BrokerException ex)
        {
            ReplaceItems([], []);
            SetStatus(ex.Message, error: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunSelectedAsync()
    {
        ItemView? item = Selected;
        if (item is null || _busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            Response response = await _broker.RunAsync(item.Id);
            if (response.Code == ResultCode.Ok)
            {
                SetStatus(string.Format(Strings.Started, item.Title), error: false);
            }
            else
            {
                ShowWarning(Strings.Describe(response.Code, response.Message));
            }
        }
        catch (BrokerException ex)
        {
            ShowWarning(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CreateShortcut()
    {
        if (Selected is not { } item)
        {
            return;
        }

        try
        {
            string path = ShortcutWriter.CreateOnDesktop(item.Id, item.Title, item.IconPath ?? item.TargetPath);
            SetStatus(string.Format(Strings.ShortcutCreated, Path.GetFileName(path)), error: false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowWarning(string.Format(Strings.ShortcutFailed, ex.Message));
        }
    }

    private void CopyCommand()
    {
        if (Selected is not { } item)
        {
            return;
        }

        try
        {
            Clipboard.SetText(ShortcutWriter.CommandLine(item.Id));
            SetStatus(Strings.CommandCopied, error: false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.ExternalException)
        {
            ShowWarning(ex.Message);
        }
    }

    private void OpenLocation()
    {
        if (Selected is not { } item)
        {
            return;
        }

        try
        {
            // Caminhos do Windows não contêm aspas; o caminho vem da política do administrador.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.TargetPath}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception ex)
        {
            ShowWarning(string.Format(Strings.OpenLocationFailed, ex.Message));
        }
    }

    private void ReplaceItems(IReadOnlyList<ItemView> items, List<(Image Large, Image Small)> images)
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            ClearImages();
            for (int i = 0; i < items.Count; i++)
            {
                _largeImages.Images.Add(items[i].Id, images[i].Large);
                _smallImages.Images.Add(items[i].Id, images[i].Small);
                _ownedImages.Add(images[i].Large);
                _ownedImages.Add(images[i].Small);
            }

            _items = items;
        }
        finally
        {
            _list.EndUpdate();
        }

        Populate();
    }

    private void Populate()
    {
        string filter = _search.Text.Trim();
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (ItemView item in _items)
            {
                if (filter.Length > 0 && !item.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                {
                    continue;
                }

                var row = new ListViewItem(item.Title, item.Id) { Tag = item, ToolTipText = item.TargetPath };
                row.SubItems.Add(Strings.ModeText(item.Mode));
                row.SubItems.Add(item.TargetPath);
                _list.Items.Add(row);
            }

            if (_list.Items.Count > 0)
            {
                _list.Items[0].Selected = true;
                _list.Items[0].Focused = true;
            }
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    private static (Image Large, Image Small) BuildImages(ItemView item, Size large, Size small)
    {
        using Icon? icon = LoadIcon(item);
        bool shield = item.Mode == RunMode.Elevated;
        return (Compose(icon, large, shield), Compose(icon, small, shield));
    }

    private static Icon? LoadIcon(ItemView item)
    {
        string source = item.IconPath ?? item.TargetPath;
        try
        {
            return source.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                ? new Icon(source, 256, 256)
                : Icon.ExtractAssociatedIcon(source);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Ícone do alvo no tamanho pedido; itens <see cref="RunMode.Elevated"/> recebem o escudo do UAC no canto.</summary>
    private static Bitmap Compose(Icon? icon, Size size, bool shield)
    {
        var bmp = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

        using (Bitmap source = (icon ?? SystemIcons.Application).ToBitmap())
        {
            g.DrawImage(source, new Rectangle(Point.Empty, size));
        }

        if (shield)
        {
            int side = Math.Max(8, size.Width / 2);
            using Icon stock = SystemIcons.GetStockIcon(StockIconId.Shield, side <= 16 ? StockIconOptions.SmallIcon : default);
            using Bitmap badge = stock.ToBitmap();
            g.DrawImage(badge, new Rectangle(size.Width - side, size.Height - side, side, side));
        }

        return bmp;
    }

    private void ClearImages()
    {
        _largeImages.Images.Clear();
        _smallImages.Images.Clear();
        foreach (Image image in _ownedImages)
        {
            image.Dispose();
        }

        _ownedImages = [];
    }

    private void ToggleView()
    {
        bool toDetails = _list.View == View.LargeIcon;
        _list.View = toDetails ? View.Details : View.LargeIcon;
        _toggleView.Text = toDetails ? Strings.ViewIcons : Strings.ViewDetails;
    }

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        if (Selected is null)
        {
            e.Cancel = true;
            return;
        }

        _menuRun.Enabled = !_busy;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter:
                e.SuppressKeyPress = true;
                _ = RunSelectedAsync();
                break;
            case Keys.Down when _list.Items.Count > 0:
                e.SuppressKeyPress = true;
                _list.Focus();
                break;
        }
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F5)
        {
            e.Handled = true;
            _ = RefreshItemsAsync();
        }
        else if (e.Control && e.KeyCode == Keys.F)
        {
            e.Handled = true;
            _search.Focus();
            _search.SelectAll();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _refresh.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text.Replace('\n', ' ');
        _status.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
    }

    private void ShowWarning(string text) =>
        MessageBox.Show(this, text, Strings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearImages();
            _largeImages.Dispose();
            _smallImages.Dispose();
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }
}
