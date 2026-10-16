using System.Globalization;
using System.Security.Principal;
using RunAsLauncher.Policies;
using RunAsLauncher.Verification;

namespace RunAsLauncher.Admin;

/// <summary>
/// Edição de um <see cref="PolicyItem"/>: todos os campos, "Calcular hash", "Ler assinatura", seletor de
/// usuários/grupos (nome → SID via <see cref="NTAccount.Translate"/>) e avisos de alto risco/pasta não protegida.
/// O resultado (<see cref="Result"/>) já passou pelo <see cref="PolicyValidator"/>.
/// </summary>
internal sealed class ItemEditorForm : Form
{
    /// <summary>Opção de ComboBox com texto localizado.</summary>
    private sealed record Choice<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>Linha da lista de contas: guarda o SID, mostra o nome.</summary>
    private sealed record Account(string Sid, string Name)
    {
        public override string ToString() => Name == Sid ? Sid : $"{Name}  ({Sid})";
    }

    private readonly HashSet<string> _otherIds;

    private readonly TextBox _id = new() { Dock = DockStyle.Fill };
    private readonly TextBox _title = new() { Dock = DockStyle.Fill };
    private readonly TextBox _target = new() { Dock = DockStyle.Fill };
    private readonly TextBox _arguments = new() { Dock = DockStyle.Fill };
    private readonly TextBox _workingDirectory = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _mode = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _window = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _children = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _sha256 = new() { Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9f) };
    private readonly TextBox _size = new() { Dock = DockStyle.Fill };
    private readonly TextBox _subject = new() { Dock = DockStyle.Fill };
    private readonly TextBox _thumbprint = new() { Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9f) };
    private readonly CheckBox _protected = new() { AutoSize = true };
    private readonly TextBox _icon = new() { Dock = DockStyle.Fill };
    private readonly ListBox _accounts = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _accountInput = new() { Dock = DockStyle.Fill };
    private readonly Label _warnings = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DarkOrange };
    private readonly Label _locationWarning = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.Firebrick };

    private string? _lastCheckedLocation;
    private bool _idEdited;

    public ItemEditorForm(PolicyItem? item, IEnumerable<string> otherIds)
    {
        _otherIds = new HashSet<string>(otherIds, StringComparer.Ordinal);

        Text = Strings.EditorTitle;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(LogicalToDeviceUnits(680), LogicalToDeviceUnits(720));
        MinimumSize = new Size(LogicalToDeviceUnits(560), LogicalToDeviceUnits(600));

        foreach (RunMode m in Enum.GetValues<RunMode>())
        {
            _mode.Items.Add(new Choice<RunMode>(m, Strings.ModeText(m)));
        }

        foreach (WindowMode w in Enum.GetValues<WindowMode>())
        {
            _window.Items.Add(new Choice<WindowMode>(w, Strings.WindowText(w)));
        }

        foreach (ChildProcessPolicy c in Enum.GetValues<ChildProcessPolicy>())
        {
            _children.Items.Add(new Choice<ChildProcessPolicy>(c, Strings.ChildrenText(c)));
        }

        _protected.Text = Strings.FieldProtected;
        _workingDirectory.PlaceholderText = Strings.WorkingDirectoryHint;
        _size.PlaceholderText = Strings.SizeHint;
        _accountInput.PlaceholderText = Strings.AccountHint;

        BuildLayout();
        LoadItem(item);

        _title.TextChanged += (_, _) => SuggestId();
        _id.TextChanged += (_, _) => _idEdited = _id.Focused || _idEdited;
        _target.TextChanged += (_, _) => UpdateRiskWarnings();
        _target.Leave += (_, _) => CheckLocation();
        _mode.SelectedIndexChanged += (_, _) => UpdateRiskWarnings();
        _children.SelectedIndexChanged += (_, _) => UpdateRiskWarnings();
        _protected.CheckedChanged += (_, _) => CheckLocation(force: true);
        _accountInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddAccount();
            }
        };
        Shown += (_, _) => CheckLocation();
    }

    /// <summary>O item validado, depois de OK.</summary>
    public PolicyItem? Result { get; private set; }

    private void BuildLayout()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            AutoScroll = true,
            Padding = new Padding(LogicalToDeviceUnits(8)),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Row(string label, Control field, Control? button = null)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, row);
            grid.Controls.Add(field, 1, row);
            if (button is not null)
            {
                grid.Controls.Add(button, 2, row);
            }
        }

        Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += onClick;
            return b;
        }

        Row(Strings.FieldTitle, _title);
        Row(Strings.FieldId, _id);
        Row(Strings.FieldTarget, _target, MakeButton(Strings.Browse, (_, _) => BrowseTarget()));
        Row(Strings.FieldArguments, _arguments);
        Row(Strings.FieldWorkingDirectory, _workingDirectory, MakeButton(Strings.Browse, (_, _) => BrowseWorkingDirectory()));
        Row(Strings.FieldMode, _mode);
        Row(Strings.FieldWindow, _window);
        Row(Strings.FieldChildren, _children);
        Row(Strings.FieldSha256, _sha256, MakeButton(Strings.ComputeHash, (_, _) => ComputeHash()));
        Row(Strings.FieldSize, _size);
        Row(Strings.FieldSubject, _subject, MakeButton(Strings.ReadSignature, (_, _) => ReadSignature()));
        Row(Strings.FieldThumbprint, _thumbprint);
        Row(string.Empty, _protected);
        Row(Strings.FieldIcon, _icon, MakeButton(Strings.Browse, (_, _) => BrowseIcon()));

        // Contas: lista + caixa de entrada com Adicionar/Remover.
        var accountButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        accountButtons.Controls.Add(MakeButton(Strings.AddAccount, (_, _) => AddAccount()));
        accountButtons.Controls.Add(MakeButton(Strings.RemoveAccount, (_, _) => RemoveAccount()));
        _accounts.Height = LogicalToDeviceUnits(110);
        Row(Strings.FieldAllowed, _accounts, accountButtons);
        Row(string.Empty, _accountInput);

        int warnRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(_warnings, 0, warnRow);
        grid.SetColumnSpan(_warnings, 3);
        int locRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(_locationWarning, 0, locRow);
        grid.SetColumnSpan(_locationWarning, 3);

        var ok = new Button { Text = Strings.Ok, AutoSize = true };
        var cancel = new Button { Text = Strings.Cancel, AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Accept();
        AcceptButton = ok;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(LogicalToDeviceUnits(8)),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(grid);
        Controls.Add(buttons);
    }

    private void LoadItem(PolicyItem? item)
    {
        _idEdited = item is not null;
        _title.Text = item?.Title ?? string.Empty;
        _id.Text = item?.Id ?? string.Empty;
        _target.Text = item?.TargetPath ?? string.Empty;
        _arguments.Text = item?.Arguments ?? string.Empty;
        _workingDirectory.Text = item?.WorkingDirectory ?? string.Empty;
        Select(_mode, item?.Mode ?? RunMode.Elevated);
        Select(_window, item?.Window ?? WindowMode.Normal);
        Select(_children, item?.Children ?? ChildProcessPolicy.Deny);
        _sha256.Text = item?.Sha256 ?? string.Empty;
        _size.Text = item?.ExpectedSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _subject.Text = item?.Publisher?.SubjectContains ?? string.Empty;
        _thumbprint.Text = item?.Publisher?.Thumbprint ?? string.Empty;
        _protected.Checked = item?.RequireProtectedLocation ?? true;
        _icon.Text = item?.IconPath ?? string.Empty;
        foreach (string sid in item?.AllowedSids ?? [])
        {
            _accounts.Items.Add(new Account(sid, TargetFiles.DisplayName(sid)));
        }

        UpdateRiskWarnings();
    }

    private static void Select<T>(ComboBox combo, T value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(((Choice<T>)combo.Items[i]!).Value, value))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private static T Selected<T>(ComboBox combo) => ((Choice<T>)combo.SelectedItem!).Value;

    /// <summary>Enquanto o administrador não mexe no id, ele acompanha o título.</summary>
    private void SuggestId()
    {
        if (!_idEdited)
        {
            _id.Text = TargetFiles.UniqueId(TargetFiles.Slugify(_title.Text), _otherIds);
        }
    }

    private void BrowseTarget()
    {
        using var dialog = new OpenFileDialog { Filter = Strings.ExeFilter, FileName = _target.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _target.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(_title.Text))
            {
                _title.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            }

            CheckLocation();
        }
    }

    private void BrowseWorkingDirectory()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _workingDirectory.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _workingDirectory.Text = dialog.SelectedPath;
        }
    }

    private void BrowseIcon()
    {
        using var dialog = new OpenFileDialog { Filter = Strings.IconFilter, FileName = _icon.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _icon.Text = dialog.FileName;
        }
    }

    private void ComputeHash()
    {
        try
        {
            UseWaitCursor = true;
            var (sha256, size) = TargetFiles.Hash(_target.Text.Trim());
            _sha256.Text = sha256;
            // Tamanho só é atualizado se o administrador já optou por conferi-lo.
            if (_size.Text.Trim().Length > 0)
            {
                _size.Text = size.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowWarning(string.Format(Strings.HashFailed, ex.Message));
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void ReadSignature()
    {
        string path = _target.Text.Trim();
        if (path.Length == 0 || !File.Exists(path))
        {
            ShowWarning(Strings.StatusMissing);
            return;
        }

        UseWaitCursor = true;
        TrustResult trust;
        try
        {
            trust = AuthenticodeVerifier.Verify(path);
        }
        finally
        {
            UseWaitCursor = false;
        }

        if (trust.Subject is null)
        {
            ShowWarning(Strings.NotSigned);
            return;
        }

        _subject.Text = trust.Subject;
        _thumbprint.Text = trust.Thumbprint ?? string.Empty;
        if (!trust.Valid)
        {
            ShowWarning(Strings.SignatureUntrusted);
        }
    }

    private void AddAccount()
    {
        string input = _accountInput.Text.Trim();
        if (input.Length == 0)
        {
            return;
        }

        string sid;
        try
        {
            sid = TargetFiles.ResolveSid(input);
        }
        catch (SystemException)
        {
            // IdentityNotMappedException (nome inexistente), ArgumentException (SID malformado), falha com o domínio.
            ShowWarning(string.Format(Strings.AccountNotFound, input));
            return;
        }

        if (!_accounts.Items.Cast<Account>().Any(a => a.Sid == sid))
        {
            _accounts.Items.Add(new Account(sid, TargetFiles.DisplayName(sid)));
        }

        _accountInput.Clear();
        UpdateRiskWarnings();
    }

    private void RemoveAccount()
    {
        if (_accounts.SelectedIndex >= 0)
        {
            _accounts.Items.RemoveAt(_accounts.SelectedIndex);
            UpdateRiskWarnings();
        }
    }

    /// <summary>Avisos baratos (sem I/O): alvo de alto risco, Administrador + filhos, nenhuma conta.</summary>
    private void UpdateRiskWarnings()
    {
        if (_mode.SelectedItem is null || _children.SelectedItem is null)
        {
            return;
        }

        var lines = new List<string>();
        string target = _target.Text.Trim();
        if (HighRiskTargets.IsHighRiskFile(target))
        {
            lines.Add(string.Format(Strings.WarnHighRiskTarget, Path.GetFileName(target)));
        }

        if (Selected<RunMode>(_mode) == RunMode.Elevated && Selected<ChildProcessPolicy>(_children) == ChildProcessPolicy.Allow)
        {
            lines.Add(Strings.WarnHighRiskChildren);
        }

        if (_accounts.Items.Count == 0)
        {
            lines.Add(Strings.WarnNoAccounts);
        }

        _warnings.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>Aviso imediato se <see cref="ProtectedLocationChecker"/> reprova o caminho (lê DACLs: só ao sair do campo).</summary>
    private void CheckLocation(bool force = false)
    {
        string target = _target.Text.Trim();
        if (!force && target == _lastCheckedLocation)
        {
            return;
        }

        _lastCheckedLocation = target;
        _locationWarning.Text = string.Empty;
        if (target.Length == 0 || !Path.IsPathFullyQualified(target) || !File.Exists(target))
        {
            return;
        }

        // Sem chamador específico: só os SIDs de usuários comuns (Everyone, Users, Authenticated Users, Interactive).
        var (ok, offending) = ProtectedLocationChecker.Check(target, string.Empty, []);
        if (!ok)
        {
            _locationWarning.Text = string.Format(Strings.WarnUnprotected, offending);
        }
    }

    private void Accept()
    {
        if (!TryBuild(out PolicyItem? item, out string? error))
        {
            ShowWarning(string.Format(Strings.ItemInvalid, error));
            return;
        }

        Result = item;
        DialogResult = DialogResult.OK;
        Close();
    }

    private bool TryBuild(out PolicyItem? item, out string? error)
    {
        item = null;
        error = null;

        long? size = null;
        string sizeText = _size.Text.Trim();
        if (sizeText.Length > 0)
        {
            if (!long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
            {
                error = Strings.SizeInvalid;
                return false;
            }

            size = parsed;
        }

        string id = _id.Text.Trim();
        if (_otherIds.Contains(id))
        {
            error = string.Format(Strings.DuplicateId, id);
            return false;
        }

        string subject = _subject.Text.Trim();
        string thumbprint = _thumbprint.Text.Trim();
        string sha256 = _sha256.Text.Trim().ToUpperInvariant();

        var candidate = new PolicyItem(
            id,
            _title.Text.Trim(),
            _target.Text.Trim(),
            NullIfEmpty(_arguments.Text),
            NullIfEmpty(_workingDirectory.Text),
            Selected<RunMode>(_mode),
            Selected<WindowMode>(_window),
            Selected<ChildProcessPolicy>(_children),
            sha256.Length == 0 ? null : sha256,
            subject.Length == 0 && thumbprint.Length == 0 ? null : new PublisherRule(subject, NullIfEmpty(thumbprint)),
            size,
            _protected.Checked,
            _accounts.Items.Cast<Account>().Select(a => a.Sid).ToList(),
            NullIfEmpty(_icon.Text));

        // Mesma validação do Salvar, aplicada só a este item (avisos ficam para o Salvar).
        var result = PolicyValidator.Validate(new Policy(PolicyValidator.CurrentSchemaVersion, [candidate]));
        if (!result.IsValid)
        {
            error = string.Join(Environment.NewLine, result.Errors.Select(e => "• " + e.Message));
            return false;
        }

        item = candidate;
        return true;
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void ShowWarning(string text) =>
        MessageBox.Show(this, text, Strings.EditorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
