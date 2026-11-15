namespace Jarvis.WorkQueues

open System
open System.Collections.Generic

/// Read-only diagnostics for the caller-verified stage-generation dependency graph.
/// A successful result is never an authorization to claim, wake or complete work.
type DependencyGraphDiagnostics =
    { GraphVersion: int
      CoverageComplete: bool
      StageIds: string list
      HardEdges: (string * string) list
      CycleWarnings: CycleWarning list }

[<RequireQualifiedAccess>]
module WorkQueueDependencyDiagnostics =
    /// Analyse only hard WorkStage.Dependencies; never infer edges from subjects,
    /// lineages, messages or waiver receipts. Report incomplete coverage as Error
    /// rather than returning a seemingly authoritative list of runnable stages.
    /// Input Map and Set traversals are ordered, making output deterministic.
    let analyze (graphVersion: int) (covered: bool) (works: Map<string, WorkStage>)
        : Result<DependencyGraphDiagnostics, string> =
        let nodes = Map.toArray works
        let lookup = Dictionary<string, WorkStage>(StringComparer.Ordinal)
        let mismatchedIds = ResizeArray<string>()

        for (id, stage) in nodes do
            if stage.Id <> id then
                mismatchedIds.Add(sprintf "%s != %s" id stage.Id)
            lookup.Add(id, stage)

        let hardEdges = ResizeArray<string * string>()
        let missingEdges = ResizeArray<string * string>()
        for (id, stage) in nodes do
            for prerequisite in stage.Dependencies do
                if lookup.ContainsKey prerequisite then
                    hardEdges.Add(id, prerequisite)
                else
                    missingEdges.Add(id, prerequisite)

        if not covered || mismatchedIds.Count > 0 || missingEdges.Count > 0 then
            let reasons =
                [ if not covered then
                      "unverified graph high-water/index"
                  if mismatchedIds.Count > 0 then
                      sprintf "stage ID/key mismatch: %s" (String.concat ", " mismatchedIds)
                  if missingEdges.Count > 0 then
                      let witnesses =
                          missingEdges
                          |> Seq.map (fun (source, target) -> sprintf "%s -> %s" source target)
                          |> String.concat ", "
                      sprintf "missing hard prerequisite stage generation(s): %s" witnesses ]
            Error(sprintf "dependency graph v%d incomplete: %s" graphVersion (String.concat "; " reasons))
        else
            // Tarjan's strongly connected components: one visit per stage/edge.
            // The componentId ID is internal; public member and edge order comes
            // from deterministic Map/Set traversal, not DFS completion order.
            let indices = Dictionary<string, int>(StringComparer.Ordinal)
            let lowlinks = Dictionary<string, int>(StringComparer.Ordinal)
            let onStack = HashSet<string>(StringComparer.Ordinal)
            let stack = Stack<string>()
            let componentOf = Dictionary<string, int>(StringComparer.Ordinal)
            let mutable nextIndex = 0
            let mutable nextComponent = 0

            let rec visit node =
                indices.[node] <- nextIndex
                lowlinks.[node] <- nextIndex
                nextIndex <- nextIndex + 1
                stack.Push(node)
                onStack.Add(node) |> ignore

                for prerequisite in lookup.[node].Dependencies do
                    if not (indices.ContainsKey prerequisite) then
                        visit prerequisite
                        lowlinks.[node] <- min lowlinks.[node] lowlinks.[prerequisite]
                    elif onStack.Contains prerequisite then
                        lowlinks.[node] <- min lowlinks.[node] indices.[prerequisite]

                if lowlinks.[node] = indices.[node] then
                    let mutable complete = false
                    while not complete do
                        let memberId = stack.Pop()
                        onStack.Remove(memberId) |> ignore
                        componentOf.[memberId] <- nextComponent
                        complete <- memberId = node
                    nextComponent <- nextComponent + 1

            for (id, _) in nodes do
                if not (indices.ContainsKey id) then
                    visit id

            let membersByComponent = Dictionary<int, ResizeArray<string>>()
            let edgesByComponent = Dictionary<int, ResizeArray<string * string>>()

            // Map order produces ascending member IDs in each componentId.
            for (id, _) in nodes do
                let componentId = componentOf.[id]
                match membersByComponent.TryGetValue componentId with
                | true, members -> members.Add id
                | false, _ ->
                    let members = ResizeArray<string>()
                    members.Add id
                    membersByComponent.Add(componentId, members)

            // Map/Set order also produces ascending exact hard-edge witnesses.
            for (source, target) in hardEdges do
                let componentId = componentOf.[source]
                if componentId = componentOf.[target] then
                    match edgesByComponent.TryGetValue componentId with
                    | true, edges -> edges.Add(source, target)
                    | false, _ ->
                        let edges = ResizeArray<string * string>()
                        edges.Add(source, target)
                        edgesByComponent.Add(componentId, edges)

            let emitted = HashSet<int>()
            let warnings = ResizeArray<CycleWarning>()
            // Emit components by their lexicographically first stage ID.
            for (id, _) in nodes do
                let componentId = componentOf.[id]
                if emitted.Add componentId then
                    let members = membersByComponent.[componentId] |> Seq.toList
                    let edges =
                        match edgesByComponent.TryGetValue componentId with
                        | true, value -> value |> Seq.toList
                        | false, _ -> []
                    if members.Length > 1 || (edges |> List.exists (fun (source, target) -> source = target)) then
                        warnings.Add { Members = members; WitnessEdges = edges }

            Ok
                { GraphVersion = graphVersion
                  CoverageComplete = true
                  StageIds = nodes |> Array.map fst |> Array.toList
                  HardEdges = List.ofSeq hardEdges
                  CycleWarnings = List.ofSeq warnings }
