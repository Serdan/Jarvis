module FilterSelectorTests

open Client
open NUnit.Framework

let private parse query =
    FilterSelectors.parse ["Wayfold"; "Projekt Loke"; "Jarvis"] ["Worker 1"; "Worker 2"; "GUI Agent"] query

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
    let terms = parse "commit #wayfold@worker 1 Review regression"
    Assert.That(terms.Text = ["commit"; "Review"; "regression"], Is.True)
    Assert.That(terms.Projects = ["wayfold"], Is.True)
    Assert.That(terms.Agents = ["worker 1"], Is.True)

[<Test>]
let ``bare text stays searchable and partial markers do not filter`` () =
    let terms = parse "render # @"
    Assert.That(terms.Text = ["render"], Is.True)
    Assert.That(terms.Projects, Is.Empty)
    Assert.That(terms.Agents, Is.Empty)
    Assert.That(FilterSelectors.parse [] [] "#", Is.EqualTo(FilterSelectors.parse [] [] ""))

[<Test>]
let ``malformed selector with missing local snapshot still preserves its first word`` () =
    let terms = FilterSelectors.parse [] [] "#unknown @missing"
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
    let terms = FilterSelectors.parse names [] "#Wayfold/silver-ring-trade"
    Assert.That(terms.Projects = ["Wayfold/silver-ring-trade"], Is.True)

[<Test>]
let ``repeat selectors keep their explicit AND meaning`` () =
    let terms = parse "#Wayfold #Jarvis @Worker @GUI"
    Assert.That(terms.Projects = ["Wayfold"; "Jarvis"], Is.True)
    Assert.That(terms.Agents = ["Worker"; "GUI"], Is.True)
