using System.Text;
using RunAsLauncher.Policies;

namespace RunAsLauncher;

/// <summary>
/// Atalhos e linha de comando para executar um item sem abrir a janela: <c>"RunAsLauncher.exe" run &lt;id&gt;</c>.
/// O atalho aponta para o launcher (nunca para o alvo), então continua passando pelo serviço.
/// </summary>
internal static class ShortcutWriter
{
    private const int MaxFileNameLength = 100;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Caminho do executável do launcher em execução.</summary>
    public static string LauncherPath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("Caminho do executável indisponível.");

    /// <summary>Comando para "Copiar comando": <c>"&lt;exe&gt;" run &lt;id&gt;</c>.</summary>
    public static string CommandLine(string id) => CommandLine(LauncherPath, id);

    public static string CommandLine(string exePath, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        EnsureValidId(id);
        return $"\"{exePath}\" run {id}";
    }

    /// <summary>
    /// Nome de arquivo seguro a partir do título: troca <c>\ / : * ? " &lt; &gt; |</c> e caracteres de controle por
    /// <c>_</c>, remove espaços/pontos das pontas, evita nomes reservados do Windows e limita o tamanho.
    /// </summary>
    public static string SanitizeFileName(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (char c in title)
        {
            sb.Append(c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '_' : c);
        }

        string name = sb.ToString().Trim(' ', '.');
        if (name.Length > MaxFileNameLength)
        {
            name = name[..MaxFileNameLength].TrimEnd(' ', '.');
        }

        if (name.Length == 0)
        {
            return Strings.AppName;
        }

        // "CON", "con.txt" etc. são reservados mesmo com extensão.
        string stem = name.Split('.')[0].TrimEnd(' ');
        return ReservedNames.Contains(stem) ? "_" + name : name;
    }

    /// <summary>Cria (ou substitui) o atalho na área de trabalho do usuário e devolve o caminho do .lnk.</summary>
    public static string CreateOnDesktop(string id, string title, string iconSource)
    {
        EnsureValidId(id);
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string lnkPath = Path.Combine(desktop, SanitizeFileName(title) + ".lnk");

        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                         ?? throw new InvalidOperationException("WScript.Shell indisponível.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            try
            {
                shortcut.TargetPath = LauncherPath;
                shortcut.Arguments = $"run {id}";
                shortcut.WorkingDirectory = Path.GetDirectoryName(LauncherPath);
                shortcut.IconLocation = iconSource + ",0";
                shortcut.Description = title;
                shortcut.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }

        return lnkPath;
    }

    // O id vem do serviço, mas vira argumento de linha de comando: só aceita o slug da política.
    private static void EnsureValidId(string id)
    {
        if (id is null || !PolicyValidator.SlugRegex().IsMatch(id))
        {
            throw new ArgumentException("Id de item inválido.", nameof(id));
        }
    }
}
