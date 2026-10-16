using RunAsLauncher.Ipc;
using RunAsLauncher.Policies;

namespace RunAsLauncher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        return args switch
        {
            [] => RunWindow(),
            ["run", string id] => RunItem(id),
            ["--help" or "-h" or "/?"] => ShowUsage(MessageBoxIcon.Information, 0),
            _ => ShowUsage(MessageBoxIcon.Warning, 2),
        };
    }

    private static int RunWindow()
    {
        Application.Run(new MainForm(new BrokerClient()));
        return 0;
    }

    /// <summary><c>run &lt;id&gt;</c>: executa sem abrir janela; só mostra algo se der errado.</summary>
    private static int RunItem(string id)
    {
        if (!PolicyValidator.SlugRegex().IsMatch(id))
        {
            ShowError(Strings.ForResult(ResultCode.BadRequest));
            return 2;
        }

        try
        {
            // Sem SynchronizationContext ainda (nenhum formulário criado): bloquear aqui não causa deadlock.
            Response response = new BrokerClient().RunAsync(id).GetAwaiter().GetResult();
            if (response.Code == ResultCode.Ok)
            {
                return 0;
            }

            ShowError(Strings.Describe(response.Code, response.Message));
            return 1;
        }
        catch (BrokerException ex)
        {
            ShowError(ex.Message);
            return 1;
        }
    }

    private static int ShowUsage(MessageBoxIcon icon, int exitCode)
    {
        MessageBox.Show(Strings.Usage, Strings.AppName, MessageBoxButtons.OK, icon);
        return exitCode;
    }

    private static void ShowError(string text) =>
        MessageBox.Show(text, Strings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
