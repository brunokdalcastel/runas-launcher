using System.Runtime.InteropServices;

namespace RunAsLauncher.Native;

/// <summary>
/// Layout comum do pedido de logon S4U para MSV1_0 (<c>MSV1_0_S4U_LOGON</c>) e Kerberos
/// (<c>KERB_S4U_LOGON</c>): ambos são MessageType + Flags + duas UNICODE_STRING (nome, domínio/realm).
/// Os buffers das strings precisam ficar contíguos à struct.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct S4U_LOGON
{
    public int MessageType;
    public uint Flags;
    public UNICODE_STRING ClientUpn;    // MSV1_0: SamAccountName; Kerberos: UPN
    public UNICODE_STRING ClientRealm;  // MSV1_0: nome do computador/domínio; Kerberos: realm
}
