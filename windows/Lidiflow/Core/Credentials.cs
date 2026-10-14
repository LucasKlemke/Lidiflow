using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Lidiflow.Core;

/// API keys live in the Windows Credential Manager (the Keychain counterpart), never in settings.json.
public static class Credentials
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    public static string? Get(LlmProvider provider)
    {
        if (!CredRead(Target(provider), CredTypeGeneric, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var value = Encoding.Unicode.GetString(bytes);
            return value.Length > 0 ? value : null;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Set(LlmProvider provider, string? value)
    {
        var target = Target(provider);
        CredDelete(target, CredTypeGeneric, 0);
        if (string.IsNullOrEmpty(value)) return;

        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = provider.ToString().ToLowerInvariant(),
            };
            if (!CredWrite(ref credential, 0))
            {
                Log.Write($"CredWrite failed: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    private static string Target(LlmProvider provider) => "Lidiflow/" + provider.ToString().ToLowerInvariant();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
