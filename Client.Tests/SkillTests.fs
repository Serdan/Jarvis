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

[<Test>]
let createSkill_writes_canonical_file_and_returns_metadata () =
    let workspace = tempWorkspace ()

    try
        createProject workspace "Project1"
        let runtime = Runtime(workspace)
        let content = "# Release\n\nDeploy only after tests pass."

        match
            createSkill
                { ProjectName = "Project1"
                  SkillName = "release"
                  Content = content
                  Overwrite = false }
                runtime
        with
        | Error error -> Assert.Fail($"CreateSkill failed: {EffectError.toString error}")
        | Ok result ->
            result.Name |> shouldEqual "release"
            result.Path |> shouldEqual ".jarvis/skills/release/SKILL.md"
            result.Hash.StartsWith("sha256:", StringComparison.Ordinal) |> shouldEqual true
            result.Overwritten |> shouldEqual false

            let path = Path.Combine(workspace, "Project1", ".jarvis", "skills", "release", "SKILL.md")
            File.ReadAllText(path) |> shouldEqual content

            getSkill
                { ProjectName = "Project1"
                  SkillName = "release" }
                runtime
            |> shouldEqual
                (Ok
                    { Name = "release"
                      Content = content })
    finally
        Directory.Delete(workspace, true)

[<Test>]
let createSkill_rejects_existing_skill_by_default () =
    let workspace = tempWorkspace ()

    try
        createProject workspace "Project1"
        writeSkill workspace "Project1" "release" "skill.md" "# Existing\n\nKeep me."
        let runtime = Runtime(workspace)

        match
            createSkill
                { ProjectName = "Project1"
                  SkillName = "release"
                  Content = "# Replacement"
                  Overwrite = false }
                runtime
        with
        | Error(ValidationError message) ->
            message.Contains("already exists", StringComparison.OrdinalIgnoreCase) |> shouldEqual true
        | Error error -> Assert.Fail($"Expected validation error, got {EffectError.toString error}")
        | Ok _ -> Assert.Fail("Expected existing skill to be rejected.")
    finally
        Directory.Delete(workspace, true)

[<Test>]
let createSkill_overwrite_replaces_skill_with_canonical_file () =
    let workspace = tempWorkspace ()

    try
        createProject workspace "Project1"
        writeSkill workspace "Project1" "release" "SKILL.md" "# Existing"
        let runtime = Runtime(workspace)
        let replacement = "# Replacement\n\nUpdated procedure."

        match
            createSkill
                { ProjectName = "Project1"
                  SkillName = "release"
                  Content = replacement
                  Overwrite = true }
                runtime
        with
        | Error error -> Assert.Fail($"CreateSkill overwrite failed: {EffectError.toString error}")
        | Ok result ->
            result.Overwritten |> shouldEqual true
            let path = Path.Combine(workspace, "Project1", ".jarvis", "skills", "release", "SKILL.md")
            File.ReadAllText(path) |> shouldEqual replacement
    finally
        Directory.Delete(workspace, true)

[<Test>]
let createSkill_rejects_empty_content () =
    let workspace = tempWorkspace ()

    try
        createProject workspace "Project1"
        let runtime = Runtime(workspace)

        match
            createSkill
                { ProjectName = "Project1"
                  SkillName = "empty"
                  Content = "   "
                  Overwrite = false }
                runtime
        with
        | Error(ValidationError _) -> ()
        | Error error -> Assert.Fail($"Expected validation error, got {EffectError.toString error}")
        | Ok _ -> Assert.Fail("Expected empty skill content to be rejected.")
    finally
        Directory.Delete(workspace, true)
