using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// WQ-10 is deliberately an offline inspector. It has no Git-write, HTTP,
// browser, scheduler, token, migration-activation or deployment code path.
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly Regex Oid = new("^[a-f0-9]{40}([a-f0-9]{24})?$", RegexOptions.Compiled);
    private static readonly Regex RefName = new("^refs/heads/[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.Compiled);

    private sealed class Snapshot
    {
        public string Name { get; set; } = "";
        public string Repo { get; set; } = "";
        public string Head { get; set; } = "";
        public string Ref { get; set; } = "";
    }

    private sealed class PendingOperation
    {
        public string Id { get; set; } = "";
        public string Project { get; set; } = "";
        public string Status { get; set; } = "";
        public string Actor { get; set; } = "";
    }

    private sealed class Observations
    {
        public bool? LegacyManagerStopped { get; set; }
        public bool? OldWritersFenced { get; set; }
        public bool? ClientProtocolsCompatible { get; set; }
        public PendingOperation[]? PendingOperations { get; set; }
        public string[]? InFlightSourceJobs { get; set; }
        public string[]? UncertainBrowserSubmissions { get; set; }
    }

    private sealed class Manifest
    {
        public int SchemaVersion { get; set; }
        public Snapshot? Account { get; set; }
        public Snapshot[]? Projects { get; set; }
        public Observations? Observations { get; set; }
    }

    private sealed record Finding(string Severity, string Code, string Context, string NextAction);
    private sealed record LegacyTask(string TaskId, string Title, string Lifecycle,
        string HistoricalPath, string[] Attempts, string[] SourceRefs, string[] SourceCommits,
        string ProposedWorkId, string Disposition);
    private sealed record Message(string MessageId, string Subject, string State, string WorkId);
    private sealed record LegacyAgent(string AgentId, string Execution, string AssignmentTaskId);
    private sealed record LegacyReview(string ReviewId, string TaskId, string Status,
        string ReviewerId, string ReviewedHeadOid);
    private sealed record LegacyIntegration(string SubmissionId, string TaskId, string Status,
        string SubmittedCommit, string ReviewId);
    private sealed record LaunchView(string LaunchId, string RoleId, string State, string AccountTokenId);
    private sealed record ProjectInventory(string Name, string Head, string CurrentRefTip,
        int LedgerEpoch, long LedgerNextSequence, long? OriginSourceHighWater,
        string QueueMigration, int HistoricalOperations,
        int HistoricalAgents, int HistoricalReviews, int HistoricalIntegrations,
        int HistoricalNotifications, int HistoricalChallenges, int ExistingQueueWorks,
        int ExistingLaunches, LegacyTask[] Tasks, Message[] Messages, LegacyAgent[] Agents,
        LegacyReview[] Reviews, LegacyIntegration[] Integrations, LaunchView[] Launches);
    private sealed record AccountInventory(string Head, string CurrentRefTip, int Epoch,
        string MigrationPhase, int MaxConcurrentAgents, int HeldTokens, int ReleasedTokens,
        string[] HeldLaunchIds);
    private sealed record Report(int SchemaVersion, string Mode, string Readiness,
        string Activation, AccountInventory? Account, ProjectInventory[] Projects,
        PendingOperation[] PendingOperations, string[] InFlightSourceJobs,
        string[] UncertainBrowserSubmissions, Finding[] Findings, string[] PreparedStatePreview,
        string[] AuthorizedRepairProcedure, string Summary);

    private static int Main(string[] args)
    {
        if (args.Length is not (1 or 3) || (args.Length == 3 &&
            (args[1] != "--format" || args[2] is not ("json" or "markdown"))))
        {
            Console.Error.WriteLine("Usage: dotnet run -c Release --project tools/Wq10MigrationPreview -- <manifest.json> [--format json|markdown]");
            return 64;
        }
        try
        {
            var raw = File.ReadAllText(args[0]);
            var manifest = JsonSerializer.Deserialize<Manifest>(raw, JsonOptions)
                ?? throw new InvalidDataException("Empty manifest.");
            if (manifest.SchemaVersion != 1 || manifest.Account is null || manifest.Projects is null
                || manifest.Projects.Length == 0)
                throw new InvalidDataException("Manifest requires schemaVersion=1, account and nonempty projects.");
            var report = Inspect(manifest);
            if (args.Length == 3 && args[2] == "markdown")
                Console.Write(Markdown(report));
            else
                Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0; // BLOCKED is a valid inspectable result, not a tool failure.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Preflight input error: " + ex.Message);
            return 1;
        }
    }

    private static Report Inspect(Manifest input)
    {
        var findings = new List<Finding>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var projects = new List<ProjectInventory>();
        var account = InspectAccount(input.Account!, findings);
        foreach (var project in input.Projects!.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(project.Name) || !names.Add(project.Name))
                throw new InvalidDataException("Project names must be unique, nonblank identities.");
            projects.Add(InspectProject(project, findings));
        }

        var observations = input.Observations ?? new Observations();
        CheckProof(observations.LegacyManagerStopped, "MANAGER_NOT_SETTLED",
            "Legacy Manager schedules/launches not proven stopped",
            "Inspect and settle actual Manager handoffs and tokens under old protocol", findings);
        CheckProof(observations.OldWritersFenced, "OLD_WRITERS_UNFENCED",
            "Old-client writes have not been independently fenced",
            "Verify compatible protocol handshake and inspect concurrent old writers", findings);
        CheckProof(observations.ClientProtocolsCompatible, "PROTOCOL_UNVERIFIED",
            "Client protocol compatibility not demonstrated",
            "Confirm the minimum protocol across every connected device", findings);
        var pending = (observations.PendingOperations ?? []).OrderBy(o => o.Project, StringComparer.Ordinal)
            .ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
        foreach (var op in pending)
        {
            if (string.IsNullOrWhiteSpace(op.Id))
                findings.Add(new("BLOCKER", "OPERATION_ID_MISSING", op.Project,
                    "Recover the original operation ID, actor and journal disposition"));
            else if (!IsSettled(op.Status))
                findings.Add(new("BLOCKER", "OPERATION_UNCERTAIN", op.Project + "/" + op.Id + " " + op.Status,
                    "Inspect original operation ID and receipt; never assume absence means failure"));
        }
        var jobs = (observations.InFlightSourceJobs ?? []).Order(StringComparer.Ordinal).ToArray();
        var browser = (observations.UncertainBrowserSubmissions ?? []).Order(StringComparer.Ordinal).ToArray();
        foreach (var job in jobs)
            findings.Add(new("BLOCKER", "SOURCE_JOB_IN_FLIGHT", job,
                "Verify job terminal evidence and remote source ref before preparing project"));
        foreach (var submission in browser)
            findings.Add(new("BLOCKER", "BROWSER_OUTCOME_UNKNOWN", submission,
                "Reconcile the original browser operation/session receipt before preparing project"));
        if (account is null || projects.Count == 0)
            findings.Add(new("BLOCKER", "SNAPSHOT_UNAVAILABLE", "Required snapshot missing",
                "Provide exact trusted Git commit(s) from a disposable or explicitly approved export"));

        var blockers = findings.Count(f => f.Severity == "BLOCKER");
        var readiness = blockers == 0 ? "PREVIEW_READY_FOR_REVIEW" : "NO_GO";
        var planned = projects.Select(p =>
            $"{p.Name}: keep {p.Head}; propose inert Prepared(epoch {account?.Epoch ?? 0}), " +
            $"{p.Tasks.Count(t => t.Disposition == "PROPOSE")} stage candidate(s), " +
            $"{p.Tasks.Count(t => t.Disposition == "ALREADY_PRESENT")} already present; no FIFO witness assigned")
            .Prepend($"Account: keep {account?.Head ?? "UNAVAILABLE"}; propose Preparing only, not Activated")
            .ToArray();
        var repairs = new[]
        {
            "Before any authorized change, copy the exact account/project Git HEADs and immutable history to recoverable backups.",
            "Investigate each warning against original operation IDs, actors, receipts, session and source Git refs. Preserve raw evidence; repair only the smallest verified inconsistency.",
            "In a separately authorized transaction, write an inspectable repair note and original-ID receipt with a minimal Git diff, then use non-force compare-and-swap at the expected head.",
            "Re-read both clones and competing head/ref before each step. On conflict or lost acknowledgement, stop and inspect original receipt; never force-push or silently roll back.",
            "Keep legacy Manager and new Scheduler disabled from overlapping authority; activation, epoch flip, deployment and restart require separate explicit operator approval."
        };
        var summary = $"WQ-10 offline preview: {readiness}; {blockers} blocker(s), " +
            $"{findings.Count(f => f.Severity == "WARNING")} warning(s); " +
            $"{projects.Sum(p => p.Tasks.Count(t => t.Disposition == "PROPOSE"))} proposed task stage(s), " +
            $"{projects.Sum(p => p.Tasks.Count(t => t.Disposition == "ALREADY_PRESENT"))} already present. " +
            "No production migration or activation performed.";
        return new(1, "OFFLINE_READ_ONLY", readiness, "NOT_AUTHORIZED",
            account, projects.ToArray(), pending, jobs, browser, findings.ToArray(),
            planned, repairs, summary);
    }

    private static AccountInventory? InspectAccount(Snapshot snapshot, List<Finding> findings)
    {
        var tip = VerifySnapshot(snapshot, "account", findings);
        if (!Oid.IsMatch(snapshot.Head)) return null;
        var node = ReadJson(snapshot, "state.json", findings, "ACCOUNT_STATE_MISSING");
        if (node is null) return null;
        int epoch = Int(node, "epoch");
        var phase = Str(node, "migrationPhase");
        var tokens = Field(node, "tokens") as JsonArray;
        if (Int(node, "schemaVersion") != 1 || epoch < 1 || string.IsNullOrWhiteSpace(Str(node, "accountId"))
            || string.IsNullOrWhiteSpace(Str(node, "ledgerId")) || tokens is null)
            findings.Add(new("BLOCKER", "ACCOUNT_SCHEMA_INCOMPLETE", snapshot.Head,
                "Inspect original account state.json and restore missing authority fields"));
        var held = tokens?.Where(t => Str(t, "status") == "Held").ToArray() ?? [];
        var released = tokens?.Count(t => Str(t, "status") == "Released") ?? 0;
        if (held.Length > 0)
            findings.Add(new("BLOCKER", "CAPACITY_OCCUPIED", $"{held.Length} account token(s) held",
                "Reconcile original project admission/terminal receipts before considering release"));
        if (phase == "Activated")
            findings.Add(new("WARNING", "ACCOUNT_ALREADY_ACTIVATED", snapshot.Head,
                "Inspect actual epoch/protocol history; this tool never reactivates or rolls back"));
        return new(snapshot.Head, tip, epoch, phase, Int(node, "maxConcurrentAgents"),
            held.Length, released, held.Select(t => Str(t, "launchId")).Order(StringComparer.Ordinal).ToArray());
    }

    private static ProjectInventory InspectProject(Snapshot snapshot, List<Finding> findings)
    {
        var context = snapshot.Name;
        var tip = VerifySnapshot(snapshot, context, findings);
        var header = ReadJson(snapshot, "ledger.json", findings, "PROJECT_LEDGER_MISSING");
        var paths = ListPaths(snapshot, findings);
        var tasks = new List<LegacyTask>();
        var newQueue = ReadOptionalJson(snapshot, "records/work-queue/project.json", findings);
        var state = Field(newQueue, "state");
        var works = Field(state, "works") as JsonObject;
        if (works is not null)
            foreach (var coverageField in new[]
                     { "queueCovered", "graphCovered", "messageCovered", "sourceCovered" })
                if (Scalar(Field(state, coverageField)) != "true")
                    findings.Add(new("BLOCKER", "CANONICAL_INDEX_UNVERIFIED",
                        context + "/" + coverageField,
                        "Inspect exact producer/index high-water and canonical coverage receipts"));
        var messages = Field(state, "messages") as JsonObject;
        var launches = Field(state, "launches") as JsonObject;
        var migratedSubjects = new HashSet<string>(StringComparer.Ordinal);
        if (works is not null)
            foreach (var work in works)
            {
                var subject = Str(work.Value, "subjectId");
                if (subject.Length > 0) migratedSubjects.Add(subject);
            }
        long? originSourceHighWater = null;
        var originPaths = paths.Where(p =>
            p.StartsWith("records/work-queue/origins/", StringComparison.Ordinal)
            && p.EndsWith(".json", StringComparison.Ordinal)).ToArray();
        foreach (var path in originPaths)
        {
            var origin = ReadOptionalJson(snapshot, path, findings);
            if (origin is null) continue;
            var value = Long(origin, "sourceHighWater");
            if (value < 1)
                findings.Add(new("BLOCKER", "ORIGIN_HIGH_WATER_INVALID", context + "/" + path,
                    "Inspect original producer receipt and correct only with auditable CAS"));
            else originSourceHighWater = Math.Max(originSourceHighWater ?? value, value);
        }
        if (works?.Count > 0 && originPaths.Length == 0)
            findings.Add(new("BLOCKER", "ORIGIN_RECEIPTS_MISSING", context,
                "Locate immutable work-origin/producer receipts before treating queue as migrated"));
        var attemptsByTask = new Dictionary<string, List<JsonNode>>(StringComparer.Ordinal);
        var attemptsByPath = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in paths.Where(p => p.StartsWith("tasks/", StringComparison.Ordinal) &&
                     p.Contains("/attempts/", StringComparison.Ordinal) && p.EndsWith(".json", StringComparison.Ordinal)
                     && !p.Contains("/assignments/", StringComparison.Ordinal)
                     && !p.Contains("/events/", StringComparison.Ordinal)
                     && !p.Contains("/checkpoints/", StringComparison.Ordinal)))
        {
            var parts = path.Split('/');
            if (parts.Length != 4) continue;
            var attempt = ReadOptionalJson(snapshot, path, findings);
            if (attempt is null) continue;
            if (!attemptsByTask.TryGetValue(parts[1], out var values))
                attemptsByTask[parts[1]] = values = [];
            values.Add(attempt);
            if (!attemptsByPath.TryGetValue(parts[1], out var files))
                attemptsByPath[parts[1]] = files = [];
            files.Add(path);
        }
        foreach (var path in paths.Where(p => Regex.IsMatch(p, "^tasks/[^/]+/task[.]json$",
                     RegexOptions.CultureInvariant)).Order(StringComparer.Ordinal))
        {
            var task = ReadOptionalJson(snapshot, path, findings);
            if (task is null) continue;
            var taskId = Str(task, "taskId");
            if (taskId.Length == 0) taskId = path.Split('/')[1];
            var lifecycle = Str(task, "lifecycle");
            if (lifecycle is "Closed" or "Cancelled") continue;
            var attempts = attemptsByTask.GetValueOrDefault(taskId) ?? [];
            var active = attempts.Where(a => Str(a, "phase") is "Started" or "Queued" or "Blocked" or "Deferred").ToArray();
            var refs = attempts.SelectMany(a => new[] { Str(a, "sourceRef"), Str(a, "targetRef") })
                .Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var commits = attempts.SelectMany(a => new[] { Str(a, "baseCommit"), Str(a, "sourceCommit") })
                .Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var duplicateCount = works?.Count(w =>
                Str(w.Value, "subjectId") == taskId || Str(w.Value, "subjectId") == "task-" + taskId) ?? 0;
            var already = duplicateCount > 0 || migratedSubjects.Contains(taskId) || migratedSubjects.Contains("task-" + taskId);
            if (duplicateCount > 1)
                findings.Add(new("BLOCKER", "MULTIPLE_CANONICAL_STAGES", context + "/" + taskId,
                    "Inspect duplicate stages and exact origin receipts; do not auto-merge them"));
            if (active.Length > 0)
                findings.Add(new("BLOCKER", "LEGACY_ATTEMPT_ACTIVE", context + "/" + taskId,
                    "Settle active claims, source effects, checkpoints, and original owner before staging"));
            if (attempts.Any(a => Str(a, "phase") == "Started") && refs.Length == 0)
                findings.Add(new("BLOCKER", "SOURCE_LINEAGE_MISSING", context + "/" + taskId,
                    "Inspect the source worktree and immutable ref/commit, or mark RecoveryBlocked"));
            var proposedId = "legacy-" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(context + "\n" + taskId))).ToLowerInvariant()[..20];
            tasks.Add(new(taskId, Str(task, "title"), lifecycle, path,
                (attemptsByPath.GetValueOrDefault(taskId) ?? []).Order(StringComparer.Ordinal).ToArray(),
                refs, commits, proposedId, already ? "ALREADY_PRESENT" : "PROPOSE"));
        }

        var msgList = new List<Message>();
        if (messages is not null)
            foreach (var kv in messages.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var msg = kv.Value;
                var status = Case(Field(msg, "state"));
                var subject = Case(Field(msg, "subject"));
                if (status == "Pending")
                    msgList.Add(new(kv.Key, subject, status, Str(msg, "workId")));
            }
        var pendingRoles = msgList.Count(m => m.Subject == "Role");
        var pendingTasks = msgList.Count(m => m.Subject == "Task");
        if (pendingRoles + pendingTasks > 0)
            findings.Add(new("WARNING", "MESSAGES_PENDING", $"{context}: {pendingRoles} Role, {pendingTasks} Task",
                "Preserve exact message IDs and threads; map Role to work, Task to passive attention"));
        var launchList = new List<LaunchView>();
        if (launches is not null)
            foreach (var kv in launches.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var launch = kv.Value;
                launchList.Add(new(kv.Key, Str(launch, "roleId"),
                    Case(Field(launch, "state")), Str(launch, "accountTokenId")));
            }
        var activeLaunches = launches?.Count(kv =>
            Case(Field(kv.Value, "state")) is not ("Terminal" or "Completed" or "Cancelled" or "Stopped")) ?? 0;
        if (activeLaunches > 0)
            findings.Add(new("BLOCKER", "QUEUED_LAUNCH_ACTIVE", $"{context}: {activeLaunches} launch(es)",
                "Inspect one-shot dispatch, registration and terminal receipts; no duplicate dispatch"));
        var agents = new List<LegacyAgent>();
        foreach (var path in paths.Where(p => Regex.IsMatch(p, "^agents/[^/]+[.]json$")))
        {
            var agent = ReadOptionalJson(snapshot, path, findings);
            if (agent is null) continue;
            var id = Str(agent, "agentId");
            var execution = Str(agent, "execution");
            var assignedTask = Str(Field(agent, "currentAssignment"), "taskId");
            agents.Add(new(id, execution, assignedTask));
            if (execution == "Working")
                findings.Add(new("BLOCKER", "LEGACY_AGENT_WORKING", context + "/" + id,
                    "Reconcile current task/attempt and last session before cutting over"));
        }
        var reviews = new List<LegacyReview>();
        foreach (var path in paths.Where(p => p.EndsWith("/review.json", StringComparison.Ordinal)))
        {
            var review = ReadOptionalJson(snapshot, path, findings);
            if (review is null) continue;
            var id = Str(review, "reviewId");
            var status = Str(review, "status");
            var reviewer = Str(review, "reviewerId");
            var head = Str(review, "reviewedHeadOid");
            reviews.Add(new(id, Str(review, "taskId"), status, reviewer, head));
            if (status == "Claimed")
                findings.Add(new("BLOCKER", "CLAIMED_REVIEW", context + "/" + id,
                    "Settle the original review claim and frozen SHA before migration"));
            if (head.Length == 0 || !Oid.IsMatch(head))
                findings.Add(new("BLOCKER", "FROZEN_REVIEW_SHA_MISSING", context + "/" + id,
                    "Inspect accepted review source ref and immutable reviewed head"));
        }
        var integrations = new List<LegacyIntegration>();
        foreach (var path in paths.Where(p =>
                 Regex.IsMatch(p, "^tasks/[^/]+/integrations/[^/]+[.]json$")))
        {
            var integration = ReadOptionalJson(snapshot, path, findings);
            if (integration is null) continue;
            var id = Str(integration, "submissionId");
            var status = Str(integration, "status");
            integrations.Add(new(id, Str(integration, "taskId"), status,
                Str(integration, "submittedCommit"), Str(integration, "reviewId")));
            if (status != "Integrated")
                findings.Add(new("BLOCKER", "INTEGRATION_UNSETTLED", context + "/" + id + " " + status,
                    "Inspect original mutating Git job, target ancestry and exact submission receipt"));
        }
        var historicalReviews = paths.Count(p => p.EndsWith("/review.json", StringComparison.Ordinal));
        var historicalIntegrations = paths.Count(p => Regex.IsMatch(p, "^tasks/[^/]+/integrations/[^/]+[.]json$"));
        var historicalNotifications = paths.Count(p => p.StartsWith("notifications/", StringComparison.Ordinal));
        var historicalOperations = paths.Count(p => p.StartsWith("operations/", StringComparison.Ordinal));
        var historicalAgents = paths.Count(p => Regex.IsMatch(p, "^agents/[^/]+[.]json$"));
        var historicalChallenges = paths.Count(p => Regex.IsMatch(p, "^challenges/[^/]+/challenge[.]json$"));
        if (historicalNotifications > 0)
            findings.Add(new("WARNING", "LEGACY_ALERTS_PRESERVED", $"{context}: {historicalNotifications} alert record(s)",
                "Retain original agent-keyed recipients; do not redirect to reused Worker labels"));
        if (works is null)
            findings.Add(new("WARNING", "CANONICAL_QUEUE_NOT_PRESENT", context,
                "Preview historical tasks only; canonical queue, origin/high-water and FIFO remain unverified"));
        if (header is null || Str(header, "ledgerId").Length == 0 || Str(header, "projectId").Length == 0)
            findings.Add(new("BLOCKER", "PROJECT_IDENTITY_MISSING", context,
                "Recover canonical ledger identity and epoch before prepared-state consideration"));
        // A dry run never invents a server-witnessed FIFO timestamp or authorizes a worker.
        return new(context, snapshot.Head, tip, Int(header, "epoch"), Long(header, "nextSequence"),
            originSourceHighWater, Case(Field(state, "migration")), historicalOperations, historicalAgents,
            historicalReviews, historicalIntegrations, historicalNotifications, historicalChallenges,
            works?.Count ?? 0, launches?.Count ?? 0, tasks.ToArray(), msgList.ToArray(),
            agents.ToArray(), reviews.ToArray(), integrations.ToArray(), launchList.ToArray());
    }

    private static bool IsSettled(string value) => value is "Applied" or "Resolved" or "Settled";
    private static void CheckProof(bool? value, string code, string context, string action, List<Finding> findings)
    {
        if (value != true)
            findings.Add(new("BLOCKER", code, context + (value is null ? " (unknown)" : " (not proven)"), action));
    }

    private static string VerifySnapshot(Snapshot snapshot, string context, List<Finding> findings)
    {
        if (!Oid.IsMatch(snapshot.Head) || !RefName.IsMatch(snapshot.Ref))
        {
            findings.Add(new("BLOCKER", "INVALID_SNAPSHOT_ID", context,
                "Supply exact hex Git HEAD and full refs/heads/... name, not a mutable branch alias"));
            return "";
        }
        var kind = Git(snapshot.Repo, ["cat-file", "-t", snapshot.Head]);
        if (kind.ExitCode != 0 || kind.Output.Trim() != "commit")
        {
            findings.Add(new("BLOCKER", "PINNED_COMMIT_MISSING", context + "/" + snapshot.Head,
                "Fetch the approved immutable head into a disposable clone; do not infer from mutable branch name"));
            return "";
        }
        var tip = Git(snapshot.Repo, ["rev-parse", "--verify", snapshot.Ref + "^{commit}"]);
        if (tip.ExitCode != 0 || !Oid.IsMatch(tip.Output.Trim()))
        {
            findings.Add(new("BLOCKER", "REF_UNAVAILABLE", context + "/" + snapshot.Ref,
                "Independently inspect current Git ref; missing ref is not proof of safe state"));
            return "";
        }
        var observed = tip.Output.Trim();
        if (observed != snapshot.Head)
            findings.Add(new("BLOCKER", "REF_HEAD_CHANGED", $"{context}: pinned {snapshot.Head}, observed {observed}",
                "Competing clone/old writer changed head; inspect diff, original operation ID and rerun preflight"));
        return observed;
    }

    private static string[] ListPaths(Snapshot snapshot, List<Finding> findings)
    {
        if (!Oid.IsMatch(snapshot.Head)) return [];
        var listing = Git(snapshot.Repo, ["ls-tree", "-r", "--name-only", snapshot.Head]);
        if (listing.ExitCode != 0)
        {
            findings.Add(new("BLOCKER", "TREE_UNAVAILABLE", snapshot.Name,
                "Recover the exact pinned Git tree before inventory"));
            return [];
        }
        var paths = listing.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length > 10000)
        {
            findings.Add(new("BLOCKER", "INVENTORY_TOO_LARGE", snapshot.Name,
                "Split the offline evidence set into bounded reviewed snapshots"));
            return [];
        }
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    private static JsonNode? ReadJson(Snapshot snapshot, string path, List<Finding> findings, string missingCode)
    {
        var node = ReadOptionalJson(snapshot, path, findings);
        if (node is null)
            findings.Add(new("BLOCKER", missingCode, snapshot.Name + "/" + path,
                "Inspect this exact pinned Git path and reconcile original immutable state"));
        return node;
    }

    private static JsonNode? ReadOptionalJson(Snapshot snapshot, string path, List<Finding> findings)
    {
        if (!Oid.IsMatch(snapshot.Head) || path.Contains("..", StringComparison.Ordinal)) return null;
        var result = Git(snapshot.Repo, ["show", snapshot.Head + ":" + path]);
        if (result.ExitCode != 0) return null;
        try { return JsonNode.Parse(result.Output); }
        catch (JsonException ex)
        {
            findings.Add(new("BLOCKER", "MALFORMED_GIT_RECORD", snapshot.Name + "/" + path,
                "Preserve record and inspect JSON error: " + ex.Message));
            return null;
        }
    }

    private static (int ExitCode, string Output) Git(string repo, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repo);
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(psi) ?? throw new IOException("Failed to start Git");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException("Read-only Git inspection timed out");
        }
        error.GetAwaiter().GetResult();
        return (process.ExitCode, output.GetAwaiter().GetResult().TrimEnd());
    }

    private static JsonNode? Field(JsonNode? node, string key) =>
        node is JsonObject obj
            ? obj.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value
            : null;
    private static string Scalar(JsonNode? node)
    {
        if (node is null) return "";
        try { return node.GetValue<string>(); }
        catch (InvalidOperationException) { return node.ToJsonString(); }
    }
    private static string Str(JsonNode? node, string key) => Scalar(Field(node, key));
    private static int Int(JsonNode? node, string key) =>
        int.TryParse(Scalar(Field(node, key)), out var value) ? value : 0;
    private static long Long(JsonNode? node, string key) =>
        long.TryParse(Scalar(Field(node, key)), out var value) ? value : 0;
    private static string Case(JsonNode? node) =>
        node is JsonObject ? Str(node, "case") : Scalar(node);

    private static string Markdown(Report report)
    {
        var b = new StringBuilder();
        b.AppendLine("# WQ-10 offline migration preview");
        b.AppendLine();
        b.AppendLine($"**{report.Readiness}** — {report.Summary}");
        b.AppendLine();
        b.AppendLine("Production activation: **NOT AUTHORIZED**. No input Git ref, account state, or project state was changed.");
        b.AppendLine();
        b.AppendLine("## Pinned state and inert proposed state");
        foreach (var item in report.PreparedStatePreview) b.AppendLine("- " + item);
        b.AppendLine();
        b.AppendLine("## Findings");
        foreach (var f in report.Findings)
            b.AppendLine($"- **{f.Severity} {f.Code}**: {f.Context}. Next: {f.NextAction}.");
        if (report.Findings.Length == 0) b.AppendLine("- No documented blockers in supplied offline evidence. Re-verify live authorities before any authorization.");
        b.AppendLine();
        b.AppendLine("## Tasks and pending Role/Task messages");
        foreach (var p in report.Projects)
        {
            b.AppendLine($"### {p.Name} @ {p.Head}");
            b.AppendLine($"Ledger next sequence: {p.LedgerNextSequence}; maximum observed work-origin source high-water: {p.OriginSourceHighWater?.ToString() ?? "UNVERIFIED"} (not proof of complete coverage).");
            b.AppendLine($"Historical operation receipts: {p.HistoricalOperations}; agents: {p.HistoricalAgents}; reviews: {p.HistoricalReviews}; integrations: {p.HistoricalIntegrations}; alerts: {p.HistoricalNotifications}.");
            foreach (var task in p.Tasks)
                b.AppendLine($"- {task.TaskId} ({task.Lifecycle}): {task.Disposition}; proposed {task.ProposedWorkId}; source refs: {string.Join(", ", task.SourceRefs)}; commits: {string.Join(", ", task.SourceCommits)}");
            foreach (var m in p.Messages)
                b.AppendLine($"- Message {m.MessageId}: {m.Subject}, {m.State}, work {m.WorkId}");
            foreach (var a in p.Agents)
                b.AppendLine($"- Agent {a.AgentId}: {a.Execution}, assigned task {a.AssignmentTaskId}");
            foreach (var r in p.Reviews)
                b.AppendLine($"- Review {r.ReviewId}: {r.Status}, reviewer {r.ReviewerId}, frozen {r.ReviewedHeadOid}");
            foreach (var i in p.Integrations)
                b.AppendLine($"- Integration {i.SubmissionId}: {i.Status}, source {i.SubmittedCommit}, review {i.ReviewId}");
            foreach (var launch in p.Launches)
                b.AppendLine($"- Launch {launch.LaunchId}: {launch.RoleId}, {launch.State}, token {launch.AccountTokenId}");
        }
        b.AppendLine();
        b.AppendLine("## Authorized manual repair path (not executed)");
        foreach (var step in report.AuthorizedRepairProcedure) b.AppendLine("- " + step);
        return b.ToString();
    }
}
