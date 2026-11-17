# Changes requested — account-global AGENTS.md

Independent source review of frozen `40c6a9f3373a96007e6b2f8a88349fe4155d70e7` against base `35f675cc1564710d10fdb2f50a03edff39368a28` and canonical task contract. Source-only inspection; author reports Release Server.Tests 498/498, **not independently rerun**.

**P1 blocking:** `attachAccountGlobal` returns unchanged result for `PendingJob`, while the first agent inbox may return a pending asynchronous job whose completion is retrieved through `get_job_result`. An agent can therefore begin its assignment without global policy. See `findings/pending-job-instruction-omission.md`. Fix end-to-end first-delivery behavior and add a pending-job regression test.

Other source mechanisms appear coherent: principal-derived hashed paths, scoped explicit operations, private UTF-8 files, bounded size, startup snapshot, separate missing/unavailable, in-process CAS, atomic rename, and precedence. No deployment or merge authorized.