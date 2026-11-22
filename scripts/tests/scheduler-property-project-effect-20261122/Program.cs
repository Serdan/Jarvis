// Standalone independent .NET 11 Phase-A model; not a StageB implementation test.
// All provider/account state and Git clones in sibling Node tests are disposable.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

enum Outcome {
    Claimed, StaleProject, StaleAccount, ForeignCandidate, ClaimConflict,
    Started, AlreadyStarted, NotClaimed, ForeignOwner, VerifiedTerminal,
    VerifiedNoSend, UntrustedProof, NotTerminal, Released, AlreadyReleased,
    AlreadyApplied, OperationIdReused, ProjectMutated, AccountMutated, Observed
}
record Candidate(string Task, string Attempt, int Generation, string Lease,
    string Owner, string Ledger, int Epoch, string Reservation, string Effect,
    string Dispatch);
record Proof(string Marker, string Status, string Effect, string Session);
record Command(string Kind, string Op = "", Candidate? Candidate = null,
    int ExpectedProject = -1, int ExpectedAccount = -1,
    string Owner = "", string Token = "claim-a", string Effect = "effect-a",
    Proof? Proof = null);
sealed record State {
    public int ProjectVersion { get; init; }
    public int AccountVersion { get; init; } = 1;
    public Candidate Candidate { get; init; } = Program.Candidate;
    public bool Claimed { get; init; }
    public string Phase { get; init; } = "Idle";
    public int Sends { get; init; }
    public int Releases { get; init; }
    public bool ReservationActive { get; init; } = true;
    public bool Completed { get; init; }
    public string ProviderStatus { get; init; } = "Unknown";
    public string? Session { get; init; }
    public Dictionary<string, string> Operations { get; init; } = [];
}
static class Program {
    public static readonly Candidate Candidate =
        new("task-a", "attempt-a", 7, "refs/heads/immutable-source", "Worker A",
            "project-ledger-a", 2, "fake-reservation-a", "effect-a", "dispatch-a");
    static readonly Proof ValidTerminal = new("DISPOSABLE_PROVIDER_TERMINAL", "Terminal",
        "effect-a", "fake-session-1");
    static string Hash(Command cmd, bool omitTerminalProof = false) {
        string serialized = omitTerminalProof && cmd.Kind == "Terminal"
            ? JsonSerializer.Serialize(new {cmd.Kind, cmd.Op, cmd.Effect})
            : JsonSerializer.Serialize(cmd);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
    }
    static (State, Outcome) Move(State source, Command command, bool omitTerminalProof = false) {
        var s = source with { Operations = new(source.Operations) };
        if (command.Kind == "Observe" || command.Kind == "Restart")
            return (s, Outcome.Observed);
        if (command.Kind == "ProjectWrite")
            return (s with { ProjectVersion = s.ProjectVersion + 1 }, Outcome.ProjectMutated);
        if (command.Kind == "AccountWrite")
            return (s with { AccountVersion = s.AccountVersion + 1 }, Outcome.AccountMutated);
        string hash = Hash(command, omitTerminalProof);
        if (s.Operations.TryGetValue(command.Op, out var recorded))
            return (s, recorded == hash ? Outcome.AlreadyApplied : Outcome.OperationIdReused);
        (State, Outcome) Record(State updated, Outcome outcome) {
            updated.Operations[command.Op] = hash;
            return (updated, outcome);
        }
        if (command.Kind == "Claim") {
            if (command.ExpectedProject != s.ProjectVersion)
                return (s, Outcome.StaleProject);
            if (command.ExpectedAccount != s.AccountVersion || !s.ReservationActive)
                return (s, Outcome.StaleAccount);
            if (command.Candidate != s.Candidate)
                return (s, Outcome.ForeignCandidate);
            if (s.Claimed)
                return (s, Outcome.ClaimConflict);
            return Record(s with { Claimed = true, ProjectVersion = s.ProjectVersion + 1 }, Outcome.Claimed);
        }
        if (!s.Claimed)
            return (s, Outcome.NotClaimed);
        if (command.Token != "claim-a" || command.Effect != s.Candidate.Effect)
            return (s, Outcome.ForeignCandidate);
        if (command.Kind == "Start") {
            if (command.Owner != s.Candidate.Owner)
                return (s, Outcome.ForeignOwner);
            if (s.Phase != "Idle")
                return (s, Outcome.AlreadyStarted);
            if (command.ExpectedProject != s.ProjectVersion)
                return (s, Outcome.StaleProject);
            return Record(s with { Phase = "Dispatching", ProjectVersion = s.ProjectVersion + 1 },
                Outcome.Started);
        }
        if (command.Kind == "Terminal") {
            var proof = command.Proof;
            if (s.Phase != "Dispatching" || s.Sends != 1)
                return (s, Outcome.NotTerminal);
            if (proof != ValidTerminal || s.ProviderStatus != "Terminal"
                || s.Session != ValidTerminal.Session)
                return (s, Outcome.UntrustedProof);
            return Record(s with { Phase = "VerifiedTerminal", ProjectVersion = s.ProjectVersion + 1 },
                Outcome.VerifiedTerminal);
        }
        if (command.Kind == "NoSend") {
            var proof = command.Proof;
            if (s.Phase != "Dispatching" || s.Sends != 0 ||
                proof is null || proof.Marker != "VerifiedNoSend" ||
                proof.Status != "VerifiedNoProviderSession" || proof.Effect != s.Candidate.Effect)
                return (s, Outcome.UntrustedProof);
            return Record(s with { Phase = "VerifiedNoSend", ProjectVersion = s.ProjectVersion + 1 },
                Outcome.VerifiedNoSend);
        }
        if (command.Kind == "Release") {
            if (command.ExpectedAccount != s.AccountVersion)
                return (s, Outcome.StaleAccount);
            if (s.Releases != 0 || !s.ReservationActive)
                return (s, Outcome.AlreadyReleased);
            if (s.Phase is not ("VerifiedTerminal" or "VerifiedNoSend"))
                return (s, Outcome.NotTerminal);
            return Record(s with { AccountVersion = s.AccountVersion + 1,
                ReservationActive = false, Releases = 1, Completed = true }, Outcome.Released);
        }
        throw new InvalidOperationException("Unexpected command " + command.Kind);
    }
    static State SendAfterCommittedStart(State s, string status = "Active") {
        if (s.Phase != "Dispatching")
            throw new InvalidOperationException("Fake send before durable start");
        return s with { Sends = s.Sends + 1, Session = "fake-session-" + (s.Sends + 1),
            ProviderStatus = status };
    }
    static void Invariants(State s) {
        if (s.Sends > 1 || s.Releases > 1)
            throw new InvalidOperationException("Duplicate side effect");
        if (s.Releases != 0 && (s.ReservationActive ||
            s.Phase is not ("VerifiedTerminal" or "VerifiedNoSend")))
            throw new InvalidOperationException("False release");
        if (s.Sends > 0 && s.Phase == "VerifiedNoSend")
            throw new InvalidOperationException("False no-send");
        if (s.Completed != (s.Releases == 1))
            throw new InvalidOperationException("False completion");
    }
    static State Do(State s, Command cmd, Outcome expected) {
        var (next, got) = Move(s, cmd);
        if (expected != got)
            throw new InvalidOperationException($"Expected {expected}, got {got} for {cmd}");
        Invariants(next);
        return next;
    }
    static Command Claim(string op = "claim", Candidate? candidate = null,
        int expectedProject = 0, int expectedAccount = 1) =>
        new("Claim", op, candidate ?? Candidate, expectedProject, expectedAccount);
    static Command Start(string op = "start", string owner = "Worker A", int expectedProject = 1) =>
        new("Start", op, ExpectedProject: expectedProject, Owner: owner);
    static Command Terminal(string op = "term", Proof? proof = null) =>
        new("Terminal", op, Proof: proof ?? ValidTerminal);
    static Command Release(string op = "release", int account = 1) =>
        new("Release", op, ExpectedAccount: account);
    static void Assert(bool condition, string detail) {
        if (!condition) throw new InvalidOperationException(detail);
    }
    static int tests;
    static void Test(string name, Action action) {
        action(); Console.WriteLine("PASS " + name); tests++;
    }
    static uint Next(ref uint x) {
        x ^= x << 13; x ^= x >> 17; x ^= x << 5; return x;
    }
    static void Main() {
        Test("stale head and foreign lease do not claim", () => {
            Assert(Move(new(), Claim(expectedProject: 2)).Item2 == Outcome.StaleProject, "head");
            Assert(Move(new(), Claim(candidate: Candidate with { Lease = "foreign" })).Item2 ==
                Outcome.ForeignCandidate, "lease");
            Assert(Move(new(), Claim(expectedAccount: 0)).Item2 == Outcome.StaleAccount, "account");
        });
        Test("two workers same attempt cannot both start", () => {
            var s = Do(new(), Claim(), Outcome.Claimed);
            s = Do(s, Start(), Outcome.Started);
            Assert(Move(s, Start("other", "Worker B")).Item2 == Outcome.ForeignOwner, "owner");
            Assert(Move(s, Start("retry")).Item2 == Outcome.AlreadyStarted, "one-start");
        });
        Test("uncertain ACK blocks release and re-send", () => {
            var s = Do(Do(new(), Claim(), Outcome.Claimed), Start(), Outcome.Started);
            s = SendAfterCommittedStart(s);
            Assert(Move(s, Start("again")).Item2 == Outcome.AlreadyStarted, "duplicate");
            Assert(Move(s, Release()).Item2 == Outcome.NotTerminal, "Unknown release");
            Assert(Move(s, Terminal()).Item2 == Outcome.UntrustedProof, "forged terminal");
            Invariants(s);
        });
        Test("terminal exact replay versus same-ID changed proof", () => {
            var s = SendAfterCommittedStart(Do(Do(new(), Claim(), Outcome.Claimed),
                Start(), Outcome.Started), "Terminal");
            s = Do(s, Terminal(), Outcome.VerifiedTerminal);
            Assert(Move(s, Terminal()).Item2 == Outcome.AlreadyApplied, "same replay");
            Assert(Move(s, Terminal(proof: ValidTerminal with { Status = "Unknown" })).Item2 ==
                Outcome.OperationIdReused, "changed status");
            Assert(Move(s, Terminal(proof: ValidTerminal with { Session = "foreign" })).Item2 ==
                Outcome.OperationIdReused, "changed session");
        });
        Test("negative oracle mutant omits terminal proof hash", () => {
            var s = SendAfterCommittedStart(Do(Do(new(), Claim(), Outcome.Claimed),
                Start(), Outcome.Started), "Terminal");
            var (accepted, code) = Move(s, Terminal(), omitTerminalProof: true);
            Assert(code == Outcome.VerifiedTerminal, "mutant source");
            Assert(Move(accepted, Terminal(proof: ValidTerminal with { Status = "Unknown" }),
                omitTerminalProof: true).Item2 == Outcome.AlreadyApplied, "mutant not exposed");
        });
        Test("release exactly once with verified provider", () => {
            var s = SendAfterCommittedStart(Do(Do(new(), Claim(), Outcome.Claimed),
                Start(), Outcome.Started), "Terminal");
            s = Do(s, Terminal(), Outcome.VerifiedTerminal);
            s = Do(s, Release(), Outcome.Released);
            Assert(Move(s, Release("retry", 2)).Item2 == Outcome.AlreadyReleased, "repeat release");
            Assert(s.Releases == 1 && !s.ReservationActive, "account released once");
        });
        Test("two independently matching no-send proofs required", () => {
            var s = Do(Do(new(), Claim(), Outcome.Claimed), Start(), Outcome.Started);
            var valid = new Proof("VerifiedNoSend", "VerifiedNoProviderSession", "effect-a", "");
            Assert(Move(s, new("NoSend", "bad", Proof: valid with { Status = "Unknown" })).Item2
                == Outcome.UntrustedProof, "Unknown no-send");
            s = Do(s, new("NoSend", "nosend", Proof: valid), Outcome.VerifiedNoSend);
            s = Do(s, Release(), Outcome.Released);
            Assert(s.Sends == 0 && s.Releases == 1, "prestart-negative");
        });
        Test("adversarial seeded 512 x 48 typed traces", () => {
            for (int seed = 1; seed <= 512; seed++) {
                uint rng = (uint)seed * 2654435761u;
                State s = new();
                for (int index = 0; index < 48; index++) {
                    int draw = (int)(Next(ref rng) % 12);
                    string op = $"seed-{seed}-step-{index}";
                    Command cmd = draw switch {
                        0 => Claim(op), 1 => Claim(op, expectedProject: -1),
                        2 => Claim(op, Candidate with { Generation = 6 }),
                        3 => Start(op), 4 => Start(op, owner: "Foreign Worker"),
                        5 => Terminal(op), 6 => Terminal(op,
                            ValidTerminal with { Status = "Unknown" }),
                        7 => Release(op), 8 => new("ProjectWrite"),
                        9 => new("AccountWrite"), 10 => new("Observe"),
                        _ => new("Restart")
                    };
                    (s, _) = Move(s, cmd);
                    Invariants(s);
                }
            }
        });
        Console.WriteLine($"PASS {tests}/{tests} independent .NET11 Release model checks");
    }
}
