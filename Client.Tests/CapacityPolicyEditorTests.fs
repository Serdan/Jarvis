module Client.Tests.CapacityPolicyEditorTests

open System
open System.Threading
open Client.ConsoleTui
open NUnit.Framework

let private initialPolicy =
    { AutomationEnabled = false
      Total = Some 12
      Roles = Map.ofList [ "Lead", 2; "Worker", 3 ] }

let private expectedEnabledPolicy =
    { initialPolicy with AutomationEnabled = true }

let private projectId = Guid.Parse("a33a4d35-4f75-4d38-bb53-c5e7f68b93b1")
let private ledgerId = Guid.Parse("c6a9ecae-71c8-4eeb-a1b1-0b9dffb2cc18")

let private snapshot revision draft (save: Guid -> CapacityPolicyDraft -> Result<unit, CapacityPolicyEditFailure>) =
    { CapacityProjectName = "test-project"
      ProjectId = projectId
      LedgerId = ledgerId
      Epoch = 3
      Revision = revision
      Draft = draft
      Save = save }

let private editorState (ui: ConsoleTui) = ui.InspectProjectEditorForFixture()

let private waitForEditor (ui: ConsoleTui) predicate =
    Assert.That(
        SpinWait.SpinUntil(
            (fun () -> predicate (editorState ui)),
            4000),
        Is.True,
        "The project editor did not reach the expected state.")

let private openEditorWith copyText load =
    let ui = ConsoleTui()
    ui.SetClipboardCopyForFixture(copyText)
    ui.SetCapacityPolicyAccess({ Load = load })
    ui.OpenCapacityPolicyForFixture("test-project")
    waitForEditor ui (fun (mode, busy, pending, _, _, needsReload) ->
        mode = "ProjectEditorMode" && not busy && pending.IsNone && not needsReload)
    ui

let private openEditor load =
    openEditorWith (fun _ -> Ok "fixture clipboard") load

let private press (ui: ConsoleTui) key ch =
    ui.DispatchProjectEditorKeyForFixture(ConsoleKeyInfo(ch, key, false, false, false))

let private pressControl (ui: ConsoleTui) key ch =
    ui.DispatchProjectEditorKeyForFixture(ConsoleKeyInfo(ch, key, false, false, true))

let private typeText ui (value: string) =
    for ch in value do
        press ui ConsoleKey.NoName ch

let private typeLines ui (lines: string list) =
    lines
    |> List.iteri (fun index line ->
        if index > 0 then press ui ConsoleKey.Enter '\000'
        typeText ui line)

let private currentDraft (ui: ConsoleTui) =
    let _, _, _, draft, _, _ = editorState ui
    draft

let private currentNotice (ui: ConsoleTui) =
    let _, _, _, _, notice, _ = editorState ui
    notice

let private renderContains (ui: ConsoleTui) (needle: string) =
    ui.RenderProjectEditorForFixture(80, 24)
    |> Array.exists (fun line -> line.Contains(needle, StringComparison.Ordinal))

let private moveToAutomationStart ui roleCount =
    press ui ConsoleKey.Home '\000'
    for _ in 1 .. roleCount + 1 do
        press ui ConsoleKey.UpArrow '\000'

let private replaceAutomationFlag ui roleCount flag =
    moveToAutomationStart ui roleCount
    for _ in 1 .. "automation=".Length do
        press ui ConsoleKey.RightArrow '\000'
    for _ in 1 .. 3 do
        press ui ConsoleKey.Delete '\000'
    typeText ui flag

[<Test>]
let actualPolicyKeyDispatcherMovesCaretAcrossLinesAndEditsAtCursor () =
    let load _ = Ok(snapshot 1 initialPolicy (fun _ _ -> Ok()))
    let ui = openEditor load
    let original = CapacityPolicyDraft.format initialPolicy

    Assert.That(currentDraft ui, Is.EqualTo(original))
    Assert.That(renderContains ui "role Worker=3▏", Is.True, "The loaded draft should show a visible caret.")

    // Movement is bounded at both ends of the document.
    press ui ConsoleKey.RightArrow '\000'
    press ui ConsoleKey.DownArrow '\000'
    Assert.That(currentDraft ui, Is.EqualTo(original))

    // Move from the final line to the first line using the real key dispatcher.
    moveToAutomationStart ui initialPolicy.Roles.Count
    Assert.That(renderContains ui "▏automation=off", Is.True)
    press ui ConsoleKey.LeftArrow '\000'
    press ui ConsoleKey.UpArrow '\000'
    Assert.That(renderContains ui "▏automation=off", Is.True, "Left/Up at the first character must not move before the document.")

    // End/Right crosses the newline; Left returns to the preceding line's end.
    press ui ConsoleKey.End '\000'
    Assert.That(renderContains ui "automation=off▏", Is.True)
    press ui ConsoleKey.RightArrow '\000'
    Assert.That(renderContains ui "▏total=12", Is.True)
    press ui ConsoleKey.LeftArrow '\000'
    Assert.That(renderContains ui "automation=off▏", Is.True)

    // Change only the switch: Delete removes the old value and typed characters
    // are inserted at the caret rather than appended to the whole document.
    replaceAutomationFlag ui initialPolicy.Roles.Count "on"
    Assert.That(currentDraft ui, Is.EqualTo(CapacityPolicyDraft.format expectedEnabledPolicy))

    // Backspace removes the character before the caret; insertion restores it.
    press ui ConsoleKey.Backspace '\000'
    Assert.That(currentDraft ui, Is.EqualTo("automation=o\ntotal=12\nrole Lead=2\nrole Worker=3"))
    typeText ui "n"
    Assert.That(currentDraft ui, Is.EqualTo(CapacityPolicyDraft.format expectedEnabledPolicy))

[<Test>]
let saveReadbackPreservesCapsAndReloadCopyAndCancelRemainAvailable () =
    let mutable persisted = initialPolicy
    let mutable revision = 1
    let saveCalls = ResizeArray<Guid * CapacityPolicyDraft>()
    let load _ =
        Ok(snapshot revision persisted (fun operation draft ->
            saveCalls.Add((operation, draft))
            persisted <- draft
            revision <- revision + 1
            Ok()))

    let copied = ref None
    let ui =
        openEditorWith
            (fun body ->
                copied := Some body
                Ok "fixture clipboard")
            load

    replaceAutomationFlag ui initialPolicy.Roles.Count "on"
    press ui ConsoleKey.F2 '\000'
    waitForEditor ui (fun (_, busy, pending, _, _, needsReload) ->
        not busy && pending.IsNone && needsReload)

    Assert.That(saveCalls.Count, Is.EqualTo(1))
    let operation, saved = saveCalls[0]
    Assert.That(operation, Is.Not.EqualTo(Guid.Empty))
    Assert.That(saved, Is.EqualTo(expectedEnabledPolicy))
    Assert.That(saved.Total, Is.EqualTo(initialPolicy.Total))
    Assert.That(saved.Roles = initialPolicy.Roles, Is.True)

    // F5 reloads the authenticated readback rather than retaining a local draft.
    press ui ConsoleKey.F5 '\000'
    waitForEditor ui (fun (_, busy, pending, _, _, needsReload) ->
        not busy && pending.IsNone && not needsReload)
    let canonicalBody = CapacityPolicyDraft.format persisted
    Assert.That(currentDraft ui, Is.EqualTo(canonicalBody))
    Assert.That(renderContains ui "Saved Automation: ON", Is.True)

    // F6 copies the exact existing multiline draft without mutating it.
    press ui ConsoleKey.F6 '\000'
    Assert.That(!copied, Is.EqualTo(Some canonicalBody))
    Assert.That(currentDraft ui, Is.EqualTo(canonicalBody))

    // Reload discards unsaved edits; Escape cancels without another write.
    press ui ConsoleKey.NoName 'x'
    Assert.That(currentDraft ui, Is.EqualTo(canonicalBody + "x"))
    press ui ConsoleKey.F5 '\000'
    waitForEditor ui (fun (_, busy, pending, _, _, needsReload) ->
        not busy && pending.IsNone && not needsReload)
    Assert.That(currentDraft ui, Is.EqualTo(canonicalBody))

    press ui ConsoleKey.NoName 'x'
    press ui ConsoleKey.Escape '\000'
    let mode, _, _, _, _, _ = editorState ui
    Assert.That(mode, Is.EqualTo("AgentsMode"))
    Assert.That(saveCalls.Count, Is.EqualTo(1))

[<Test>]
let invalidPolicyDraftIsRejectedBeforeAuthenticatedSave () =
    let mutable saveCalls = 0
    let load _ =
        Ok(snapshot 1 initialPolicy (fun _ _ ->
            saveCalls <- saveCalls + 1
            Ok()))

    let ui = openEditor load
    pressControl ui ConsoleKey.U 'u'
    typeLines ui [ "automation=on"; "role Worker=3" ]
    press ui ConsoleKey.F2 '\000'

    let _, busy, pending, _, notice, needsReload = editorState ui
    Assert.That(busy, Is.False)
    Assert.That(pending.IsNone, Is.True)
    Assert.That(needsReload, Is.False)
    Assert.That(saveCalls, Is.EqualTo(0))
    Assert.That(notice, Does.Contain("total"))

[<Test>]
let stalePolicyWriteLocksDraftUntilReloadingLatestRevision () =
    let mutable latest = initialPolicy
    let mutable revision = 1
    let mutable saveCalls = 0
    let load _ =
        Ok(snapshot revision latest (fun _ _ ->
            saveCalls <- saveCalls + 1
            Error(CapacityStale "The policy revision changed; reload before saving.")))

    let ui = openEditor load
    replaceAutomationFlag ui initialPolicy.Roles.Count "on"
    let attempted = currentDraft ui
    press ui ConsoleKey.F2 '\000'
    waitForEditor ui (fun (_, busy, pending, _, _, needsReload) ->
        not busy && pending.IsNone && needsReload)

    Assert.That(saveCalls, Is.EqualTo(1))
    Assert.That(currentNotice ui, Does.Contain("revision changed"))
    press ui ConsoleKey.NoName 'x'
    Assert.That(currentDraft ui, Is.EqualTo(attempted), "A stale draft must remain locked until readback.")

    latest <- { initialPolicy with Total = Some 20 }
    revision <- revision + 1
    press ui ConsoleKey.F5 '\000'
    waitForEditor ui (fun (_, busy, pending, _, _, needsReload) ->
        not busy && pending.IsNone && not needsReload)
    Assert.That(currentDraft ui, Is.EqualTo(CapacityPolicyDraft.format latest))

[<Test>]
let uncertainPolicyWriteRetriesTheExactOperationWithoutAllowingEdits () =
    let operations = ResizeArray<Guid>()
    let mutable saveCalls = 0
    let load _ =
        Ok(snapshot 1 initialPolicy (fun operation _ ->
            saveCalls <- saveCalls + 1
            operations.Add operation
            if saveCalls = 1 then Error(CapacityUncertain "The write result is unknown.")
            else Ok()))

    let ui = openEditor load
    replaceAutomationFlag ui initialPolicy.Roles.Count "on"
    let attempted = currentDraft ui
    press ui ConsoleKey.F2 '\000'
    waitForEditor ui (fun (_, busy, pending, _, _, needsReload) ->
        not busy && pending.IsSome && not needsReload)

    let _, _, pending, _, notice, _ = editorState ui
    Assert.That(pending.IsSome, Is.True)
    Assert.That(notice, Does.Contain("same operation"))
    press ui ConsoleKey.NoName 'x'
    Assert.That(currentDraft ui, Is.EqualTo(attempted), "An uncertain write must keep the draft immutable.")

    press ui ConsoleKey.F2 '\000'
    waitForEditor ui (fun (_, busy, pending, _, _, needsReload) ->
        not busy && pending.IsNone && needsReload)

    Assert.That(operations.Count, Is.EqualTo(2))
    Assert.That(operations[0], Is.EqualTo(operations[1]), "Retry must reuse the original operation ID.")
