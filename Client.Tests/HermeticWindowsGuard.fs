namespace Jarvis.Testing

open System
open System.IO
open System.Text
open System.Runtime.InteropServices
open System.Security.AccessControl
open System.Security.Principal
open Microsoft.Win32.SafeHandles

// Test-only Windows security boundary shared by the FSI parent and NUnit testhost.
// The runner must attest the real token profile and private scratch ACL BEFORE
// allocating any files; NUnit verifies the same claims independently.
module private WindowsNative =
    [<DllImport("kernel32.dll", ExactSpelling = true)>]
    extern nativeint GetCurrentProcess()
    [<DllImport("kernel32.dll", ExactSpelling = true)>]
    extern bool CloseHandle(nativeint handle)
    [<DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)>]
    extern bool OpenProcessToken(nativeint processHandle, uint32 desiredAccess, nativeint& token)
    [<DllImport("userenv.dll", EntryPoint = "GetUserProfileDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool GetUserProfileDirectory(nativeint token, StringBuilder buffer, uint32& length)
    [<DllImport("shell32.dll", ExactSpelling = true)>]
    extern int SHGetKnownFolderPath(Guid& folderId, uint32 flags, nativeint token, nativeint& path)
    [<DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern SafeFileHandle OpenExistingDirectory(string path, uint32 desiredAccess, uint32 shareMode,
                                                nativeint security, uint32 disposition,
                                                uint32 flags, nativeint templateHandle)
    [<DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern uint32 GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder buffer, uint32 capacity, uint32 flags)

[<RequireQualifiedAccess>]
module WindowsDisposableGuard =
    let private deny message = raise (UnauthorizedAccessException message)

    let private sameOrUnder (root: string) (path: string) =
        let root = Path.TrimEndingDirectorySeparator(root)
        let path = Path.TrimEndingDirectorySeparator(path)
        String.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)

    /// Pure negative gate; deliberately does not probe or create any path.
    /// Reject both an enclosing profile and a parent that encloses a profile.
    let assertOutsideProtectedFolders (candidate: string) (protectedPaths: string list) =
        if List.isEmpty protectedPaths || String.IsNullOrWhiteSpace candidate then
            deny "Cannot attest a Windows disposable directory against an OS profile."
        for protectedPath in protectedPaths do
            if String.IsNullOrWhiteSpace protectedPath ||
               sameOrUnder protectedPath candidate ||
               sameOrUnder candidate protectedPath then
                deny "Windows disposable directory overlaps a protected OS profile."

    let private tokenProfileDirectory () =
        let mutable token = IntPtr.Zero
        if not (WindowsNative.OpenProcessToken(WindowsNative.GetCurrentProcess(), 0x8u, &token)) ||
           token = IntPtr.Zero then
            deny "Cannot attest the Windows process token."
        try
            let mutable capacity = 0u
            WindowsNative.GetUserProfileDirectory(token, null, &capacity) |> ignore
            if capacity = 0u || capacity > 32768u then
                deny "Cannot size native Windows token-profile path."
            let buffer = StringBuilder(int capacity)
            if not (WindowsNative.GetUserProfileDirectory(token, buffer, &capacity)) then
                deny "Cannot read native Windows token-profile path."
            let profile = buffer.ToString()
            if String.IsNullOrWhiteSpace profile || not (Path.IsPathFullyQualified profile) then
                deny "Native Windows token profile is unavailable."
            profile
        finally
            WindowsNative.CloseHandle(token) |> ignore

    let private knownFolder (guid: string) =
        let mutable folder = Guid(guid)
        let mutable pointer = IntPtr.Zero
        // KF_FLAG_DONT_VERIFY avoids triggering folder creation during identity attestation.
        let result = WindowsNative.SHGetKnownFolderPath(&folder, 0x4000u, IntPtr.Zero, &pointer)
        if result <> 0 || pointer = IntPtr.Zero then
            deny "Cannot attest native Windows known-folder location."
        try
            let path = Marshal.PtrToStringUni pointer
            if String.IsNullOrWhiteSpace path || not (Path.IsPathFullyQualified path) then
                deny "Native Windows known-folder location is unavailable."
            path
        finally
            Marshal.FreeCoTaskMem pointer

    /// Do not follow a junction, symlink, or volume mount on any path component.
    let assertNoLinkedAncestors (path: string) =
        let mutable current = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)
        let mutable remaining = true
        while remaining do
            let attributes = File.GetAttributes current
            if attributes.HasFlag FileAttributes.ReparsePoint ||
               not (isNull (DirectoryInfo(current).LinkTarget)) then
                deny "Windows disposable directory uses a junction or reparse-point ancestor."
            let parent = Path.GetDirectoryName current
            if String.IsNullOrEmpty parent || parent = current then remaining <- false
            else current <- parent

    // Final resolved names with VOLUME_NAME_GUID defeat short-name, DOS-device
    // and alternate drive-letter aliases that lexical Path.GetFullPath misses.
    let private physicalDirectory (path: string) =
        if not (Directory.Exists path) then
            deny "Windows profile or disposable directory is not accessible."
        assertNoLinkedAncestors path
        use handle =
            WindowsNative.OpenExistingDirectory(path, 0u, 0x7u, IntPtr.Zero, 3u,
                                                0x02000000u ||| 0x00200000u, IntPtr.Zero)
        if isNull handle || handle.IsInvalid then
            deny "Cannot obtain physical Windows directory identity."
        let result = StringBuilder(32768)
        let chars = WindowsNative.GetFinalPathNameByHandle(handle, result, uint32 result.Capacity, 0x1u)
        if chars = 0u || chars >= uint32 result.Capacity then
            deny "Cannot resolve physical Windows directory identity."
        let full = result.ToString()
        if not (full.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)) then
            deny "Windows directory has no stable local volume identity."
        Path.TrimEndingDirectorySeparator full

    /// Token-derived OS profile exclusion remains reliable even when the
    /// testhost intentionally remaps USERPROFILE/APPDATA to disposable folders.
    /// Perform this second independent attestation at NUnit startup.
    let attestScratchParentTokenOnly (path: string) =
        if not (OperatingSystem.IsWindows()) then
            deny "Native Windows profile attestation requires Windows."
        if String.IsNullOrWhiteSpace path || not (Path.IsPathFullyQualified path) then
            deny "Windows scratch parent must be an absolute directory."
        let full = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)
        let candidate = physicalDirectory full
        let realProfile = tokenProfileDirectory()
        let token = physicalDirectory realProfile
        assertOutsideProtectedFolders candidate [token]
        assertOutsideProtectedFolders full [realProfile]
        full

    /// Parent FSI process only: BEFORE changing environment or allocating any
    /// scratch, additionally attest real roaming/local known folders. Repeating
    /// those queries in testhost could read synthetic, nonexistent mappings.
    let attestScratchParent (path: string) =
        let full = attestScratchParentTokenOnly path
        let candidate = physicalDirectory full
        let roamingPath = knownFolder "3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"
        let localPath = knownFolder "F1B32785-6FBA-4FCF-9D55-7B8E7F157091"
        assertOutsideProtectedFolders candidate
            [ physicalDirectory roamingPath; physicalDirectory localPath ]
        assertOutsideProtectedFolders full [roamingPath; localPath]
        full

    let private currentUser () =
        if not (OperatingSystem.IsWindows()) then deny "Windows ACL enforcement is unavailable."
        use identity = WindowsIdentity.GetCurrent()
        if isNull identity || isNull identity.User then
            deny "Cannot attest current Windows user SID."
        identity.User

    /// Verify owner, non-null allow ACL and absence of permissions for any
    /// other principal (including inherited broad Users/Everyone ACEs).
    let assertOwnerOnly (path: string) (isDirectory: bool) (requireProtected: bool) =
        if not (OperatingSystem.IsWindows()) then deny "Windows ACL verification is unavailable."
        if File.GetAttributes(path).HasFlag FileAttributes.ReparsePoint then
            deny "Refusing reparse-point Windows scratch ACL."
        let security : FileSystemSecurity =
            if isDirectory then
                FileSystemAclExtensions.GetAccessControl(
                    DirectoryInfo(path), AccessControlSections.Owner ||| AccessControlSections.Access)
                :> FileSystemSecurity
            else
                FileSystemAclExtensions.GetAccessControl(
                    FileInfo(path), AccessControlSections.Owner ||| AccessControlSections.Access)
                :> FileSystemSecurity
        let user = currentUser()
        let owner = security.GetOwner(typeof<SecurityIdentifier>) :?> SecurityIdentifier
        if isNull owner || not (owner.Equals user) then
            deny "Windows scratch owner is not the actual process SID."
        if requireProtected && not security.AreAccessRulesProtected then
            deny "Windows scratch root inherited an untrusted parent ACL."
        let rules =
            security.GetAccessRules(true, true, typeof<SecurityIdentifier>)
            |> Seq.cast<FileSystemAccessRule>
            |> Seq.toList
        if List.isEmpty rules ||
           rules |> List.exists (fun rule ->
               rule.AccessControlType <> AccessControlType.Allow ||
               not (rule.IdentityReference.Equals user)) then
            deny "Windows scratch DACL grants non-owner or unknown access."
        if not (rules |> List.exists (fun rule ->
            (rule.FileSystemRights &&& FileSystemRights.FullControl) = FileSystemRights.FullControl)) then
            deny "Windows scratch owner does not have full control."

    /// Atomic security descriptor at creation time: no moment with inherited
    /// world-readable access before a post-creation ACL rewrite.
    let createPrivateRoot (path: string) (injectAfterCreate: bool) =
        if File.Exists path || Directory.Exists path then
            deny "Disposable Windows scratch root name is already occupied."
        let user = currentUser()
        let security = DirectorySecurity()
        security.SetOwner user
        security.SetAccessRuleProtection(true, false)
        security.AddAccessRule(
            FileSystemAccessRule(user, FileSystemRights.FullControl,
                                 InheritanceFlags.ContainerInherit ||| InheritanceFlags.ObjectInherit,
                                 PropagationFlags.None, AccessControlType.Allow))
        let created = FileSystemAclExtensions.CreateDirectory(security, path)
        // Exercise parent acquisition cleanup immediately after OS allocation,
        // before a post-create attestation failure could leave a private root.
        if injectAfterCreate then
            failwith "Hermetic root-creation failure injected before ACL verification."
        assertOwnerOnly created.FullName true true
        created
