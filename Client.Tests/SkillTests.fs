module SkillTests

open System
open System.IO
open Client
open Client.Effect
open Client.ProjectBrowser
open Common
open FsUnitTyped
open NUnit.Framework

let private tempWorkspace () =
    let path = Path.Combine(Path.GetTempPath(), "jarvis-skill-tests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(path) |> ignore
    path

let private createProject workspace projectName =
    Directory.CreateDirectory(Path.Combine(workspace, projectName)) |> ignore

let private writeSkill workspace projectName skillName fileName (content: string) =
    let directory =
        Path.Combine(workspace, projectName, ".jarvis", "skills", skillName)

    Directory.CreateDirectory(directory) |> ignore
    File.WriteAllText(Path.Combine(directory, fileName), content)

[<Test>]
let listSkills_discovers_skill_files_and_descriptions () =
    let workspace = tempWorkspace ()

    try
        createProject workspace "Project1"
        writeSkill workspace "Project1" "deploy" "SKILL.md" "# Deploy\n\nDeploy production safely.\n\nMore detail."
        writeSkill workspace "Project1" "build" "skill.md" "# Build\n\nRun the canonical build workflow."
        Directory.CreateDirectory(Path.Combine(workspace, "Project1", ".jarvis", "skills", "empty")) |> ignore

        let runtime = Runtime(workspace)

        match listSkills { ProjectName = "Project1" } runtime with
        | Error error -> Assert.Fail($"ListSkills failed: {EffectError.toString error}")
        | Ok result ->
            result.Skills
            |> shouldEqual
                [ { Name = "build"
                    Description = Some "Run the canonical build workflow." }
                  { Name = "deploy"
                    Description = Some "Deploy production safely." } ]
    finally
        Directory.Delete(workspace, true)

[<Test>]
let getSkill_reads_skill_content_and_accepts_lowercase_file_name () =
    let workspace = tempWorkspace ()

    try
        createProject workspace "Project1"
        let content = "# Diagnose\n\nInspect logs before changing state."
        writeSkill workspace "Project1" "diagnose" "skill.md" content

        let runtime = Runtime(workspace)

        getSkill
            { ProjectName = "Project1"
              SkillName = "diagnose" }
            runtime
        |> shouldEqual
            (Ok
                { Name = "diagnose"
                  Content = content })
    finally
        Directory.Delete(workspace, true)

[<Test>]
let listSkills_returns_empty_list_when_project_has_no_skills_directory () =
    let workspace = tempWorkspace ()

    try
        createProject workspace "Project1"
        let runtime = Runtime(workspace)

        listSkills { ProjectName = "Project1" } runtime
        |> shouldEqual (Ok { Skills = [] })
    finally
        Directory.Delete(workspace, true)

[<Test>]
let getSkill_rejects_traversal_like_skill_names () =
    let workspace = tempWorkspace ()

    try
        createProject workspace "Project1"
        let runtime = Runtime(workspace)

        match
            getSkill
                { ProjectName = "Project1"
                  SkillName = "../secret" }
                runtime
        with
        | Error(ValidationError _) -> ()
        | Error error -> Assert.Fail($"Expected validation error, got {EffectError.toString error}")
        | Ok _ -> Assert.Fail("Expected traversal-like skill name to be rejected.")
    finally
        Directory.Delete(workspace, true)

[<Test>]
let getSkill_reports_missing_skill () =
    let workspace = tempWorkspace ()

    try
        createProject workspace "Project1"
        let runtime = Runtime(workspace)

        match
            getSkill
                { ProjectName = "Project1"
                  SkillName = "missing" }
                runtime
        with
        | Error(NotFoundError _) -> ()
        | Error error -> Assert.Fail($"Expected not-found error, got {EffectError.toString error}")
        | Ok _ -> Assert.Fail("Expected missing skill to fail.")
    finally
        Directory.Delete(workspace, true)
