# P1 — Capacity admission counts unresolved external-effect work as matchable existing-launch credit

**Frozen revision:** `a8e8639b16596c2cbcbe93ee21e94f88496ff8af`
**Source:** `Server/WorkSchedulerSqlite.fs:289-319, 398-418, 489-503`

`matchExisting` builds a feasible-demand matching from all `projection.Eligible` candidates with compatible queues (lines 292–298). It does *not* exclude stage records whose `ExternalEffect` is `Prepared` or `Unknown`. However, both new-reservation selection (lines 400–403) and actual claim selection (lines 489–495) explicitly require `settled`. Thus the same stage is treated as a valid credit while preventing the reserved execution from ever claiming it.

**Reproduction / missing negative test:** One project, shared capacity 2; an already reserved review-only launch holds the review demand `R` whose stage's durable `ExternalEffect=Unknown`. A settled implementation demand `I` is the only actually claimable stage; a generalist role can execute `I`. `reserve` sees `R` in `matchExisting`, concludes the existing reviewer is still matchable, and grants the second reservation for `I`. Afterwards the review-only launch cannot claim `R` (the `claim` settled check rejects it) and cannot do `I`; it consumes capacity without feasible runnable work. A `Prepared` row is likewise counted. A counterexample with a missing credit but *settled* stage is already guarded; unresolved effect is the omitted case.

**Impact:** The E0 admission invariant 'do not strand already-admitted specialist executions' is not satisfied, and capacity can be exhausted by incorrectly protected ghost demand. This is especially relevant during one-shot effect reconciliation, when a stage can remain `Unknown` indefinitely pending evidence.

**Required correction:** Apply identical effect/claimability gates to the existing-demand matching set and the new admission/claim selection sets. Add a regression covering a specialist reservation with unresolved-only compatible demand plus a competing settled generalist demand; reserve must not consume the remaining slot on the assumption that the specialist's demand is feasible. Preserve the fail-closed no-retry interpretation of an `Unknown` effect.
