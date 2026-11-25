module Client.ProjectWorkingCount

open System
open System.Text
open FsToolkit.ErrorHandling
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.RoleInstructions

let private invalid message = Error(InvalidBinding ("Working-count cap: " + message))

// Agent records have canonical base64url names; challenge subscription records
// share the directory but are not agents.
let private agentId (path: string) =
    try
        let key = path.Substring(7, path.Length - 7 - 5)
        let encoded = key.Replace('-', '+').Replace('_', '/')
        let id = UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')))
        let canonical = Convert.ToBase64String(Encoding.UTF8.GetBytes id).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        if key = canonical && not (String.IsNullOrWhiteSpace id) then Ok id
        else invalid "noncanonical agent key"
    with _ -> invalid "malformed agent key"

// Only scheduled callers use this check. The limits are the successfully
// decoded optional policy, NOT a fallback on policy read failure.
// Ok true = held by capacity; Ok false = no capacity hold (not permission).
let isHeldAt (binding: LedgerBinding) (snapshot: LedgerSnapshot)
             (targetRole: string) (total: int option) (roles: Map<string, int>) =
    if not (Option.forall (fun limit -> limit > 0) total)
       || isNull (box roles)
       || not (Map.forall (fun role limit -> not (String.IsNullOrWhiteSpace role) && limit > 0) roles) then
        invalid "invalid enabled limit"
    elif not (Map.isEmpty roles) && String.IsNullOrWhiteSpace targetRole then
        invalid "missing target role"
    else
        let roleLimit = Map.tryFind targetRole roles
        if Option.isNone total && Option.isNone roleLimit then Ok false
        elif snapshot.Header.ProjectId <> string binding.ProjectId
             || snapshot.Header.LedgerId <> string binding.LedgerId
             || snapshot.Header.Epoch <> binding.Epoch then
            invalid "snapshot binding mismatch"
        else result {
            let! paths = LedgerStore.listAt binding snapshot "agents"
            let mutable working = 0
            let mutable matching = 0
            for path in paths do
                if not (path.EndsWith(".challenge-subscription.json", StringComparison.Ordinal)) then
                    let! id = agentId path
                    let! agent = ReviewWorkflow.getAgentWorkingTurnAt binding snapshot id
                    if agent.AgentId <> id then
                        return! invalid "agent record identity mismatch"
                    if agent.State = "Working" then
                        working <- working + 1
                        if Option.isSome roleLimit then
                            let! membership = RoleInstructions.resolveAtKnownAgent binding snapshot id
                            if List.contains targetRole membership.RoleIds then
                                matching <- matching + 1
            return (Option.exists (fun limit -> working >= limit) total
                    || Option.exists (fun limit -> matching >= limit) roleLimit)
        }
