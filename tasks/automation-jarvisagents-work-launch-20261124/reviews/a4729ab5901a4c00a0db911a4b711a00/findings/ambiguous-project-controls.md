# Ambiguous project model controls can authorize Send

Frozen source: a4729ab5901aec0020db911a4b711a00b8be293a, browser-scripts/chatgpt/launchAgent.js:174, 193-200, 485-513.

The task explicitly requires missing/ambiguous controls to be covered and the actual composer configuration verified. `picker()` takes the first matching model button without checking uniqueness, while `projectWorkLabel()` takes the first matching descendant. `surface()` accepts multiple visible model labels whenever any one is the Terra Work label. A page with one composer but two visible model controls (the first showing Terra Work/medium and another showing a conflicting selection), or with both Terra and a contradictory label in the selected picker, therefore passes the project Work proof and can click Send. Neither proof binds the chosen button to the composer. This is a concrete DOM-state counterexample; I did not observe it on the live page. The new tests model only one picker and one label, and do not cover this ambiguity.

Impact: a launch can be attributed to Work/Terra while a conflicting active surface exists, contrary to the fail-closed selection contract. Require an unambiguous, composer-associated picker and reject conflicting visible labels; add negative tests asserting zero sends for duplicate/conflicting controls. The exact remedy is for the author/adjudicator to choose.
