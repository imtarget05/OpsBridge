# ADR-018: OpsBridge Migration and Legacy Baseline Strategy

- **Status**: Accepted
- **Date**: 2026-10-20

## Context

OpsBridge owns its own PostgreSQL database (`OpsBridgeDbContext`) and, since
`27a1dd4`, its own EF Core migration: `20261020063024_InitialOpsBridgeSchema`, a
pure-DDL migration that creates 19 domain tables, 19 primary keys and 7 foreign keys.
It carries no `HasData` seed, no `migrationBuilder.Sql`, no `defaultValue`, no
computed column, no sequence and no provider-specific annotation.

Two problems follow from that state.

**1. Nothing applies the migration.** `src/OpsBridge.Infrastructure/DependencyInjection.cs`
registered the `DbContext` and left the schema to whoever created it. The integration
fixture (`tests/OpsBridge.IntegrationTests/OpsBridgeFixture.cs`) calls
`MigrateAsync()` explicitly, so the tests were green while no deployed environment had
a defined, repeatable path from an empty database to a migrated one.

**2. Databases created by `EnsureCreatedAsync()` cannot be migrated.** `EnsureCreated`
creates the tables but no `__EFMigrationsHistory` table. A later `MigrateAsync()`
therefore believes nothing has been applied and issues `CREATE TABLE` for tables that
already exist, which PostgreSQL rejects with `42P07 duplicate_table`. Microsoft states
that `EnsureCreated` "can not be used with migrations", and EF Core ships no command
that adopts such a database.

An independent `pg_dump --schema-only` comparison established that a database created
by `EnsureCreatedAsync()` and one created by the migration differ **only** by EF's
`__EFMigrationsHistory` table. That is what makes a baseline possible at all — but it
also makes a *blind* baseline dangerous: writing a history row without comparing
schemas would tell EF "the schema is at head" even when it is not, and every later
migration would then be applied on top of an unknown starting point.

## Decision

### 1. Migrations are applied by a dedicated deployment step

`OpsBridge.Api --migrate` applies pending migrations and exits. It is the only
supported way to change the OpsBridge schema in a deployed environment, and it runs
with a **separate DDL-capable identity**; the runtime identity holds DML rights only
(`SELECT`/`INSERT`/`UPDATE`/`DELETE`) and can never change the schema.

The branch is placed **before the JWT validation block** in `src/OpsBridge.Api/Program.cs`
and before `builder.Build()`. A schema step must depend on PostgreSQL and nothing else:
the JWT block throws when `Jwt:SigningKey` is missing, shorter than 32 bytes, or still
the built-in development key, and a key-less production migration job would otherwise
refuse to start even though it has everything it needs. The logger is created directly
with `LoggerFactory.Create(b => b.AddSimpleConsole())` rather than through a throwaway
service provider (ASP0000), because the branch runs before the host exists.

The connection string is resolved by `OpsBridgeConnectionStringResolver`:

| Path | `ConnectionStrings:OpsBridgeDb` | `ConnectionStrings:DefaultConnection` | localhost fallback |
|---|---|---|---|
| `ResolveForApplication` (runtime) | used | used | **allowed** (see the note below) |
| `ResolveForMigration` (`--migrate`, `--baseline-legacy`) | used | used | **refused** |

The migration path refuses to fall back to `localhost`, because a schema operation that
silently targets the developer's local database reports success while the intended
target stays untouched — a data-loss class of bug that is invisible from the exit code.

The fallback literal itself is unchanged —
`"Host=localhost;Port=5432;Database=OpsBridgeDb;Username=postgres;Password=postgres"` —
but the **null-versus-blank semantics did change**. The original registration used `??`,
which only falls through on `null`: a *present but blank* `ConnectionStrings:OpsBridgeDb`
used to produce an empty connection string. The resolver treats a blank value as "not
configured", so it now falls through to `ConnectionStrings:DefaultConnection` and then to
localhost. That is a deliberate improvement (an empty connection string fails later and
far from its cause), and it is recorded here rather than described as "unchanged".

### 2. Runtime auto-migration is opt-in, local-only, and refused in Production

`OpsBridge:AutoMigrate` (bool, default **false**). The default keeps every existing
behaviour unchanged, including the integration fixture, which migrates explicitly.

* **Development / Test** → the migration runs at startup (after `builder.Build()`) and
  the application **fails closed**: a failed migration rethrows and the process does
  not start.
* **Production, and any environment that is neither Development nor Test** → the
  application refuses to start with an `InvalidOperationException` stating that
  production schema changes belong to the dedicated `--migrate` step with a
  DDL-capable identity. Refusing in Staging as well is deliberate: `AutoMigrate` is a
  local convenience, and letting it through in a non-local environment would turn
  every rollout into a schema event.

### 3. No custom advisory lock — EF Core's built-in migration locking is used

EF Core 9 introduced built-in migration locking; this repository pins
`Microsoft.EntityFrameworkCore 10.0.12` and `Npgsql.EntityFrameworkCore.PostgreSQL`
10.0.3, where the lock is on by default. Npgsql implements it as
`LOCK TABLE "__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE` acquired **inside the
migration transaction**, so it is released on commit, on rollback, on failure and on
process death, and it is re-acquired for every migration. Microsoft documents turning
it off as unsupported.

Therefore this ADR adds **no** `pg_advisory_lock` / `pg_try_advisory_lock`:

* a session-level advisory lock is unsafe under Npgsql pooling — `DISCARD ALL` is
  deferred, so the lock can outlive the logical scope it was taken in
  (npgsql/npgsql#5592);
* it would be a second lock in front of a lock that already exists, with a different
  failure mode and no additional guarantee.

`MigrateAsync` is likewise **not** wrapped in an explicit user transaction: EF Core 9+
throws `MigrationsUserTransactionWarning`, and EF already runs each migration inside
its own transaction.

What *is* configured is the Npgsql **command timeout**, raised from the 30 second
default to a configurable **120 seconds** (`OpsBridge:MigrationCommandTimeoutSeconds`).
The timeout applies to the *first* statement of a run — reading the pending migrations —
which is where a second instance waits for EF's migration lock. Raising the timeout
therefore **extends** the wait a second instance is allowed to make before the wait is
declared a failure; it does not bound it. With the default of 120 seconds, a legitimate
wait during a rolling deployment is not mistaken for a hung command. A wait that exceeds
the configured timeout is a failure with exit code 1, and the operator message names the
timeout and the configuration key.

### 4. The legacy baseline is verified, not blind

`OpsBridge.Api --baseline-legacy` adopts an `EnsureCreated`-shaped database into EF's
migration history. The order of operations is fixed:

1. If `__EFMigrationsHistory` exists **and contains rows**, the database is already
   managed — and it is **verified, not blessed** (see "the already-managed path" below).
2. Capture per-table row counts for the target's user tables (the data fingerprint).
3. Create a **scratch database** on the same server, apply the real migration to it,
   and compare the scratch schema against the target schema. The first pass compares
   domain objects only, because the history table exists in the scratch database at
   that moment and not yet in the target.
4. If **any** difference exists → write nothing, log every difference, exit **2**
   (drift detected). The tool never repairs, never drops and never modifies data.
5. If there are **zero** differences → create EF's history table with EF's exact shape
   and insert one baseline row **per migration** the scratch database recorded.
   `ProductVersion` is the value EF itself wrote into the scratch database — the real
   EF runtime value, read at runtime, never a literal; the EF assembly's
   `AssemblyInformationalVersionAttribute` is read as a cross-check and logged.
6. **Re-verify** after the write, this time *including* `__EFMigrationsHistory`. The
   result must be zero differences; anything else is a hard failure (exit **1**).
7. Capture the row counts again. They must be identical to the pre-write counts; any
   difference is a hard failure.

Steps 5–7 happen inside **one explicit transaction**: the `CREATE TABLE`, the
`INSERT`s, the post-write verification and the row-count comparison are one unit of
work. The transaction is committed only when the verification reports zero differences
and the row counts are unchanged; otherwise it is rolled back. A target can therefore
never be left carrying history rows for a schema that was not verified — the
half-written target that an earlier, non-transactional version of this tool could
produce is no longer reachable. (This is direct SQL, not `MigrateAsync`; EF's
prohibition on wrapping `MigrateAsync` in a user transaction does not apply here.)

#### What the comparison covers — and what it does not

The comparison covers, **in the `public` schema only**:

* tables (the set of tables, and the presence of unexpected ones),
* columns (name, data type, nullability, default),
* primary keys,
* unique constraints,
* indexes,
* foreign keys.

It deliberately covers **nothing else**: views, sequences, enums, functions, triggers,
materialised views, extensions and every non-`public` schema are **not** compared. They
are not owned by EF's migrations, so a difference in them is not evidence of drift.
The success message therefore says that the target "matches the migration schema for
every compared object class" — not that it matches "exactly".

#### The already-managed path

A non-empty history table is **not** proof of a healthy database, so the tool does not
short-circuit to success. It:

1. compares the recorded migration ids against the migrations **this build compiled**.
   An id the build does not know is *unknown state* — it cannot be verified, and it is
   refused with **exit 2** and a diagnostic that names the id;
2. requires the recorded ids to be a **prefix** of the compiled migrations (a history
   that skips or reorders a migration cannot be reproduced and is refused with exit 2);
3. creates a scratch database, migrates it **up to that same migration**, and compares
   the target against it. A difference is drift → **exit 2**;
4. on a clean comparison, reports success with **exit 0**, writes nothing, and reports
   how many migrations remain pending for the normal `--migrate` path.

The outcome carries a `VerificationRan` flag, so a "successful" baseline always says
whether the verification actually ran.

#### Privileges, and the scratch database

The baseline identity needs **`CREATEDB`** on the target server: the scratch database is
the only way to obtain an authoritative "what does the migration produce" schema
without guessing. If `CREATE DATABASE` is refused, the tool reports the refusal
clearly and **aborts without writing anything**. It never falls back to an unverified
baseline.

The scratch database is dropped in every path, including the failure paths, with
`DROP DATABASE … WITH (FORCE)`. **`WITH (FORCE)` requires PostgreSQL 13 or newer**,
which is therefore the minimum supported server version for the baseline tool; the
`FORCE` clause is what lets the drop succeed while a stale session is still attached.
A **failed drop is not swallowed**: it is logged as a warning, reported in the outcome,
and turns the run into a **failure** (exit 1). A leftover scratch database is not a
clean outcome, and the operator is told which database to remove.

#### What the tool touches in the target

The statements executed against the target are:

* `CREATE TABLE "__EFMigrationsHistory"` — only when the table does not exist yet,
* **one `INSERT` per recorded migration** (not one per run: with several migrations
  there is one insert each),
* the read-only `SELECT COUNT(*)` calls that produce the row-count fingerprint, one per
  user table, before and after the write,
* the read-only catalog queries (`pg_class`, `pg_attribute`, `pg_constraint`,
  `pg_index`) that produce the schema comparison.

No business table is ever written, dropped or altered.

### 5. Exit-code contract

| Code | Meaning |
|---|---|
| `0` | Success, including "the schema was already at head" and "the database was already managed and verified" |
| `1` | Failure (migration error, verification failure, missing configuration, refused `CREATE DATABASE`, failed scratch-database drop) |
| `2` | Drift detected, or unknown migration state; the baseline refused to adopt the database |

A re-run must succeed, not error: deployment steps are retried and re-run on every
release, so "already at head" is a success.

**The contract is enforced, not documented.** Nothing may escape the entrypoint:

* `OpsBridgeMigrationRunner.RunAsync` wraps its whole body — the pending-migration
  read, the migration itself and the history read — so *every* failure (wrong
  password, unreachable host, command timeout, cancellation) becomes
  `ExitCode = 1` with a one-line operator message instead of an unhandled exception.
  A cancellation is caught explicitly and reported as a cancellation; it is still a
  non-zero exit, because the schema was not brought to head.
* `OpsBridgeLegacyBaseline.AdoptAsync` does the same, and additionally reports a
  scratch database it could not drop as a failure.
* `src/OpsBridge.Api/Program.cs` wraps both entrypoints again, so that even a failure
  *outside* the two tools (a configuration error, an unexpected exception) leaves the
  process with a contract exit code. The process never dies with the 134 an unhandled
  exception produces.
* **Stack traces are shown selectively, not never.** An environment-level failure
  (wrong password, unreachable host, command timeout, cancellation) is logged as a
  single line that says what to fix, because a stack trace buries it. An unexpected
  or schema-level failure keeps its stack trace, because that is where the
  diagnostic value is. Both entrypoints classify the failure the same way.
* An external `SIGTERM` (a deployment job being deleted) terminates the process
  with the conventional `143` (128 + SIGTERM) rather than the contract's `1`. That
  is a deliberate consequence of not translating POSIX signals into a managed
  cancellation; `143` is the code an orchestrator already understands as "killed".
  An in-process cancellation returns `1`.

### 6. What "applied" means in a concurrent run

Five instances may start against the same database at the same time. EF's migration
lock makes exactly one of them do the work; the others find the schema at head. The
outcome therefore separates three different things, and the log never claims a process
applied a DDL it did not apply:

| Field | Meaning |
|---|---|
| `AlreadyAtHead` | **This run** applied no migration — either nothing was pending when it started, or another instance had already applied the pending migrations before this run took the lock. |
| `AppliedMigrations` | The migrations **this process** applied. EF exposes this as its own per-run result (one `Applying migration '<id>'` log line per migration it executes), so it is observed, not inferred. |
| `NewlyRecordedMigrations` | The migrations that became present in the history table **during this run**. Under concurrency this can be larger than `AppliedMigrations`: a migration another instance applied is in the history table too. It is not a claim about who applied it. |
| `RecordedMigrations` | Everything in the history table when the outcome was produced. On failure this is what **is** committed, which is not the same as what this run applied. |

## Relation to ADR-006

ADR-006 ("Database Migration Strategy (Migrations over EnsureCreated)") banned
`EnsureCreated` from runtime code for the **FlashSale** context and chose
`MigrateAsync` at runtime for local runs with a separate migration step reserved for
production. This ADR **extends** that decision to the **OpsBridge** context: the same
migrations-over-EnsureCreated rule, the same "runtime migrates locally, a dedicated
step migrates in production" split, and the same ban on `EnsureCreated` in runtime code.

This ADR **does not copy** ADR-006's concurrency reasoning. ADR-006 was written when
EF Core had no migration locking and its text notes that concurrent `MigrateAsync` is
"serialized by Postgres via the `__EFMigrationsHistory` insert" — a weaker guarantee
than a lock. OpsBridge runs on EF Core 10, where the guarantee is an explicit,
transaction-scoped `LOCK TABLE`. The OpsBridge decision therefore rests on EF's
built-in lock and adds no advisory lock of its own. Where the two disagree, this ADR
is the newer statement and applies to OpsBridge; the FlashSale context is not changed
by it.

## Alternatives considered

1. **Idempotent `Up()` guards** (wrap every `CREATE TABLE` in `IF NOT EXISTS`). Rejected:
   `IF NOT EXISTS` makes a *partial* schema silently accepted — a table with the wrong
   columns is not fixed by a guard, and the guard hides the difference instead of
   reporting it. The baseline compares schemas, so drift is reported, not absorbed.
2. **Blind history insert** (write the `__EFMigrationsHistory` row without comparing
   schemas). Rejected: it is indistinguishable from a correct baseline at the moment it
   runs and poisons every later migration. An unverified baseline is a lie told to EF.
3. **Rebuild-and-restore** (create a fresh database with the migration, copy the data
   over). Rejected: it needs a maintenance window, doubles the storage, breaks foreign
   keys and sequences during the copy, and is a much larger blast radius than writing
   one history row. It remains the right answer when the schema *does* differ, which is
   exactly why the tool exits with code `2` and prints the diff instead of guessing.
4. **Custom `pg_advisory_lock` around `MigrateAsync`.** Rejected: redundant with EF
   Core 9+'s built-in lock, unsupported to disable EF's lock, and unsafe under Npgsql
   pooling for session-level advisory locks.
5. **Auto-migration everywhere (including Production).** Rejected: it forces the
   runtime identity to hold DDL rights, and makes a rollout a schema event. Production
   schema changes go through the dedicated step.

## Consequences

* `OpsBridge.Api --migrate` is the supported schema-change path for OpsBridge; it is
  idempotent, returns a documented exit code, and **never throws out of the
  entrypoint**: the runner, the baseline tool and `Program.cs` each convert every
  failure into the exit-code contract (see section 5).
* `OpsBridge.Api --baseline-legacy` can adopt a legacy database **only** when the
  schema matches the migration's for every compared object class; otherwise it refuses
  and prints the differences. It requires **`CREATEDB`** on the target server, a
  PostgreSQL server of **version 13 or newer** (for `DROP DATABASE … WITH (FORCE)`),
  and it writes the history in a single transaction that is rolled back on any
  verification failure.
* The runtime identity needs only DML rights in Production; the DDL-capable identity
  is needed only by the deployment step.
* `OpsBridge:AutoMigrate` stays off by default. When switched on outside
  Development/Test the application refuses to start.
* The migration path has no localhost fallback, so the deployment step cannot
  accidentally migrate the wrong database.
* A failed migration leaves no history row (EF writes the history row in the same
  transaction as the migration), and EF Core 10 commits per migration rather than
  all-or-nothing across migrations.
* Two new configuration keys exist: `OpsBridge:AutoMigrate` (bool, default `false`)
  and `OpsBridge:MigrationCommandTimeoutSeconds` (int, default `120`).
* This ADR does not create a deployment manifest for OpsBridge. The repository has no
  Kubernetes or compose deployment tree for this context; who runs `--migrate`, with
  which identity, in which environment is an open operational decision. The operational
  procedure that a deployment should follow is written down in
  [Production deployment runbook](#production-deployment-runbook), so the decision is
  "who runs it", not "how does it work".

### Note on this ADR's number and filename

This decision was planned as `docs/adr/013-ef-baseline-strategy.md`. Number `013` was
already taken by [ADR-013 (auth design)](013-auth-design.md) and ADR numbers in this
repository are a stable, non-reusable sequence, so a second ADR with number 013 would
make the index ambiguous. The decision therefore lives in **ADR-018**, next to the
ADR-017 bounded-context ADR it belongs to. No content was lost by the renumbering;
the file name is the only difference from the original plan.

## Rollback

Two different things can be rolled back, and they have **opposite** risk profiles.

### 1. Undoing a baseline (adopting a legacy database)

The baseline writes **one row** into `__EFMigrationsHistory` and touches nothing else.
Its rollback is therefore the inverse of that one write, and it is deliberately **not**
an EF operation:

```sql
-- Undo the baseline. The 19 domain tables and all their data stay exactly as they are;
-- the database returns to the legacy shape (schema without migration history).
DROP TABLE "__EFMigrationsHistory";
```

Verified on a disposable database: after this statement the database had its 19 domain
tables and its business rows, and a subsequent `--baseline-legacy` run re-adopted it
successfully (exit 0, `HistoryWritten=True`, row counts unchanged). The baseline is
therefore **reversible and repeatable**, not a one-way door.

> **Do not use `dotnet ef database update 0` to undo a baseline.** The initial
> migration's `Down()` drops the 19 domain tables, so EF's own rollback path destroys
> the schema *and* the data of a database that was never migrated in the first place.
> Measured on a disposable copy of a baseline-stamped legacy database: 20 public tables
> (19 domain + history) before the rollback, **1** (the history table alone) after it,
> with the business rows gone. EF rolls back *migrations*; a baseline is not a
> migration, it is a history row, and its inverse is one `DROP TABLE`.

### 2. Rolling back a migration that was applied on top of a baseline

From the second migration onwards, the normal EF rule applies: a migration must be
reversible (`Down()` must be written and tested), and the rollback is
`dotnet ef database update <previous-migration-id>`. The rule of thumb for this
repository: **a migration that is not reversible is a migration that requires a
restore-from-backup plan to be written in the same pull request.**

Because the OpsBridge schema is owned by a migration chain, the safe production
rollback for a *new* migration is:

1. `dotnet ef database update <previous-id>` — only if the migration's `Down()` is
   verified and no business data is lost by it;
2. otherwise: restore the database from the pre-deployment backup (PITR or `pg_dump`)
   and roll back the application image to the previous build.

## Production deployment runbook

The decision tree is driven by **the shape the production database is in**, which is
the first thing an operator must establish (see the open questions below). The tool
reports the shape itself, so the check is one command:

```bash
# 0. What shape is the database in? (read-only)
psql "$CONN" -c "select count(*) from information_schema.tables
                 where table_schema='public' and table_name='__EFMigrationsHistory';"
```

| Observed state | Command to run | Expected result |
|---|---|---|
| No database / empty database | `OpsBridge.Api --migrate` | exit 0, 19 domain tables + history |
| 19 domain tables, **no** `__EFMigrationsHistory` (legacy) | `OpsBridge.Api --baseline-legacy` | exit 0, history written, data preserved |
| Same, **but** the schema has drifted | `OpsBridge.Api --baseline-legacy` | **exit 2**, difference printed, nothing written |
| `__EFMigrationsHistory` present and populated | `OpsBridge.Api --migrate` | exit 0, "already at head" (nothing to do) |
| `__EFMigrationsHistory` present but empty | `OpsBridge.Api --baseline-legacy` | exit 0, adopted (verified) |
| History contains an id this build does not know | `OpsBridge.Api --baseline-legacy` | **exit 2**, the unknown id is named |

Ordering rules for a real rollout:

1. **Schema first, application second.** Run `--migrate` (or `--baseline-legacy`)
   *before* the new application image starts serving traffic. The application is
   expected to be compatible with both the old and the new schema for the duration of
   the rollout, exactly as in the FlashSale context (ADR-006).
2. **One schema step, not two.** `--baseline-legacy` and `--migrate` are mutually
   exclusive; a legacy database is baselined *and then* migrated, in that order, in
   two separate steps, each with its own exit code.
3. **Identities.** The schema step needs a DDL-capable identity; the runtime identity
   needs DML only. The baseline additionally needs **`CREATEDB`** on the target
   server (it creates and drops a scratch database). If `CREATEDB` is refused the run
   aborts with exit 1 and writes nothing — it never falls back to an unverified
   baseline.
4. **Exit codes are the contract.** `0` = success, `1` = failure, `2` = drift/unknown
   state. A pipeline must stop on `1` and `2`, and must not retry `2` blindly — a
   drift exit means a human has to look at the printed difference.
5. **Never point the step at the wrong database.** The schema entrypoints refuse the
   localhost fallback and take the connection string only from
   `ConnectionStrings__OpsBridgeDb` (or `ConnectionStrings__DefaultConnection`).
6. **Re-running is safe.** Both commands are idempotent: a second run of `--migrate`
   reports "already at head" and exits 0, and a second `--baseline-legacy` verifies the
   already-managed database and writes nothing.

## Verification evidence (2026-10-20)

All of the following was executed against a **disposable** PostgreSQL 16 container
(`opsbridge-ef-verify`, port 55444), never against a real database, and the container
and every database in it were dropped afterwards.

| # | Scenario | Command | Result |
|---|---|---|---|
| 0 | Legacy database (19 tables from a real `EnsureCreatedAsync()`), migration applied blindly | `--migrate` | **exit 1**, `42P07: relation "AuditEvents" already exists`, 0 history rows written — this is the blocker |
| 1 | Legacy database (19 tables, no history, 1 seeded `Connectors` row) | `--baseline-legacy` | **exit 0**, `HistoryWritten=True`, `VerificationRan=True`, `Differences=0`, 19/19 row counts unchanged, scratch database dropped |
| 1b | Same database, after the baseline | `--migrate` | **exit 0**, `AlreadyAtHead=True` |
| 2 | Empty database | `--migrate` | **exit 0**, 20 public tables (19 domain + history), 1 history row |
| 3 | Database already at head | `--migrate` (re-run) | **exit 0**, `AlreadyAtHead=True`, `AppliedByThisRun=[]`, still exactly 1 history row |
| 4 | Legacy database with one column dropped (drift) | `--baseline-legacy` | **exit 2**, `table "Connectors" is missing column "BaseUrl"`, `HistoryWritten=False` |
| 5 | History table present but empty | `--baseline-legacy` | **exit 0**, adopted and verified |
| 6 | Model parity | `dotnet ef migrations has-pending-model-changes` | "No changes have been made to the model since the last migration." |
| 7 | Rollback of an applied migration | `dotnet ef database update 0` | "Reverting migration … Done." — 20 tables → 1 (history table only) |
| 8 | Undo of a baseline (history drop) | `DROP TABLE "__EFMigrationsHistory"` → `--baseline-legacy` | 19 tables and the business row kept, re-adoption exit 0 |
| 9 | Baseline of a *drifted* database followed by EF's own rollback | `dotnet ef database update 0` | 20 tables → 1 and the business row **destroyed** — why EF's rollback must not be used to undo a baseline |

## Open questions for the owner

These are not implementation details; they are decisions that need a human answer
before the first production migration.

1. **What shape is the production OpsBridge database in today?** Concretely: does it
   exist, does it have the 19 domain tables, and does it have an
   `__EFMigrationsHistory` table? The three answers select the command:
   * no database → `--migrate`;
   * schema without history → `--baseline-legacy`;
   * schema with history → `--migrate` (probably a no-op).
   Until this is answered, **the safe action is to run `--baseline-legacy`**: it
   verifies the schema before it writes, and it refuses with exit 2 instead of guessing.
2. **Is there a production OpsBridge database at all?** Neither `docker-compose.yml`
   nor `infrastructure/kubernetes` contains an OpsBridge workload today (verified:
   no `opsbridge` match in either tree). If OpsBridge is not deployed yet, the
   production database does not exist, and the whole question reduces to "which
   command does the first deployment run" — namely `--migrate` on an empty database.
3. **Who runs the schema step, with which identity, in which environment?** There is
   no deployment manifest for OpsBridge yet, and no Kubernetes Job analogous to the
   FlashSale migration Job. The identity needs DDL rights plus `CREATEDB` for the
   baseline; the runtime identity must stay DML-only. This also decides whether
   `OpsBridge:AutoMigrate` ever needs to exist outside Development/Test — the current
   answer is "no", because the deployment step is the supported path.
4. **Is the runtime identity allowed to hold DDL rights at all?** The current ADR
   answers "no" (runtime = DML only). If the operational reality is a single
   credential for everything, that answer changes and ADR-018 has to be amended
   before the first deployment — not after.
5. **Is the PostgreSQL server 13 or newer?** The baseline's `DROP DATABASE … WITH
   (FORCE)` requires it. The disposable verification used PostgreSQL 16.
6. **What is the backup/PITR position before the first production schema change?** A
   baseline cannot be undone with EF's own rollback (see "Rollback" above), so a
   verified restore point is the only safety net if the production database turns out
   to have drift.
7. **Should a migration ever be written that is not reversible?** The current rule is
   "reversible or restore-from-backup, decided in the same pull request". Confirm
   that this is the standard the team wants, because it is the rule that makes
   `dotnet ef database update <previous>` a safe production action.
