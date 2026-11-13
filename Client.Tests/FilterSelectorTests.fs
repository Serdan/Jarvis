module FilterSelectorTests

open Client
open NUnit.Framework

let private parse query = FilterSelectors.parse query

[<Test>]
let ``selectors distinguish projects from agents in either contiguous order`` () =
    for query in ["#Wayfold@Worker 1"; "@Worker 1#Wayfold"; "#Wayfold @Worker 1"; "@Worker 1 #Wayfold"] do
        let terms = parse query
        Assert.That(terms.Projects = ["Wayfold"], Is.True, query)
        Assert.That(terms.Agents = ["Worker 1"], Is.True, query)
        Assert.That(terms.Text, Is.Empty, query)

[<Test>]
let ``name fragments match case insensitively with multiword names`` () =
    let terms = parse "#projekt loke@GUI Agent"
    Assert.That(terms.Projects = ["projekt loke"], Is.True)
    Assert.That(terms.Agents = ["GUI Agent"], Is.True)

[<Test>]
let ``unrelated descriptive text remains text and selector terms combine with AND`` () =
    let terms = parse "commit #wayfold@worker 1 -- Review regression"
    Assert.That(terms.Text = ["commit"; "Review"; "regression"], Is.True)
    Assert.That(terms.Projects = ["wayfold"], Is.True)
    Assert.That(terms.Agents = ["worker 1"], Is.True)

[<Test>]
let ``bare text stays searchable and partial markers do not filter`` () =
    let terms = parse "render # @"
    Assert.That(terms.Text = ["render"], Is.True)
    Assert.That(terms.Projects, Is.Empty)
    Assert.That(terms.Agents, Is.Empty)
    Assert.That(FilterSelectors.parse "#", Is.EqualTo(FilterSelectors.parse ""))

[<Test>]
let ``explicit selector intent is independent of a snapshot`` () =
    let terms = FilterSelectors.parse "#unknown @missing"
    Assert.That(terms.Projects = ["unknown"], Is.True)
    Assert.That(terms.Agents = ["missing"], Is.True)

[<Test>]
let ``single matching agent completes all words without guessing a separator`` () =
    Assert.That(FilterSelectors.completeTrailing [] ["GUI Agent"] "@Gu", Is.EqualTo("@GuI Agent"))
    Assert.That(FilterSelectors.completeTrailing [] ["Worker 1"] "@Work", Is.EqualTo("@Worker 1"))

[<Test>]
let ``ambiguous agent completion extends only to longest common prefix`` () =
    let names = ["Worker 2"; "Worker 1"]
    let first = FilterSelectors.completeTrailing [] names "@Wo"
    Assert.That(first, Is.EqualTo("@Worker "))
    Assert.That(FilterSelectors.completeTrailing [] names first, Is.EqualTo(first))
    Assert.That(FilterSelectors.completeTrailing [] names (first + "1"), Is.EqualTo("@Worker 1"))

[<Test>]
let ``case insensitive matching preserves typed case and deterministic result`` () =
    let a = FilterSelectors.completeTrailing [] ["Worker 2"; "Worker 1"] "@wo"
    let b = FilterSelectors.completeTrailing [] ["Worker 1"; "Worker 2"] "@wo"
    Assert.That(a, Is.EqualTo("@worker "))
    Assert.That(b, Is.EqualTo(a))
    Assert.That(FilterSelectors.completeTrailing [] ["woRKer 2"; "WORKER 1"] "@wo", Is.EqualTo("@woRKER "))

[<Test>]
let ``no candidate means no change and prior terms are untouched`` () =
    Assert.That(FilterSelectors.completeTrailing [] [] "#Way", Is.EqualTo("#Way"))
    Assert.That(FilterSelectors.completeTrailing ["Wayfold"] [] "urgent #Wa", Is.EqualTo("urgent #Wayfold"))
    Assert.That(FilterSelectors.completeTrailing ["Wayfold"] ["Worker 1"] "urgent #Wayfold@Wo", Is.EqualTo("urgent #Wayfold@Worker 1"))
    Assert.That(FilterSelectors.completeTrailing ["Wayfold"] [] "#Wayfold description", Is.EqualTo("#Wayfold description"))
    Assert.That(FilterSelectors.completeTrailing ["Wayfold"] [] "urgent words", Is.EqualTo("urgent words"))

[<Test>]
let ``project completion is stable and never cycles candidate names`` () =
    let first = FilterSelectors.completeTrailing ["Waygate"; "Wayfold"] [] "#W"
    Assert.That(first, Is.EqualTo("#Way"))
    Assert.That(FilterSelectors.completeTrailing ["Waygate"; "Wayfold"] [] first, Is.EqualTo(first))
    Assert.That(FilterSelectors.completeTrailing ["Wayfold"] [] "#Wa", Is.EqualTo("#Wayfold"))

[<Test>]
let ``shortcut appends selectors without clearing previous filters`` () =
    Assert.That(FilterSelectors.appendSelector '#' "ready", Is.EqualTo("ready #"))
    Assert.That(FilterSelectors.appendSelector '@' "#Wayfold", Is.EqualTo("#Wayfold @"))
    Assert.That(FilterSelectors.appendSelector '@' "", Is.EqualTo("@"))
    Assert.That(FilterSelectors.appendSelector '#' "ready ", Is.EqualTo("ready #"))

[<Test>]
let ``project worktree labels are valid selector completions`` () =
    let names = ["Wayfold/silver-ring-trade"; "Wayfold/tooltip-fix"]
    Assert.That(FilterSelectors.completeTrailing names [] "#Wayfold/s", Is.EqualTo("#Wayfold/silver-ring-trade"))
    let terms = FilterSelectors.parse "#Wayfold/silver-ring-trade"
    Assert.That(terms.Projects = ["Wayfold/silver-ring-trade"], Is.True)

[<Test>]
let ``repeat selectors keep their explicit AND meaning`` () =
    let terms = parse "#Wayfold #Jarvis @Worker @GUI"
    Assert.That(terms.Projects = ["Wayfold"; "Jarvis"], Is.True)
    Assert.That(terms.Agents = ["Worker"; "GUI"], Is.True)

[<Test>]
let numericAgentTokensRespectBoundariesWithoutSuppressingPartialWorkerMatches () =
    for name in ["Worker"; "Worker 1"; "Worker 10"; "PreWorker 1"; "Worker 1_backup"] do
        Assert.That(FilterSelectors.matchesAgentTerm "worker" name, Is.True, name)
    for name in ["Worker 10"; "Worker 11"; "Worker 1a"; "Worker 1_backup"] do
        Assert.That(FilterSelectors.matchesAgentTerm "Worker 1" name, Is.False, name)
    for name in ["Worker 1"; "PreWorker 1"; "Worker 101 Worker 1"; "Worker 1-support"] do
        Assert.That(FilterSelectors.matchesAgentTerm "wOrKeR 1" name, Is.True, name)
    Assert.That(FilterSelectors.matchesAgentTerm "Worker 1" "Worker 10 Worker 1", Is.True)
    Assert.That(FilterSelectors.matchesAgentTerm "Worker 10" "Worker 100", Is.False)
    Assert.That(FilterSelectors.matchesAgentTerm "Worker" "GUI Agent", Is.False)
    Assert.That(FilterSelectors.matchesAgentTerm "GUI Ag" "GUI Agent", Is.True)

[<Test>]
let ``absent multiword selector does not depend on candidates`` () =
    let absentNumeric = parse "#Wayfold@Worker 1"
    Assert.That(absentNumeric.Projects = ["Wayfold"], Is.True)
    Assert.That(absentNumeric.Agents = ["Worker 1"], Is.True)
    Assert.That(absentNumeric.Text, Is.Empty)
    let absentName = parse "#Projekt Loke@GUI Agent"
    Assert.That(absentName.Projects = ["Projekt Loke"], Is.True)
    Assert.That(absentName.Agents = ["GUI Agent"], Is.True)
    Assert.That(absentName.Text, Is.Empty)

[<Test>]
let ``explicit text separator and multiple selectors have stable AND semantics`` () =
    let terms = parse "review #Wayfold@Worker 1 -- sprint 1 @GUI Agent"
    Assert.That(terms.Text = ["review"; "sprint"; "1"], Is.True)
    Assert.That(terms.Projects = ["Wayfold"], Is.True)
    Assert.That(terms.Agents = ["Worker 1"; "GUI Agent"], Is.True)
    let projectText = parse "#Projekt Loke -- audio fix"
    Assert.That(projectText.Projects = ["Projekt Loke"], Is.True)
    Assert.That(projectText.Text = ["audio"; "fix"], Is.True)
    Assert.That((parse "#Wayfold -- ready").Text = ["ready"], Is.True)
    Assert.That((parse "ready #Wayfold").Text = ["ready"], Is.True)

[<Test>]
let ``selector grammar handles missing names and live partial input without text leakage`` () =
    for query, expected in
        [ "@Worker 1", "Worker 1"
          "@Worker 10", "Worker 10"
          "@GUI Agent", "GUI Agent"
          "@Nobody Here", "Nobody Here"
          "@Worker 1 ", "Worker 1" ] do
        let terms = parse query
        Assert.That(terms.Agents = [expected], Is.True, query)
        Assert.That(terms.Text, Is.Empty, query)
    Assert.That((parse "#Wayfold@").Projects = ["Wayfold"], Is.True)
    Assert.That((parse "#Wayfold@").Agents, Is.Empty)
    Assert.That((parse "#Wayfold --").Projects = ["Wayfold"], Is.True)
    Assert.That((parse "#Wayfold --").Text, Is.Empty)
    Assert.That((parse "#").Projects, Is.Empty)
    Assert.That((parse "--").Text, Is.Empty)

[<Test>]
let ``delimiter must be standalone and embedded markers stay ordinary text`` () =
    let text = parse "mail user@example.test progress--followup"
    Assert.That(text.Text = ["mail"; "user@example.test"; "progress--followup"], Is.True)
    let terms = parse "@Worker 1--test"
    Assert.That(terms.Agents = ["Worker 1--test"], Is.True)
    Assert.That(terms.Text, Is.Empty)
    Assert.That(FilterSelectors.completeTrailing ["Wayfold"] [] "#Wayfold -- sprint", Is.EqualTo("#Wayfold -- sprint"))
    Assert.That(FilterSelectors.completeTrailing ["Wayfold"] [] "#Wa --", Is.EqualTo("#Wa --"))
