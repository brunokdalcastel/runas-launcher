using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;

namespace RunAsLauncher.Admin;

/// <summary>Um evento de auditoria do serviço, já decomposto para a grade.</summary>
internal sealed record AuditEntry(DateTime Time, int EventId, string User, string Item, string Result, string Detail);

/// <summary>
/// Aba "Auditoria": os últimos <see cref="MaxEvents"/> eventos da fonte <c>RunAsLauncher</c> no log
/// <c>Application</c> (mais recentes primeiro), com exportação CSV.
/// </summary>
internal sealed class AuditView : UserControl
{
    public const int MaxEvents = 500;

    private const string LogName = "Application";
    private const string Query = "*[System[Provider[@Name='RunAsLauncher']]]";

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        BackgroundColor = SystemColors.Window,
    };
    private readonly Button _refresh = new() { AutoSize = true };
    private readonly Button _export = new() { AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 8, 0, 0) };

    private IReadOnlyList<AuditEntry> _entries = [];
    private bool _loaded;

    public AuditView()
    {
        Dock = DockStyle.Fill;

        _refresh.Text = Strings.AuditRefresh;
        _export.Text = Strings.AuditExport;

        AddColumn(Strings.AuditDate, 140);
        AddColumn(Strings.AuditEvent, 130);
        AddColumn(Strings.AuditUser, 160);
        AddColumn(Strings.AuditItem, 120);
        AddColumn(Strings.AuditResult, 140);
        var detail = AddColumn(Strings.AuditDetail, 200);
        detail.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        top.Controls.Add(_refresh);
        top.Controls.Add(_export);
        top.Controls.Add(_status);

        Controls.Add(_grid);
        Controls.Add(top);

        _refresh.Click += async (_, _) => await RefreshAsync();
        _export.Click += (_, _) => ExportCsv();
        VisibleChanged += async (_, _) =>
        {
            // Carrega na primeira vez que a aba aparece (ler o Event Log não deve atrasar a abertura do console).
            if (Visible && !_loaded)
            {
                _loaded = true;
                await RefreshAsync();
            }
        };
    }

    private DataGridViewTextBoxColumn AddColumn(string header, int width)
    {
        var column = new DataGridViewTextBoxColumn { HeaderText = header, Width = LogicalToDeviceUnits(width), SortMode = DataGridViewColumnSortMode.Automatic };
        _grid.Columns.Add(column);
        return column;
    }

    public async Task RefreshAsync()
    {
        _refresh.Enabled = false;
        UseWaitCursor = true;
        try
        {
            _entries = await Task.Run(ReadEvents);
            _grid.Rows.Clear();
            foreach (AuditEntry e in _entries)
            {
                int row = _grid.Rows.Add(
                    e.Time.ToString("g", CultureInfo.CurrentCulture), Strings.EventText(e.EventId), e.User, e.Item, e.Result, e.Detail);
                if (e.EventId is 1001 or 1002 or 1101)
                {
                    _grid.Rows[row].DefaultCellStyle.ForeColor = Color.Firebrick;
                }
            }

            _status.Text = _entries.Count == 0
                ? Strings.AuditNoSource
                : string.Format(Strings.AuditCount, _entries.Count, MaxEvents);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            _status.Text = string.Format(Strings.AuditReadFailed, ex.Message);
        }
        finally
        {
            _refresh.Enabled = true;
            UseWaitCursor = false;
        }
    }

    private static List<AuditEntry> ReadEvents()
    {
        var entries = new List<AuditEntry>(MaxEvents);
        var query = new EventLogQuery(LogName, PathType.LogName, Query) { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        while (entries.Count < MaxEvents)
        {
            using EventRecord? record = reader.ReadEvent();
            if (record is null)
            {
                break;
            }

            entries.Add(Parse(record.TimeCreated ?? DateTime.MinValue, record.Id, MessageOf(record)));
        }

        return entries;
    }

    /// <summary>
    /// Texto gravado pelo serviço. <c>EventLog.WriteEntry</c> guarda a mensagem como o único inserto do evento,
    /// então ela vem de <c>Properties[0]</c> mesmo sem a DLL de mensagens registrada.
    /// </summary>
    private static string MessageOf(EventRecord record)
    {
        if (record.Properties.Count > 0 && record.Properties[0].Value is string text)
        {
            return text;
        }

        try
        {
            return record.FormatDescription() ?? string.Empty;
        }
        catch (EventLogException)
        {
            return string.Empty;
        }
    }

    /// <summary>Decompõe a mensagem do <c>AuditLog</c> do serviço (linhas <c>Chave: valor</c>).</summary>
    internal static AuditEntry Parse(DateTime time, int eventId, string message)
    {
        string[] lines = message.Replace("\r\n", "\n").Split('\n');
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                fields.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
            }
        }

        string Field(string key) => fields.TryGetValue(key, out string? v) && v != "-" ? v : string.Empty;

        string headline = lines.Length > 0 ? lines[0].Trim() : string.Empty;
        string result = Field("Resultado");
        string detail = Field("Detalhe");
        if (fields.Count == 0 || (result.Length == 0 && detail.Length == 0))
        {
            // Eventos de política/serviço: a mensagem inteira é o detalhe.
            detail = string.Join(" ", lines.Select(l => l.Trim()).Where(l => l.Length > 0));
        }
        else if (detail.Length == 0)
        {
            detail = Field("Caminho");
        }

        return new AuditEntry(time, eventId, Field("Usuário"), Field("Item"), result.Length > 0 ? result : headline, detail);
    }

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = Strings.CsvFilter,
            FileName = $"runaslauncher-audit-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[]
        {
            Strings.AuditDate, Strings.AuditEvent, "EventId", Strings.AuditUser, Strings.AuditItem, Strings.AuditResult, Strings.AuditDetail,
        }.Select(Csv)));
        foreach (AuditEntry e in _entries)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                e.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Strings.EventText(e.EventId),
                e.EventId.ToString(CultureInfo.InvariantCulture),
                e.User, e.Item, e.Result, e.Detail,
            }.Select(Csv)));
        }

        try
        {
            // BOM UTF-8: o Excel abre os acentos corretamente.
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _status.Text = string.Format(Strings.AuditExported, dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, string.Format(Strings.SaveFailed, ex.Message), Strings.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Campo CSV (RFC 4180); prefixa <c>'</c> em fórmulas para não serem executadas pelo Excel.</summary>
    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
