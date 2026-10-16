using System.Globalization;

namespace RunAsLauncher.Localization;

/// <summary>Idiomas da interface (F11).</summary>
public enum UiLanguage
{
    Portuguese,
    English,
}

/// <summary>
/// Infraestrutura comum de textos visíveis (padrão Fragmo <c>T(pt, en)</c>), usada pelo launcher e pelo console.
/// O idioma segue o Windows: português se a interface for pt-*, inglês nos demais casos.
/// </summary>
public static class Text
{
    public static UiLanguage Language { get; set; } = Detect(CultureInfo.CurrentUICulture);

    public static bool Portuguese => Language == UiLanguage.Portuguese;

    public static UiLanguage Detect(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "pt" ? UiLanguage.Portuguese : UiLanguage.English;

    /// <summary>Escolhe o texto do idioma atual.</summary>
    public static string T(string pt, string en) => Portuguese ? pt : en;
}
