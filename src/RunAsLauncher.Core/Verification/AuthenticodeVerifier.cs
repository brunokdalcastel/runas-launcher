using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RunAsLauncher.Native;
using RunAsLauncher.Policies;

namespace RunAsLauncher.Verification;

/// <summary>Resultado da verificação Authenticode de um arquivo.</summary>
/// <param name="Valid">true se a assinatura é confiável segundo o WinVerifyTrust.</param>
/// <param name="Subject">Subject do certificado do signatário, se legível.</param>
/// <param name="Thumbprint">Thumbprint (SHA-1 hex maiúsculo) do certificado, se legível.</param>
public sealed record TrustResult(bool Valid, string? Subject, string? Thumbprint);

/// <summary>
/// Verificação de assinatura Authenticode via WinVerifyTrust. Revogação desligada por padrão
/// (uso offline/privado); o subject/thumbprint vêm do certificado embutido no arquivo.
/// </summary>
public static class AuthenticodeVerifier
{
    /// <summary>
    /// Verifica a assinatura de <paramref name="filePath"/>. Com <paramref name="checkRevocation"/>
    /// desligado (padrão), não consulta CRL/OCSP (amigável a ambientes sem internet).
    /// </summary>
    public static TrustResult Verify(string filePath, bool checkRevocation = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        IntPtr pFilePath = Marshal.StringToHGlobalUni(filePath);
        IntPtr pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        IntPtr pData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
        Guid action = Wintrust.WINTRUST_ACTION_GENERIC_VERIFY_V2;
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = pFilePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };
            Marshal.StructureToPtr(fileInfo, pFileInfo, fDeleteOld: false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = Wintrust.WTD_UI_NONE,
                fdwRevocationChecks = checkRevocation ? Wintrust.WTD_REVOKE_WHOLECHAIN : Wintrust.WTD_REVOKE_NONE,
                dwUnionChoice = Wintrust.WTD_CHOICE_FILE,
                pFile = pFileInfo,
                dwStateAction = Wintrust.WTD_STATEACTION_VERIFY,
                dwProvFlags = Wintrust.WTD_SAFER_FLAG | (checkRevocation ? 0u : Wintrust.WTD_CACHE_ONLY_URL_RETRIEVAL),
            };
            Marshal.StructureToPtr(data, pData, fDeleteOld: false);

            int status = Wintrust.WinVerifyTrust(Wintrust.INVALID_HANDLE_VALUE, ref action, pData);

            // Libera o estado da verificação (STATEACTION_CLOSE), independentemente do resultado.
            var back = Marshal.PtrToStructure<WINTRUST_DATA>(pData);
            back.dwStateAction = Wintrust.WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(back, pData, fDeleteOld: false);
            Wintrust.WinVerifyTrust(Wintrust.INVALID_HANDLE_VALUE, ref action, pData);

            bool valid = status == 0;
            var (subject, thumbprint) = ReadPublisher(filePath);
            return new TrustResult(valid, subject, thumbprint);
        }
        finally
        {
            Marshal.FreeHGlobal(pData);
            Marshal.FreeHGlobal(pFileInfo);
            Marshal.FreeHGlobal(pFilePath);
        }
    }

    /// <summary>true se o resultado é confiável e casa com a regra do administrador.</summary>
    public static bool Matches(PublisherRule rule, TrustResult result)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Valid || result.Subject is null)
        {
            return false;
        }

        if (result.Subject.IndexOf(rule.SubjectContains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.Thumbprint))
        {
            if (result.Thumbprint is null)
            {
                return false;
            }

            string actual = result.Thumbprint.Replace(" ", string.Empty);
            string expected = rule.Thumbprint.Replace(" ", string.Empty);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static (string? Subject, string? Thumbprint) ReadPublisher(string filePath)
    {
        try
        {
            // CreateFromSignedFile é a forma canônica de obter o certificado do signatário Authenticode
            // a partir do arquivo assinado; não há equivalente não-obsoleto (X509CertificateLoader carrega
            // bytes de certificado, não o signatário de um PE). Supressão restrita a esta chamada.
#pragma warning disable SYSLIB0057
            var cert = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            // GetCertHashString() (SHA-1) = thumbprint em hex maiúsculo, igual ao X509Certificate2.Thumbprint.
            return (cert.Subject, cert.GetCertHashString());
        }
        catch (CryptographicException)
        {
            return (null, null);
        }
    }
}
