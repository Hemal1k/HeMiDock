using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace HeMiDock;

/// <summary>
/// The digital signature (Authenticode) of a downloaded program: checked by Windows (WinVerifyTrust, the same check as the
/// properties of a file show) and its signer must be Microsoft – before a runtime downloaded from aka.ms is started.
/// Nothing of WinUI is used, it runs before the Windows App Runtime is there.
/// </summary>
static class Authenticode
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
    // revocation lists are not asked for: a proxy or a slow network then failed the check of a good file
    const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;

    [DllImport("wintrust.dll")] static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);
    [DllImport("wintrust.dll")] static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signer, bool counterSigner, uint counterSignerIndex);
    [DllImport("wintrust.dll")] static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint cert);

    /// <summary>
    /// The subject of the certificate that signed the file, when Windows trusts the signature (null otherwise) – read from
    /// the same check, so it is the signer of exactly the file that was checked.
    /// </summary>
    public static string TrustedSigner(string file)
    {
        var info = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        IntPtr pInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(info, pInfo, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = WTD_UI_NONE, fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE, pFile = pInfo, dwStateAction = WTD_STATEACTION_VERIFY
            };
            var action = GenericVerifyV2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            string subject = null;
            try
            {
                // the first signer and the first certificate of its chain (the signer's own); CRYPT_PROVIDER_CERT starts
                // with its size, its CERT_CONTEXT follows at the size of a pointer
                IntPtr prov = result == 0 ? WTHelperProvDataFromStateData(data.hWVTStateData) : IntPtr.Zero;
                IntPtr signer = prov != IntPtr.Zero ? WTHelperGetProvSignerFromChain(prov, 0, false, 0) : IntPtr.Zero;
                IntPtr cert = signer != IntPtr.Zero ? WTHelperGetProvCertFromChain(signer, 0) : IntPtr.Zero;
                IntPtr context = cert != IntPtr.Zero ? Marshal.ReadIntPtr(cert, IntPtr.Size) : IntPtr.Zero;
                if (context != IntPtr.Zero)
                    using (var x = new X509Certificate2(context)) subject = x.Subject;   // a copy of its own
            }
            finally
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;                  // frees what the check kept
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
            return subject;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pInfo);
            Marshal.FreeHGlobal(pInfo);
        }
    }

    /// <summary>Signed validly, and by Microsoft (the organisation in the certificate of the signer).</summary>
    public static bool IsMicrosoft(string file)
    {
        try { return TrustedSigner(file)?.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }
}
