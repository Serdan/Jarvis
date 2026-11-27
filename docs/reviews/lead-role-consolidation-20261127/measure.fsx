// Run from the assigned source checkout: dotnet fsi --exec docs/reviews/lead-role-consolidation-20261127/measure.fsx
open System
open System.IO
open System.Text
open System.Text.Json
open System.Diagnostics
let gitShow (ref: string) =
    let psi = ProcessStartInfo("git")
    psi.ArgumentList.Add("show")
    psi.ArgumentList.Add(ref)
    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    use p = Process.Start(psi)
    let result = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    if p.ExitCode <> 0 then failwith ("git show failed: " + ref)
    result
let prop name ref =
    use d = JsonDocument.Parse(gitShow ref)
    d.RootElement.GetProperty(name: string).GetString()
let role = prop "instructions" "1caf9d891d29a6dc403efd46afd002954e1e89f9"
let v1 = prop "instructions" "ef7d97d1bcb6fc6767fccfe50fc8243f64f0451a:records/roles/Lead.json"
let agent = prop "instructions" "8cfc206ac5aa33a3ba7c8534e4f8d5eda8d69fbc"
let candidate = File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "candidate-role.md"))
let plan = gitShow "717ae71e7ec34b89872220eef4cfe85baa71e8a3"
let options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)
let n (s: string) = Encoding.UTF8.GetByteCount s
let escaped (s: string) = n (JsonSerializer.Serialize(s, options))
let report label (s: string) =
    printfn "%s: UTF16=%d UTF8=%d STJ-string=%d validBody=%b" label s.Length (n s) (escaped s) (not (String.IsNullOrWhiteSpace s) && n s <= 32768 && escaped s <= 16384)
report "Role v1" v1
report "Role v17" role
report "Agent v4" agent
report "Candidate" candidate
printfn "Plan HTML: UTF16=%d UTF8=%d" plan.Length (n plan)
printfn "Two-read role+agent old: %d; candidate with unchanged agent: %d; saved: %d" (2*(role.Length+agent.Length)) (2*(candidate.Length+agent.Length)) (2*(role.Length-candidate.Length))
