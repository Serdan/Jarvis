module Client.DisposableProjectCapacityWitness

open System
open Client.LedgerStore

// This unregistered preparation cannot certify completeness. A caller invokes
// it only inside the transaction callback and must discard it on every retry.
type MissingProducer =
    | KnownNewWriterCoverage
    | ProjectCapacityPolicy
    | CompleteInventoryAndGeneration
    | ReservationAndEffectOccupancy
    | TrustedObservationTime

type Hold =
    | IdentityMismatch
    | CapacityEvidenceMissing of project: Guid * ledger: Guid * epoch: int * head: string * missing: MissingProducer list

let inspect (binding: LedgerBinding) (snapshot: LedgerSnapshot) =
    let header = snapshot.Header
    let mutable project = Guid.Empty
    let mutable ledger = Guid.Empty
    if isNull (box header) || binding.ProjectId = Guid.Empty || binding.LedgerId = Guid.Empty
       || binding.Epoch < 1 || String.IsNullOrWhiteSpace snapshot.Commit
       || not (Guid.TryParse(header.ProjectId, &project))
       || not (Guid.TryParse(header.LedgerId, &ledger))
       || project <> binding.ProjectId || ledger <> binding.LedgerId
       || header.Epoch <> binding.Epoch then
        IdentityMismatch
    else
        CapacityEvidenceMissing (project, ledger, binding.Epoch, snapshot.Commit,
            [ KnownNewWriterCoverage; ProjectCapacityPolicy; CompleteInventoryAndGeneration
              ReservationAndEffectOccupancy; TrustedObservationTime ])
