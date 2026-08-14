using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Cairn.Core.Games;

/// <summary>
/// Checks that the Vintage Story Windows installer is the vendor's before Cairn runs it.
///
/// Cairn downloads an <c>.exe</c> named by a JSON document and executes it silently, with
/// no UAC prompt because the install is per-user. Everything upstream of that binds the
/// artifact to the catalogue and not to the vendor: the MD5 comes out of the same document
/// as the URL, so whoever can rewrite one rewrites the other, and the host allowlist in
/// <see cref="GameCatalog"/> bounds where the bytes come from without saying anything about
/// who made them. Authenticode is the only check here whose answer does not come from the
/// catalogue at all.
///
/// Two questions, and both have to be asked. <see cref="WinVerifyTrust"/> answers "is this
/// signature valid and does it chain to a root this machine trusts" — without it, an
/// attacker embeds any certificate they like and a naive read of the subject believes it.
/// The signer pin answers "and is it *theirs*" — without it, any code-signing certificate
/// in the world passes, which is a low bar to clear.
///
/// Observed on the shipped installer: an SSL.com EV code-signing certificate issued to
/// <c>Anego Studios SIA</c> (Latvia), chaining through "SSL.com EV Code Signing
/// Intermediate CA RSA R3" to "SSL.com EV Root Certification Authority RSA R2". The
/// organisation is pinned rather than a thumbprint, because EV certificates are reissued
/// every year or two and pinning one would break every install the day it rotated; the
/// organisation name is the part that stays put.
///
/// Failing is a version that cannot be installed, with a reason worth printing. That is
/// the right direction for the last check standing between a downloaded executable and
/// Process.Start — but it does mean that if Anego Studios ever re-registers under another
/// name, this constant is what has to change.
///
/// <para><b>Embedded signatures only.</b> Passing WINTRUST_FILE_INFO asks about the
/// signature inside the file and nothing else, so a file signed through a security catalog
/// — which is how most of Windows' own binaries are signed — comes back as unsigned. That
/// is not a gap here: a freshly downloaded installer is in no catalog on the machine, and
/// the Vintage Story installer carries a real embedded signature. It does mean the obvious
/// smoke test misleads. Measured on Windows 11: <c>notepad.exe</c> returns
/// TRUST_E_NOSIGNATURE from this while <c>Get-AuthenticodeSignature</c> calls it Valid,
/// because it is catalog-signed; <c>vdagent.exe</c> (Red Hat, embedded) returns success and
/// is then refused on the signer pin, which is the case that actually exercises this.</para>
/// </summary>
public static class WindowsCodeSignature
{
    /// <summary>
    /// The subject common name Vintage Story installers are signed with. See the class
    /// summary for why this is a name rather than a thumbprint.
    /// </summary>
    public const string ExpectedSigner = "Anego Studios SIA";

    /// <summary>
    /// Whether a signer's common name is the one Cairn expects.
    ///
    /// Split out as a plain string comparison so it can be asserted on from any platform —
    /// the rest of this file needs Windows to run at all, which would otherwise leave the
    /// one decision that is actually a policy untested on the machines most of this is
    /// developed on.
    /// </summary>
    public static bool IsExpectedSigner(string? commonName) =>
        !string.IsNullOrWhiteSpace(commonName)
        && string.Equals(commonName.Trim(), ExpectedSigner, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why this file may not be run, or null when it is properly signed by the vendor.
    ///
    /// A no-op off Windows, where there is neither a WinVerifyTrust to call nor a path
    /// that reaches it: <see cref="GameInstaller"/> refuses a Windows installer on any
    /// other platform well before this.
    /// </summary>
    public static string? Problem(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;

        if (TrustProblem(path) is { } untrusted) return untrusted;

        string? signer;
        try
        {
            // SYSLIB0057 points at X509CertificateLoader, which loads a certificate out of
            // a blob and has no equivalent for "the signer of this PE file's Authenticode
            // signature". Reaching that any other way means CryptQueryObject and a good
            // deal more interop for the same answer, so the obsolete call stays and is
            // suppressed here rather than left as noise in a security-critical file.
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            signer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException)
        {
            // WinVerifyTrust already said the signature was good, so this is a malformed
            // or unreadable certificate rather than an unsigned file. Refused all the
            // same: a signature nothing can name the signer of is not one to act on.
            return Lang.Get("sign-unreadable", e.Message);
        }

        return IsExpectedSigner(signer)
            ? null
            : Lang.Get("sign-wrong-signer", signer, ExpectedSigner);
    }

    /// <summary>
    /// Throws unless the file carries a valid signature from the expected signer.
    /// </summary>
    public static void Require(string path)
    {
        if (Problem(path) is not { } problem) return;

        throw new GameInstallException(Lang.Get(
            "install-refusing-unsigned", Path.GetFileName(path), problem));
    }

    // ---- WinVerifyTrust ----

    // {00AAC56B-CD44-11d0-8CC2-00C04FC295EE} — "is this file's Authenticode signature
    // valid, and does it chain to a trusted root", which is the whole question.
    private static readonly Guid GenericVerifyV2 = new("00aac56b-cd44-11d0-8cc2-00c04fc295ee");

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint SaferFlag = 0x100;

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int TrustEExplicitDistrust = unchecked((int)0x800B0111);
    private const int TrustESubjectNotTrusted = unchecked((int)0x800B0004);
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEExpired = unchecked((int)0x800B0101);
    private const int CertEChaining = unchecked((int)0x800B010A);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? TrustProblem(string path)
    {
        var pathPtr = IntPtr.Zero;
        var fileInfoPtr = IntPtr.Zero;

        try
        {
            pathPtr = Marshal.StringToHGlobalUni(path);

            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPtr,
                FileHandle = IntPtr.Zero,
                KnownSubject = IntPtr.Zero,
            };

            fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UIChoice = UiNone,

                // No revocation check. It would put a network round trip on the install
                // path — which has to work behind a proxy and on a machine that has just
                // been handed a game to download — and the control here is the signer pin
                // rather than the certificate's current standing: a forgery still has to
                // be signed by Anego Studios' own key, revoked or not.
                RevocationChecks = RevokeNone,

                UnionChoice = ChoiceFile,
                FileInfoPtr = fileInfoPtr,
                StateAction = StateActionVerify,
                ProvFlags = SaferFlag,
            };

            // A local copy because the call takes it by ref and a static readonly field
            // cannot be passed that way.
            var action = GenericVerifyV2;

            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            // The verify call allocates provider state that only a second call with
            // STATEACTION_CLOSE releases, so this runs whatever the answer was.
            data.StateAction = StateActionClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return result == 0 ? null : Describe(result);
        }
        catch (DllNotFoundException)
        {
            // wintrust.dll is a Windows component and its absence is not a normal state.
            // Refused rather than waved through: "the check could not run" must not mean
            // "the check passed" for the one thing standing in front of Process.Start.
            return Lang.Get("sign-no-wintrust");
        }
        catch (EntryPointNotFoundException)
        {
            return Lang.Get("sign-no-winverifytrust");
        }
        finally
        {
            if (fileInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPtr);
            if (pathPtr != IntPtr.Zero) Marshal.FreeHGlobal(pathPtr);
        }
    }

    /// <summary>
    /// What a rejection means, with the raw code kept on every message.
    ///
    /// The code is not decoration. Windows conflates cases this wording would otherwise
    /// state too confidently — see TRUST_E_NOSIGNATURE below — and a refusal that stops a
    /// game installing needs to be reportable by whoever hits it, not just readable.
    /// </summary>
    private static string Describe(int hresult) => hresult switch
    {
        // Both "unsigned" and "altered after signing" arrive here. Verified on Windows 11
        // against the shipped installer: flipping one byte a megabyte into
        // vs_install_win-x64_1.22.6.exe yields 0x800B0100, not TRUST_E_BAD_DIGEST, even
        // though Get-AuthenticodeSignature calls the same file HashMismatch — it reaches
        // the answer by a different route. Saying only "unsigned" would send somebody
        // looking for a vendor who had stopped signing when what they have is a broken
        // download, so this says both.
        TrustENoSignature =>
            Lang.Get("sign-no-signature", $"0x{hresult:X8}"),

        // Kept although the case above is what a tampered file actually produces through
        // this provider: other callers and other providers do return it.
        TrustEBadDigest =>
            Lang.Get("sign-altered", $"0x{hresult:X8}"),

        TrustEExplicitDistrust =>
            Lang.Get("sign-distrusted", $"0x{hresult:X8}"),
        TrustESubjectNotTrusted =>
            Lang.Get("sign-untrusted", $"0x{hresult:X8}"),
        CertEUntrustedRoot =>
            Lang.Get("sign-no-chain", $"0x{hresult:X8}"),
        CertEExpired => Lang.Get("sign-expired", $"0x{hresult:X8}"),
        CertEChaining => Lang.Get("sign-chain-failed", $"0x{hresult:X8}"),

        _ => Lang.Get("sign-rejected", $"0x{hresult:X8}"),
    };

    // DllImport rather than LibraryImport: the source generator behind the latter emits
    // unsafe code, and turning AllowUnsafeBlocks on for the whole of Cairn.Core to reach
    // one p/invoke is a poor trade. Nothing here is hot enough for the difference to
    // matter — it runs once per game install.
    [DllImport("wintrust.dll", EntryPoint = "WinVerifyTrust", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPtr;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;
    }
}
