using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodexRouter.Control;

public interface ICodexCredentialWriter
{
    Task SaveAgentIdentityAsync(
        string codexHome,
        CodexAgentIdentityRecord identity,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string codexHome, CancellationToken cancellationToken = default);
}

/// <summary>
/// Write-only adapter for the legacy direct Windows keyring backend used by OpenAI Codex.
/// Cleanup covers both that legacy entry and the current encrypted-secrets key entry.
/// It intentionally exposes no credential read API.
/// </summary>
public sealed class CodexDirectKeyringStore : ICodexCredentialWriter
{
    private const string KeyringService = "Codex Auth";
    private const string SecretsKeyringService = "codex";
    private const string KeyringVersionComment = "keyring v3.6.3";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistEnterprise = 3;
    private const int MaxCredentialBlobBytes = 5 * 512;

    public Task SaveAgentIdentityAsync(
        string codexHome,
        CodexAgentIdentityRecord identity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(identity);
        EnsureWindows();
        var accountName = ComputeAccountName(codexHome);
        var serialized = SerializeAgentIdentity(identity);
        WriteCredential(accountName, serialized);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string codexHome, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        DeleteCredential(TargetName(ComputeAccountName(codexHome)));
        DeleteCredential(SecretsTargetName(ComputeSecretsAccountName(codexHome)));
        return Task.CompletedTask;
    }

    private static void DeleteCredential(string targetName)
    {
        if (!CredDelete(targetName, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            const int ErrorNotFound = 1168;
            if (error != ErrorNotFound)
            {
                throw new InvalidOperationException($"Windows Credential Manager could not delete Codex credential material (Win32 {error}).");
            }
        }
    }

    internal static string SerializeAgentIdentity(CodexAgentIdentityRecord identity)
    {
        var agentIdentity = new Dictionary<string, object?>
        {
            ["agent_runtime_id"] = identity.AgentRuntimeId,
            ["agent_private_key"] = identity.AgentPrivateKey,
            ["account_id"] = identity.AccountId,
            ["chatgpt_user_id"] = identity.ChatGptUserId,
            ["email"] = identity.Email ?? string.Empty,
            ["plan_type"] = identity.PlanType,
            ["chatgpt_account_is_fedramp"] = identity.ChatGptAccountIsFedRamp
        };
        if (!string.IsNullOrWhiteSpace(identity.TaskId))
        {
            agentIdentity["task_id"] = identity.TaskId;
        }

        var auth = new Dictionary<string, object?>
        {
            ["auth_mode"] = "agentIdentity",
            ["OPENAI_API_KEY"] = null,
            ["agent_identity"] = agentIdentity
        };
        return JsonSerializer.Serialize(auth);
    }

    internal static string ComputeAccountName(string codexHome)
    {
        if (string.IsNullOrWhiteSpace(codexHome)) throw new ArgumentException("CODEX_HOME is required.", nameof(codexHome));
        EnsureWindows();
        var canonical = CanonicalizeWindowsDirectory(Path.GetFullPath(codexHome));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        return $"cli|{hex[..16]}";
    }

    internal static string TargetName(string accountName) => $"{accountName}.{KeyringService}";

    internal static string ComputeSecretsAccountName(string codexHome)
    {
        if (string.IsNullOrWhiteSpace(codexHome)) throw new ArgumentException("CODEX_HOME is required.", nameof(codexHome));
        EnsureWindows();
        var canonical = CanonicalizeWindowsDirectory(Path.GetFullPath(codexHome));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        return $"secrets|{hex[..16]}";
    }

    internal static string SecretsTargetName(string accountName) => $"{accountName}.{SecretsKeyringService}";

    private static void WriteCredential(string accountName, string serialized)
    {
        var blob = Encoding.Unicode.GetBytes(serialized);
        if (blob.Length > MaxCredentialBlobBytes)
        {
            CryptographicOperations.ZeroMemory(blob);
            throw new InvalidOperationException("Codex AgentIdentity payload exceeds the Windows Credential Manager size limit.");
        }

        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var credential = new Credential
            {
                Flags = 0,
                Type = CredTypeGeneric,
                TargetName = TargetName(accountName),
                Comment = KeyringVersionComment,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistEnterprise,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = null,
                UserName = accountName
            };
            if (!CredWrite(ref credential, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Windows Credential Manager could not store the Codex AgentIdentity (Win32 {error}).");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            if (blobPtr != IntPtr.Zero)
            {
                var zeros = new byte[Math.Max(1, blob.Length)];
                Marshal.Copy(zeros, 0, blobPtr, blob.Length);
                Marshal.FreeHGlobal(blobPtr);
            }
        }
    }

    private static string CanonicalizeWindowsDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        using var handle = CreateFile(
            path,
            0,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new InvalidOperationException($"Windows could not canonicalize CODEX_HOME (Win32 {Marshal.GetLastWin32Error()}).");
        }

        var capacity = 512;
        while (true)
        {
            var builder = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
            if (length == 0)
            {
                throw new InvalidOperationException($"Windows could not resolve CODEX_HOME (Win32 {Marshal.GetLastWin32Error()}).");
            }
            if (length < builder.Capacity)
            {
                return builder.ToString();
            }
            capacity = checked((int)length + 1);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Codex direct keyring storage is supported only on Windows.");
        }
    }

    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref Credential userCredential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] StringBuilder filePath,
        uint filePathSize,
        uint flags);
}
