# Finding 1 — Claimed-stage generation is replaced by stage generation (required correction)

**Severity:** Major correctness / provenance.  
**Frozen source:** `ce66e7a1344768086f06c590857603f93c0bd81e`  
**Location:** `Client/Modules/WorkStatefulTransport.fs:78-115`, especially lines 92-96; rendered by `Client/Modules/WorkStatefulProjection.fs:190-205`.

The persisted `WorkReadStage` carries distinct `StageGeneration` and `ClaimGeneration`. The `Claimed` branch constructs:

```fsharp
{ LaunchId = row.ClaimLaunchId.Value
  Generation = int row.StageGeneration
  Execution = Evidence.Unknown ... }
```

This ignores `row.ClaimGeneration`. A stage with stage generation 4 and claim generation 7 is displayed to the operator as **"claim generation 4"**. More seriously, a `Claimed` row with claim generation zero/missing provenance is displayed as an apparently valid claimed execution whenever the stage generation is positive. No current H1a client test covers a claimed stage with differing generations.

The Work migration contract requires exact generation and claim provenance; the dashboard must not silently substitute an unrelated field. Although H1a intentionally exposes no mutation or live launch endpoint, false claim-generation evidence is unacceptable for a recovery-facing read model.

**Required:** Guard claimed records on a valid, positive, representable `ClaimGeneration`; project `Claim.Generation` from that field while retaining `WorkStage.Generation` from `StageGeneration`. Unverifiable claims must remain explicitly UNKNOWN/omitted with a diagnostic. Add negative-space tests with unequal generations and zero/invalid claim generation. Do not infer execution liveness.