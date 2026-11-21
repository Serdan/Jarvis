module BuildIdentityTests

open Client.BuildIdentity
open NUnit.Framework

let private sample =
    { ApplicationVersion = "1.2.3"
      GitCommit = "abcdef0123456789abcdef0123456789abcdef01"
      RuntimeIdentifier = "linux-x64"
      Dirty = false
      ProtocolVersion = "3.68" }

[<Test>]
let cleanIdentityShowsApplicationProtocolCommitAndRuntime () =
    let formatted = detail sample
    Assert.That(formatted, Does.Contain("Jarvis Client 1.2.3"))
    Assert.That(formatted, Does.Contain(sample.GitCommit))
    Assert.That(formatted, Does.Contain("Protocol: 3.68"))
    Assert.That(formatted, Does.Contain("RID: linux-x64"))
    Assert.That(compact sample, Does.Contain("abcdef012345"))
    Assert.That(buildLabel sample, Does.Contain("clean"))

[<Test>]
let dirtyAndUnknownSourceCannotLookLikeCleanRelease () =
    Assert.That(compact { sample with Dirty = true }, Does.Contain("dirty"))
    Assert.That(buildLabel { sample with GitCommit = "unknown"; Dirty = false },
                Does.Contain("dirty/development"))

[<Test>]
let compactHeaderPreservesVersionAndProtocolInNarrowFrame () =
    let frame = Client.TerminalFrame.Frame(24, 2)
    frame.WriteLine("Jarvis " + compact sample)
    let rendered = [ for col in 0 .. 23 -> frame.GetCell(0, col).Text ] |> String.concat ""
    Assert.That(rendered, Does.Contain("v1.2.3"))
    Assert.That(rendered, Does.Contain("P3.68"))

[<Test>]
let disconnectedTuiRendersAppAndProtocolAtSmallAndNormalHeight () =
    let tui = Client.ConsoleTui.ConsoleTui()
    let small = tui.InspectIdentityHeaderForFixture(30, 16)
    let large = tui.InspectIdentityHeaderForFixture(80, 24)
    for lines in [ small; large ] do
        let header = String.concat "\n" (lines |> List.take 2)
        Assert.That(header, Does.Contain("v0.1.0"))
        Assert.That(header, Does.Contain("P3.68"))
