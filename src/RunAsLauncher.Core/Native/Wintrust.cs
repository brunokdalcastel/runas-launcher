using System.Runtime.InteropServices;

namespace RunAsLauncher.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct WINTRUST_FILE_INFO
{
    public uint cbStruct;
    public IntPtr pcwszFilePath;
    public IntPtr hFile;
    public IntPtr pgKnownSubject;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINTRUST_DATA
{
    public uint cbStruct;
    public IntPtr pPolicyCallbackData;
    public IntPtr pSIPClientData;
    public uint dwUIChoice;
    public uint fdwRevocationChecks;
    public uint dwUnionChoice;
    public IntPtr pFile; // união; só usamos a variante WTD_CHOICE_FILE
    public uint dwStateAction;
    public IntPtr hWVTStateData;
    public IntPtr pwszURLReference;
    public uint dwProvFlags;
    public uint dwUIContext;
    public IntPtr pSignatureSettings;
}

internal static partial class Wintrust
{
    public static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public const uint WTD_UI_NONE = 2;
    public const uint WTD_REVOKE_NONE = 0;
    public const uint WTD_REVOKE_WHOLECHAIN = 1;
    public const uint WTD_CHOICE_FILE = 1;
    public const uint WTD_STATEACTION_VERIFY = 1;
    public const uint WTD_STATEACTION_CLOSE = 2;
    public const uint WTD_SAFER_FLAG = 0x100;
    public const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    // hwnd = INVALID_HANDLE_VALUE → nunca abre UI.
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [LibraryImport("wintrust.dll")]
    public static partial int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr data);
}
