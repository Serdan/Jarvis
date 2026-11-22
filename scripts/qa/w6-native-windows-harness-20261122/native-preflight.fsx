// Read-only native Windows preflight. No directory or file creation in this program.
// Invocation MUST precede source extraction, restore, build, or testhost startup.
// Never replace OS-token attestations with USERPROFILE/TEMP overrides.
#r "System.Security.Principal.Windows"
#r "System.IO.FileSystem.AccessControl"
#r "System.Security.AccessControl"
open System
open System.IO
open System.Text
open System.Runtime.InteropServices
open System.Security.Principal
open System.Security.AccessControl
open Microsoft.Win32.SafeHandles

module Native =
    [<DllImport("kernel32.dll", ExactSpelling=true)>]
    extern nativeint GetCurrentProcess()
    [<DllImport("kernel32.dll", ExactSpelling=true)>]
    extern bool CloseHandle(nativeint handle)
    [<DllImport("advapi32.dll", ExactSpelling=true, SetLastError=true)>]
    extern bool OpenProcessToken(nativeint processHandle, uint32 access, nativeint& token)
    [<DllImport("userenv.dll", EntryPoint="GetUserProfileDirectoryW", CharSet=CharSet.Unicode, SetLastError=true)>]
    extern bool GetUserProfileDirectory(nativeint token, StringBuilder buffer, uint32& capacity)
    [<DllImport("shell32.dll", ExactSpelling=true)>]
    extern int SHGetKnownFolderPath(Guid& id, uint32 flags, nativeint token, nativeint& path)
    [<DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)>]
    extern SafeFileHandle OpenDirectory(string path, uint32 access, uint32 share,
                                        nativeint security, uint32 disposition, uint32 flags,
                                        nativeint templateHandle)
    [<DllImport("kernel32.dll", EntryPoint="GetFinalPathNameByHandleW", CharSet=CharSet.Unicode, SetLastError=true)>]
    extern uint32 FinalName(SafeFileHandle handle, StringBuilder output, uint32 capacity, uint32 flags)

let deny message = raise (UnauthorizedAccessException("W6_NATIVE_HOLD: " + message))
let args = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--")
let value key =
    match args |> Array.tryFindIndex ((=) key) with
    | Some i when i + 1 < args.Length -> args.[i + 1]
    | _ -> deny ("Required native preflight argument missing: " + key)
let phase = value "--phase"
if phase <> "parent" && phase <> "testhost" then deny "Unknown attestation phase."
if not (OperatingSystem.IsWindows()) then deny "Windows native kernel/token APIs unavailable."
if not Environment.Is64BitProcess then deny "64-bit native Windows preflight required."
let identity = WindowsIdentity.GetCurrent()
if isNull identity || isNull identity.User then deny "No Windows token SID."
let sid = identity.User.Value
let requiredSid = value "--dedicated-sid"
let operatorSid = value "--operator-sid"
let validSid (s:string) =
    try SecurityIdentifier(s) |> ignore; true with _ -> false
if not (validSid requiredSid && validSid operatorSid) then deny "Invalid SID evidence."
if not (String.Equals(sid, requiredSid, StringComparison.Ordinal)) then
    deny "Current native SID differs from independently recorded dedicated token."
if String.Equals(sid, operatorSid, StringComparison.Ordinal) then
    deny "Operator and test token share the same OS identity."
let principal = WindowsPrincipal(identity)
if principal.IsInRole(WindowsBuiltInRole.Administrator) then
    deny "Disposable test token has administrative privileges."
let parentPath = value "--scratch-parent"
if not (Path.IsPathFullyQualified parentPath) then deny "Scratch parent must be absolute."
let full = Path.TrimEndingDirectorySeparator(Path.GetFullPath parentPath)
if not (Directory.Exists full) then deny "Scratch parent must preexist."

let tokenProfile () =
    let mutable token = IntPtr.Zero
    if not (Native.OpenProcessToken(Native.GetCurrentProcess(), 0x8u, &token)) || token = IntPtr.Zero then
        deny "OpenProcessToken failed."
    try
        let mutable chars = 0u
        Native.GetUserProfileDirectory(token, null, &chars) |> ignore
        if chars = 0u || chars > 32768u then deny "GetUserProfileDirectoryW length unavailable."
        let buffer = StringBuilder(int chars)
        if not (Native.GetUserProfileDirectory(token, buffer, &chars)) then
            deny "GetUserProfileDirectoryW failed."
        buffer.ToString()
    finally
        if not (Native.CloseHandle token) then deny "Token close failed."

let nativeKnown (name: string, guidText: string) =
    let mutable guid = Guid(guidText)
    let mutable pointer = IntPtr.Zero
    // KF_FLAG_DONT_VERIFY: do not create shell folders during the preflight.
    let result = Native.SHGetKnownFolderPath(&guid, 0x4000u, IntPtr.Zero, &pointer)
    if result <> 0 || pointer = IntPtr.Zero then deny ("KnownFolder missing: " + name)
    try
        let path = Marshal.PtrToStringUni pointer
        if String.IsNullOrWhiteSpace path then deny ("KnownFolder empty: " + name)
        name, path
    finally
        Marshal.FreeCoTaskMem pointer

let noLinkedAncestors (path: string) =
    let mutable cursor = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)
    let mutable doneWalk = false
    while not doneWalk do
        let info = DirectoryInfo(cursor)
        if not info.Exists then deny "Unresolvable path ancestor."
        if info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
           not (isNull info.LinkTarget) then deny "Linked/junction/reparse ancestor."
        let ancestor = Path.GetDirectoryName(cursor)
        if String.IsNullOrEmpty ancestor || ancestor = cursor then doneWalk <- true
        else cursor <- ancestor

let physical (path:string) =
    if not (Path.IsPathFullyQualified path) || not (Directory.Exists path) then
        deny "Missing/nonabsolute native profile/known folder/scratch directory."
    noLinkedAncestors path
    use h = Native.OpenDirectory(path, 0u, 0x7u, IntPtr.Zero, 3u,
                                 0x02000000u ||| 0x00200000u, IntPtr.Zero)
    if isNull h || h.IsInvalid then deny "Cannot open physical directory handle."
    let b = StringBuilder(32768)
    let result = Native.FinalName(h, b, uint32 b.Capacity, 0x1u)
    if result = 0u || result >= uint32 b.Capacity then deny "Physical path resolution failed."
    let output = Path.TrimEndingDirectorySeparator(b.ToString())
    if not (output.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)) then
        deny "Directory lacks GUID-volume physical identity."
    output

let sameOrUnder (parent:string) (candidate:string) =
    let root = Path.TrimEndingDirectorySeparator parent
    let child = Path.TrimEndingDirectorySeparator candidate
    String.Equals(root, child, StringComparison.OrdinalIgnoreCase) ||
    child.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)

let activeProfile = tokenProfile()
let paths = [
    "Profile", activeProfile
    nativeKnown ("Desktop", "B4BFCC3A-DB2C-424C-B029-7FE99A87C641")
    nativeKnown ("Documents", "FDD39AD0-238F-46AF-ADB4-6C85480369C7")
    nativeKnown ("RoamingAppData", "3EB685DB-65F9-4CF6-A03A-E3EF65729F3D")
    nativeKnown ("LocalAppData", "F1B32785-6FBA-4FCF-9D55-7B8E7F157091")
]
let candidatePhys = physical full
for (name, location) in paths do
    let lexical = Path.TrimEndingDirectorySeparator(Path.GetFullPath location)
    let resolved = physical lexical
    if sameOrUnder full lexical || sameOrUnder lexical full ||
       sameOrUnder candidatePhys resolved || sameOrUnder resolved candidatePhys then
        deny ("Scratch parent aliases or encloses protected token folder: " + name)

// Already provisioned under dedicated token: protected ACL and ONLY its SID
// can read/write. A conventional inherited Users/Everyone ACE is a hard failure.
let directory = DirectoryInfo(full)
let security = FileSystemAclExtensions.GetAccessControl(
    directory, AccessControlSections.Owner ||| AccessControlSections.Access)
let owner = security.GetOwner(typeof<SecurityIdentifier>) :?> SecurityIdentifier
if isNull owner || owner.Value <> sid || not security.AreAccessRulesProtected then
    deny "Scratch parent owner/protected DACL missing."
let rules =
    security.GetAccessRules(true, true, typeof<SecurityIdentifier>)
    |> Seq.cast<FileSystemAccessRule> |> Seq.toList
if List.isEmpty rules ||
   rules |> List.exists (fun r ->
       r.IsInherited || r.AccessControlType <> AccessControlType.Allow ||
       (r.IdentityReference :?> SecurityIdentifier).Value <> sid) then
    deny "Scratch parent has non-dedicated or inherited ACL grants."
if not (rules |> List.exists (fun r ->
    (r.FileSystemRights &&& FileSystemRights.FullControl) = FileSystemRights.FullControl)) then
    deny "Scratch parent SID lacks full control."

// The operator profile ACL and the independent operator-SID denial are
// separately evidenced by the dedicated VM/account owner. This script NEVER
// traverses or probes the operator profile and cannot itself prove that denial.
printfn "W6_PREFLIGHT_OK phase=%s sid=%s physicalScratch=%s tokenProfile=%s nativeKnownFolders=%d protectedOwnerOnlyDacl=true" phase sid candidatePhys (physical activeProfile) paths.Length
