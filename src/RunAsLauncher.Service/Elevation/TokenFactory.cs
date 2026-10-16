using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using RunAsLauncher.Identity;
using RunAsLauncher.Native;

namespace RunAsLauncher.Elevation;

/// <summary>
/// Cria, via logon S4U (sem senha), um token primário do próprio usuário com o grupo
/// <c>BUILTIN\Administrators</c> habilitado e integridade alta. Exige SeTcbPrivilege (LocalSystem).
/// A necessidade exata dos passos de "token vinculado" e ajuste de integridade é confirmada pelo
/// spike da Fase 1 (ver docs/Spike-Token.md).
/// </summary>
internal sealed class TokenFactory
{
    private const string Msv1_0PackageName = "MICROSOFT_AUTHENTICATION_PACKAGE_V1_0";
    private const string KerberosPackageName = "Kerberos";
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string HighIntegritySid = "S-1-16-12288";

    /// <summary>Resultado observável do spike, além do token em si.</summary>
    internal sealed record TokenReport(TOKEN_ELEVATION_TYPE ElevationType, bool UsedLinkedToken, bool ForcedIntegrity);

    internal SafeKernelHandle CreateElevatedUserToken(string user, string domain, AccountKind kind, uint sessionId, out TokenReport report)
    {
        if (kind == AccountKind.Unsupported)
        {
            throw new NotSupportedException("Tipo de conta não suportado para elevação (ex.: Entra ID).");
        }

        Privileges.Enable(Privileges.SeTcbPrivilege);

        using SafeLsaHandle lsa = RegisterLogonProcess();
        uint authPackage = LookupPackage(lsa, kind == AccountKind.Domain ? KerberosPackageName : Msv1_0PackageName);

        SafeKernelHandle s4uToken = LogonS4U(lsa, authPackage, user, domain);
        bool usedLinked = false;
        bool forcedIntegrity = false;

        TOKEN_ELEVATION_TYPE elevationType = GetElevationType(s4uToken);
        if (elevationType == TOKEN_ELEVATION_TYPE.Limited)
        {
            SafeKernelHandle linked = GetLinkedToken(s4uToken);
            s4uToken.Dispose();
            s4uToken = linked;
            usedLinked = true;
        }

        if (GetIntegritySid(s4uToken) != HighIntegritySid)
        {
            SetIntegrity(s4uToken, HighIntegritySid);
            forcedIntegrity = true;
        }

        SafeKernelHandle primary = DuplicateAsPrimary(s4uToken);
        s4uToken.Dispose();
        SetSessionId(primary, sessionId);

        report = new TokenReport(elevationType, usedLinked, forcedIntegrity);
        return primary;
    }

    private static SafeLsaHandle RegisterLogonProcess()
    {
        LSA_STRING name = AnsiLsaString("RunAsLauncher");
        int status = Secur32.LsaRegisterLogonProcess(ref name, out SafeLsaHandle lsa, out _);
        Marshal.FreeHGlobal(name.Buffer);
        ThrowOnNtStatus(status, "LsaRegisterLogonProcess");
        return lsa;
    }

    private static uint LookupPackage(SafeLsaHandle lsa, string packageName)
    {
        LSA_STRING name = AnsiLsaString(packageName);
        int status = Secur32.LsaLookupAuthenticationPackage(lsa, ref name, out uint package);
        Marshal.FreeHGlobal(name.Buffer);
        ThrowOnNtStatus(status, "LsaLookupAuthenticationPackage");
        return package;
    }

    private SafeKernelHandle LogonS4U(SafeLsaHandle lsa, uint authPackage, string user, string domain)
    {
        IntPtr authInfo = BuildS4ULogonBuffer(user, domain, out uint authInfoLength);
        IntPtr localGroups = BuildAdministratorsGroup(out SafeLocalAllocHandle adminSid);
        LSA_STRING origin = AnsiLsaString("RunAsLauncher");
        TOKEN_SOURCE source = BuildTokenSource();

        try
        {
            int status = Secur32.LsaLogonUser(
                lsa, ref origin, SECURITY_LOGON_TYPE.Batch, authPackage,
                authInfo, authInfoLength, localGroups, ref source,
                out SafeLsaBuffer profile, out _, out _, out SafeKernelHandle token,
                out _, out int subStatus);

            profile.Dispose();
            if (status != 0)
            {
                int win = Secur32.LsaNtStatusToWinError(status != 0 ? status : subStatus);
                throw new Win32Exception(win, $"LsaLogonUser (S4U) falhou. NTSTATUS=0x{status:X8} SubStatus=0x{subStatus:X8}");
            }

            return token;
        }
        finally
        {
            Marshal.FreeHGlobal(origin.Buffer);
            Marshal.FreeHGlobal(authInfo);
            Marshal.FreeHGlobal(localGroups);
            adminSid.Dispose();
        }
    }

    /// <summary>Monta o buffer contíguo do pedido S4U (struct + strings UTF-16).</summary>
    private static IntPtr BuildS4ULogonBuffer(string user, string domain, out uint totalLength)
    {
        int structSize = Marshal.SizeOf<S4U_LOGON>();
        byte[] userBytes = System.Text.Encoding.Unicode.GetBytes(user);
        byte[] domainBytes = System.Text.Encoding.Unicode.GetBytes(domain);

        int total = structSize + userBytes.Length + domainBytes.Length;
        IntPtr buffer = Marshal.AllocHGlobal(total);

        int userOffset = structSize;
        int domainOffset = structSize + userBytes.Length;
        Marshal.Copy(userBytes, 0, buffer + userOffset, userBytes.Length);
        Marshal.Copy(domainBytes, 0, buffer + domainOffset, domainBytes.Length);

        var logon = new S4U_LOGON
        {
            MessageType = (int)LogonSubmitType.MsV1_0S4ULogon, // 12 também para Kerberos
            Flags = 0,
            ClientUpn = new UNICODE_STRING
            {
                Length = (ushort)userBytes.Length,
                MaximumLength = (ushort)userBytes.Length,
                Buffer = buffer + userOffset,
            },
            ClientRealm = new UNICODE_STRING
            {
                Length = (ushort)domainBytes.Length,
                MaximumLength = (ushort)domainBytes.Length,
                Buffer = buffer + domainOffset,
            },
        };
        Marshal.StructureToPtr(logon, buffer, false);

        totalLength = (uint)total;
        return buffer;
    }

    private static IntPtr BuildAdministratorsGroup(out SafeLocalAllocHandle adminSid)
    {
        if (!Advapi32.ConvertStringSidToSidW(AdministratorsSid, out adminSid))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ConvertStringSidToSid (Administrators) falhou");
        }

        var groups = new TOKEN_GROUPS_ONE
        {
            GroupCount = 1,
            Group0 = new SID_AND_ATTRIBUTES
            {
                Sid = adminSid.DangerousGetHandle(),
                Attributes = Secur32.SE_GROUP_MANDATORY | Secur32.SE_GROUP_ENABLED_BY_DEFAULT | Secur32.SE_GROUP_ENABLED,
            },
        };

        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<TOKEN_GROUPS_ONE>());
        Marshal.StructureToPtr(groups, buffer, false);
        return buffer;
    }

    private static TOKEN_ELEVATION_TYPE GetElevationType(SafeKernelHandle token)
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!Advapi32.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevationType, buffer, sizeof(int), out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation(TokenElevationType) falhou");
            }

            return (TOKEN_ELEVATION_TYPE)Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SafeKernelHandle GetLinkedToken(SafeKernelHandle token)
    {
        IntPtr buffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (!Advapi32.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenLinkedToken, buffer, (uint)IntPtr.Size, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation(TokenLinkedToken) falhou");
            }

            return new SafeKernelHandle(Marshal.ReadIntPtr(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string GetIntegritySid(SafeKernelHandle token)
    {
        Advapi32.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, IntPtr.Zero, 0, out uint needed);
        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!Advapi32.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, needed, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation(TokenIntegrityLevel) falhou");
            }

            // TOKEN_MANDATORY_LABEL = SID_AND_ATTRIBUTES; o SID é o primeiro ponteiro.
            IntPtr sidPtr = Marshal.ReadIntPtr(buffer);
            var sid = new SecurityIdentifier(sidPtr);
            return sid.Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetIntegrity(SafeKernelHandle token, string integritySid)
    {
        if (!Advapi32.ConvertStringSidToSidW(integritySid, out SafeLocalAllocHandle sid))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ConvertStringSidToSid (integridade) falhou");
        }

        using (sid)
        {
            var label = new SID_AND_ATTRIBUTES { Sid = sid.DangerousGetHandle(), Attributes = 0x20 /* SE_GROUP_INTEGRITY */ };
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<SID_AND_ATTRIBUTES>());
            try
            {
                Marshal.StructureToPtr(label, buffer, false);
                if (!Advapi32.SetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, (uint)Marshal.SizeOf<SID_AND_ATTRIBUTES>()))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation(TokenIntegrityLevel) falhou");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static SafeKernelHandle DuplicateAsPrimary(SafeKernelHandle token)
    {
        if (!Advapi32.DuplicateTokenEx(
                token, Advapi32.TOKEN_ALL_ACCESS, IntPtr.Zero,
                SECURITY_IMPERSONATION_LEVEL.Impersonation, TOKEN_TYPE.Primary, out SafeKernelHandle primary))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx falhou");
        }

        return primary;
    }

    private static void SetSessionId(SafeKernelHandle token, uint sessionId)
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(buffer, (int)sessionId);
            if (!Advapi32.SetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenSessionId, buffer, sizeof(uint)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation(TokenSessionId) falhou");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static TOKEN_SOURCE BuildTokenSource()
    {
        var source = new TOKEN_SOURCE();
        byte[] name = System.Text.Encoding.ASCII.GetBytes("RunAsL\0\0");
        unsafe
        {
            for (int i = 0; i < 8 && i < name.Length; i++)
            {
                source.SourceName[i] = name[i];
            }
        }

        return source;
    }

    private static LSA_STRING AnsiLsaString(string value)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value);
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        return new LSA_STRING
        {
            Length = (ushort)bytes.Length,
            MaximumLength = (ushort)bytes.Length,
            Buffer = buffer,
        };
    }

    private static void ThrowOnNtStatus(int status, string api)
    {
        if (status != 0)
        {
            throw new Win32Exception(Secur32.LsaNtStatusToWinError(status), $"{api} falhou. NTSTATUS=0x{status:X8}");
        }
    }
}
