using System.Runtime.InteropServices;
using RunAsLauncher.Policies;

namespace RunAsLauncher.Admin;

/// <summary>
/// Console de administração: CRUD dos itens da política, reordenação, drag &amp; drop de <c>.exe</c>/<c>.lnk</c>,
/// "atualizar hash de todos", import/export JSON e gravação via <see cref="PolicyStore.Save"/> (que valida).
/// A aba "Auditoria" mostra os eventos do serviço.
/// </summary>
internal sealed class AdminForm : Form
{
    private readonly PolicyStore _store;
    private readonly List<PolicyItem> _items = [];

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        MultiSelect = false,
        HideSelection = false,
        FullRowSelect = true,
        AllowDrop = true,
    };
    private readonly Button _add = new() { AutoSize = true };
    private readonly Button _edit = new() { AutoSize = true };
    private readonly Button _duplicate = new() { AutoSize = true };
    private readonly Button _remove = new() { AutoSize = true };
    private readonly Button _up = new() { AutoSize = true };
    private readonly Button _down = new() { AutoSize = true };
    private readonly Button _import = new() { AutoSize = true };
    private readonly Button _export = new() { AutoSize = true };
    private readonly Button _rehash = new() { AutoSize = true };
    private readonly Button _save = new() { AutoSize = true };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly AuditView _audit = new();

    private bool _dirty;
    private string? _message;
    // Incrementado a cada mudança da lista: descarta resultados de verificação que chegam atrasados.
    private int _version;
    private HashSet<string> _invalidIds = [];

    public AdminForm(PolicyStore store)
    {
        _store = store;

        Text = Strings.AppName;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(LogicalToDeviceUnits(980), LogicalToDeviceUnits(560));
        MinimumSize = new Size(LogicalToDeviceUnits(720), LogicalToDeviceUnits(360));
        KeyPreview = true;
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // Sem ícone na janela: não impede o uso.
        }

        _list.Columns.Add(Strings.ColumnTitle, LogicalToDeviceUnits(200));
        _list.Columns.Add(Strings.ColumnPath, LogicalToDeviceUnits(330));
        _list.Columns.Add(Strings.ColumnMode, LogicalToDeviceUnits(110));
        _list.Columns.Add(Strings.ColumnVerification, LogicalToDeviceUnits(130));
        _list.Columns.Add(Strings.ColumnCurrent, LogicalToDeviceUnits(170));

        (Button Button, string Text, Action OnClick)[] buttons =
        [
            (_add, Strings.Add, AddItem),
            (_edit, Strings.Edit, EditSelected),
            (_duplicate, Strings.Duplicate, DuplicateSelected),
            (_remove, Strings.Remove, RemoveSelected),
            (_up, Strings.MoveUp, () => MoveSelected(-1)),
            (_down, Strings.MoveDown, () => MoveSelected(+1)),
            (_import, Strings.Import, ImportPolicy),
            (_export, Strings.Export, ExportPolicy),
            (_rehash, Strings.RehashAll, RehashAll),
            (_save, Strings.Save, () => SavePolicy()),
        ];
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(LogicalToDeviceUnits(4)) };
        foreach (var (button, text, onClick) in buttons)
        {
            button.Text = text;
            button.Click += (_, _) => onClick();
            bar.Controls.Add(button);
        }

        _save.Font = new Font(_save.Font, FontStyle.Bold);

        var itemsTab = new TabPage(Strings.TabItems);
        itemsTab.Controls.Add(_list);
        itemsTab.Controls.Add(bar);
        var auditTab = new TabPage(Strings.TabAudit);
        auditTab.Controls.Add(_audit);
        _tabs.TabPages.Add(itemsTab);
        _tabs.TabPages.Add(auditTab);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);

        Controls.Add(_tabs);
        Controls.Add(statusStrip);

        _list.ItemActivate += (_, _) => EditSelected();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DragEnter += OnDragEnter;
        _list.DragDrop += OnDragDrop;
        KeyDown += OnFormKeyDown;
        FormClosing += OnFormClosing;
        Shown += (_, _) => LoadPolicy();
    }

    private int SelectedIndex => _list.SelectedIndices.Count == 1 ? _list.SelectedIndices[0] : -1;

    private void LoadPolicy()
    {
        PolicyLoadResult result = _store.Load();
        if (!result.Success)
        {
            ShowWarning(string.Format(Strings.LoadRejected, result.Reason));
        }

        _items.Clear();
        _items.AddRange(result.Policy?.Items ?? []);
        SetDirty(false);
        Populate(select: 0);
    }

    // ---- Lista ----

    private void Populate(int select)
    {
        int version = ++_version;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (PolicyItem item in _items)
            {
                var row = new ListViewItem(item.Title) { ToolTipText = item.Id, UseItemStyleForSubItems = false };
                row.SubItems.Add(item.TargetPath);
                row.SubItems.Add(Strings.ModeText(item.Mode));
                row.SubItems.Add(VerificationText(item));
                row.SubItems.Add(Strings.StatusChecking);
                if (_invalidIds.Contains(item.Id))
                {
                    row.ForeColor = Color.Firebrick;
                }

                _list.Items.Add(row);
            }

            if (_list.Items.Count > 0)
            {
                int index = Math.Clamp(select, 0, _list.Items.Count - 1);
                _list.Items[index].Selected = true;
                _list.Items[index].Focused = true;
                _list.EnsureVisible(index);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        UpdateButtons();
        UpdateStatus();
        _ = RefreshFileStatusAsync(version);
    }

    /// <summary>Confere cada arquivo atual (hash/assinatura) fora da thread de UI e preenche a coluna ✔/✖.</summary>
    private async Task RefreshFileStatusAsync(int version)
    {
        PolicyItem[] snapshot = [.. _items];
        var statuses = await Task.Run(() => snapshot.Select(TargetFiles.Status).ToArray());
        if (version != _version || IsDisposed)
        {
            return;
        }

        for (int i = 0; i < statuses.Length && i < _list.Items.Count; i++)
        {
            ListViewItem.ListViewSubItem cell = _list.Items[i].SubItems[4];
            cell.Text = statuses[i].Text;
            cell.ForeColor = statuses[i].Ok ? Color.ForestGreen : Color.Firebrick;
        }
    }

    private static string VerificationText(PolicyItem item) => (item.Sha256, item.Publisher) switch
    {
        (not null, not null) => Strings.VerifyBoth,
        (not null, null) => Strings.VerifyHash,
        (null, not null) => Strings.VerifyPublisher,
        _ => Strings.VerifyNone,
    };

    private void UpdateButtons()
    {
        int index = SelectedIndex;
        bool selected = index >= 0;
        _edit.Enabled = selected;
        _duplicate.Enabled = selected;
        _remove.Enabled = selected;
        _up.Enabled = index > 0;
        _down.Enabled = selected && index < _items.Count - 1;
        _rehash.Enabled = _items.Count > 0;
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        UpdateStatus();
    }

    private void UpdateStatus(string? message = null)
    {
        if (message is not null)
        {
            _message = message;
        }

        string text = string.Format(Strings.ItemCount, _items.Count) + (_dirty ? Strings.Unsaved : string.Empty);
        _status.Text = _message is null ? text : text + "  |  " + _message;
    }

    /// <summary>Aplica uma mudança da lista: marca como não salvo e redesenha.</summary>
    private void Changed(int select, string? message = null)
    {
        _invalidIds = [];
        _message = message;
        SetDirty(true);
        Populate(select);
    }

    // ---- Ações ----

    private PolicyItem? Edit(PolicyItem? item, IEnumerable<string> otherIds)
    {
        using var editor = new ItemEditorForm(item, otherIds);
        return editor.ShowDialog(this) == DialogResult.OK ? editor.Result : null;
    }

    private void AddItem() => AddEdited(null);

    /// <summary>Abre o editor (vazio ou pré-preenchido) e, se confirmado, insere o item após o selecionado.</summary>
    private void AddEdited(PolicyItem? prefilled)
    {
        if (Edit(prefilled, _items.Select(i => i.Id)) is { } item)
        {
            int at = SelectedIndex >= 0 ? SelectedIndex + 1 : _items.Count;
            _items.Insert(at, item);
            Changed(at);
        }
    }

    private void EditSelected()
    {
        int index = SelectedIndex;
        if (index < 0)
        {
            return;
        }

        if (Edit(_items[index], _items.Where((_, i) => i != index).Select(i => i.Id)) is { } item)
        {
            _items[index] = item;
            Changed(index);
        }
    }

    private void DuplicateSelected()
    {
        int index = SelectedIndex;
        if (index < 0)
        {
            return;
        }

        PolicyItem source = _items[index];
        PolicyItem copy = source with { Id = TargetFiles.UniqueId(source.Id, _items.Select(i => i.Id)) };
        _items.Insert(index + 1, copy);
        Changed(index + 1);
    }

    private void RemoveSelected()
    {
        int index = SelectedIndex;
        if (index < 0 || !Confirm(string.Format(Strings.RemoveConfirm, _items[index].Title)))
        {
            return;
        }

        _items.RemoveAt(index);
        Changed(index);
    }

    private void MoveSelected(int delta)
    {
        int index = SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _items.Count)
        {
            return;
        }

        (_items[index], _items[target]) = (_items[target], _items[index]);
        Changed(target);
    }

    private void RehashAll()
    {
        if (!Confirm(Strings.RehashConfirm))
        {
            return;
        }

        int updated = 0;
        var failures = new List<string>();
        UseWaitCursor = true;
        try
        {
            for (int i = 0; i < _items.Count; i++)
            {
                PolicyItem item = _items[i];
                if (item.Sha256 is null)
                {
                    continue; // Item só por assinatura: não ganha hash sem o administrador pedir.
                }

                try
                {
                    var (sha256, size) = TargetFiles.Hash(item.TargetPath);
                    PolicyItem next = item with { Sha256 = sha256, ExpectedSize = item.ExpectedSize is null ? null : size };
                    if (next != item)
                    {
                        _items[i] = next;
                        updated++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    failures.Add($"{item.Title}: {ex.Message}");
                }
            }
        }
        finally
        {
            UseWaitCursor = false;
        }

        if (updated > 0)
        {
            Changed(SelectedIndex, string.Format(Strings.Rehashed, updated));
        }
        else
        {
            Populate(SelectedIndex);
            UpdateStatus(string.Format(Strings.Rehashed, 0));
        }

        if (failures.Count > 0)
        {
            ShowWarning(string.Format(Strings.RehashFailures, string.Join(Environment.NewLine, failures)));
        }
    }

    private void ImportPolicy()
    {
        if (_dirty && !Confirm(Strings.DiscardConfirm))
        {
            return;
        }

        using var dialog = new OpenFileDialog { Filter = Strings.JsonFilter };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        PolicyLoadResult result = _store.Import(dialog.FileName);
        if (!result.Success)
        {
            ShowWarning(string.Format(Strings.ImportRejected, result.Reason));
            return;
        }

        _items.Clear();
        _items.AddRange(result.Policy!.Items);
        Changed(0, string.Format(Strings.Imported, _items.Count, Path.GetFileName(dialog.FileName)));
    }

    private void ExportPolicy()
    {
        Policy policy = CurrentPolicy();
        if (!CheckValid(policy))
        {
            return;
        }

        using var dialog = new SaveFileDialog { Filter = Strings.JsonFilter, FileName = "policy.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _store.Export(dialog.FileName, policy);
            UpdateStatus(string.Format(Strings.Exported, dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowWarning(string.Format(Strings.SaveFailed, ex.Message));
        }
    }

    private bool SavePolicy()
    {
        Policy policy = CurrentPolicy();
        if (!CheckValid(policy))
        {
            return false;
        }

        try
        {
            _store.Save(policy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowWarning(string.Format(Strings.SaveFailed, ex.Message));
            return false;
        }

        SetDirty(false);
        UpdateStatus(string.Format(Strings.Saved, _store.FilePath));
        return true;
    }

    private Policy CurrentPolicy() => new(PolicyValidator.CurrentSchemaVersion, [.. _items]);

    /// <summary>Valida como o <see cref="PolicyStore"/> faria; mostra os erros por item e pede confirmação dos avisos.</summary>
    private bool CheckValid(Policy policy)
    {
        ValidationResult result = PolicyValidator.Validate(policy);
        if (!result.IsValid)
        {
            _invalidIds = result.Errors.Where(e => e.ItemId is not null).Select(e => e.ItemId!).ToHashSet(StringComparer.Ordinal);
            Populate(SelectedIndex);
            ShowWarning(string.Format(Strings.SaveInvalid, Describe(result.Errors)));
            return false;
        }

        _invalidIds = [];
        return !result.Warnings.Any() || Confirm(string.Format(Strings.SaveWarnings, Describe(result.Warnings)));
    }

    /// <summary>Uma linha por mensagem, identificando o item pelo título (o id sozinho é pouco legível).</summary>
    private string Describe(IEnumerable<ValidationError> messages) => string.Join(Environment.NewLine, messages.Select(m =>
    {
        string who = m.ItemId is null
            ? Strings.General
            : _items.FirstOrDefault(i => i.Id == m.ItemId) is { } item ? $"{item.Title} ({item.Id})" : m.ItemId;
        return $"• {who}: {m.Message}";
    }));

    // ---- Drag & drop ----

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) as string[] ?? [];

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        bool accept = DroppedFiles(e).Any(f =>
            f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase));
        e.Effect = accept ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>Cada <c>.exe</c> (ou <c>.lnk</c> resolvido) abre o editor com um item novo pré-preenchido.</summary>
    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        var problems = new List<string>();
        foreach (string file in DroppedFiles(e))
        {
            string exe = file;
            string? arguments = null, workingDirectory = null;
            if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ShortcutTarget target = TargetFiles.ResolveShortcut(file);
                    (exe, arguments, workingDirectory) = (target.TargetPath, target.Arguments, target.WorkingDirectory);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    problems.Add(string.Format(Strings.DropLnkFailed, Path.GetFileName(file), ex.Message));
                    continue;
                }
            }

            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe))
            {
                problems.Add(string.Format(Strings.DropNotExe, string.IsNullOrEmpty(exe) ? file : exe));
                continue;
            }

            AddEdited(TargetFiles.NewItemFor(exe, arguments, workingDirectory, _items.Select(i => i.Id)));
        }

        if (problems.Count > 0)
        {
            ShowWarning(string.Join(Environment.NewLine, problems));
        }
    }

    // ---- Janela ----

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.S)
        {
            e.Handled = true;
            SavePolicy();
        }
        else if (e.KeyCode == Keys.Delete && _list.Focused)
        {
            e.Handled = true;
            RemoveSelected();
        }
        else if (e.KeyCode == Keys.Insert && _list.Focused)
        {
            e.Handled = true;
            AddItem();
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_dirty)
        {
            return;
        }

        DialogResult answer = MessageBox.Show(this, Strings.DiscardConfirm, Strings.AppName,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            e.Cancel = true;
        }
    }

    private bool Confirm(string text) =>
        MessageBox.Show(this, text, Strings.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void ShowWarning(string text) =>
        MessageBox.Show(this, text, Strings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
