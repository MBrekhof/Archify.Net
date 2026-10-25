# Which modules does Archify finalize go through, and how is the old artifact kept safe on failure? {#finalize-modules}

`finalize` is an orchestrator, not a renderer. `runFinalize` calls the three subcommands `deliver → check → browser-check` in order (validate runs inside deliver). Each gate uses sha256 and `receiptId` to confirm its receipt is bound to this artifact. The first failure stops the run. The old artifact is replaced only at deliver's commit point. The new HTML is first rendered and checked in a `.archify-delivery-*` staging area in the same directory. At commit the old file is backed up, then the new one is swapped in with a hard link. A failure midway rolls back automatically. If even the rollback fails, the recovery directory is kept.

- All three gates reuse the same CLI; finalize checks each receipt's type, byte count and hash.[](source:finalize-mjs)
- Nothing is written to the target path before commit; at commit it backs up first, then `linkSync`, and aborts on any binding mismatch.[](source:commit)
- The delivery lock and the pending journal make an interruption fail closed: concurrent runs are refused, leftover state is not trusted.[](source:deliver)
- Limit: a check or browser-check failure after a successful commit does not roll back; the old artifact is kept only up to the commit point.

@answer scope="Archify @ 82c47ca · bin/finalize.mjs + archify.mjs · explainer has no browser-check gate by default"
@meta locale=en repo_url=https://github.com/tt-a1i/archify repo_revision=82c47ca31d83749020ff30d6b1172248e71d255d
@source finalize-mjs title="runFinalize orchestrator" path=archify/bin/finalize.mjs lines=722:1050
@source commit title="commitDeliveryPair: backup, commit, rollback" path=archify/bin/archify.mjs lines=3236:3624
@source deliver title="commandDeliver main delivery flow" path=archify/bin/archify.mjs lines=4400:5301
@source check-cmd title="check and provenance checks" path=archify/bin/archify.mjs lines=5418:5659
@source recover title="recover-output.mjs recovery entry" path=archify/bin/recover-output.mjs lines=1:41

## Which gates does finalize split a delivery into? {#gates}

An explainer candidate is first compiled by `prepareExplainerInput` into a frozen bundle, and before and after each gate its dependencies are rechecked as unchanged. Native diagrams run all three gates by default; an explainer runs only `deliver + check`, and the browser gate opens only with `--browser-check`.

@workflow id=finalize-gates title="The finalize module chain"
| lane orch | Orchestrate bin/finalize.mjs |
| lane del | Deliver subcommand |
| lane ver | Verify subcommands |
| cli | commandFinalize parses args | backend | orch |
| snap | Snapshot candidate, open receipt | backend | orch |
| del | Lock, journal, staged render | backend | del |
| commit | commitDeliveryPair | backend | del |
| chk | check strict recheck | backend | ver |
| brw | browser-check Chrome | backend | ver |
| rec | .finalize.json receipt | database | orch |
| cli > snap | |
| snap > del | |
| del > commit | |
| commit > chk | pass |
| chk > brw | pass |
| brw > rec | |

@steps id=gate-steps
| parse | commandFinalize parses args | Parses quality, repo and out-dir; precompiles an explainer into a frozen bundle; starts a bounded update check in parallel. | finalize-gates | cli |
| snap | Snapshot candidate, open receipt | Records the input sha256; both receipt paths are captured atomically before writing. | finalize-gates | snap |
| del | Lock, journal, staged render | Takes the delivery lock and writes the pending journal; rendering and self-checks run in staging; any failing gate stops the run and writes diagnostics. | finalize-gates | del |
| chk | check strict recheck | Runs the strict checks once provenance=current and the sha matches, then the browser gate. | finalize-gates | chk |

## Which step fails, and what state is the old artifact in? {#failure-matrix}

| Failure point | Old artifact at the output path | Protection |
| --- | --- | --- |
| Arguments, input or validate fail | Untouched {good:Safe} | Nothing touches the target before rendering; the delivery lock is not taken yet |
| Staged render or deliver's artifact check fails | Untouched {good:Safe} | The candidate is written only to `.archify-delivery-*`; a failed check reports previous artifact was preserved |
| `commitDeliveryPair` fails midway | Rolled back {good:Safe} | The old files are first backed up as `.previous-output` and `.previous-provenance`; on error each is linked back and its binding rechecked |
| Rollback fails or the lock is lost | Left in the recovery directory {caution:Manual} | `recoveryRequired`: the staging directory is kept, and `recover-output.mjs` can restore the old files |
| Another deliver runs, or a lock/pending is left over | Blocked by the lock {good:Safe} | `delivery/concurrent-attempt` or `lock-stale`; the new delivery refuses to start |
| check or browser-check fails after deliver committed | Already replaced by the new artifact {caution:Limit} | The new artifact passed its own checks; finalize reports fail and writes diagnostics, no automatic revert |

## Which files does a finalize leave on disk? {#sidecars}

| File | Role |
| --- | --- |
| `<out>.delivery.json` | Success provenance: `receiptId`, the spec and artifact sha256, validation summary |
| `<out>.delivery-pending.json` | In-progress journal; deleted at commit, a leftover is evidence of an interrupted delivery |
| `<out>.delivery-lock.json` | Delivery lock; blocks concurrent and stale deliveries |
| `<out>.browser-check.json` | Browser evidence receipt; produced by default for native diagrams |
| `<out>.finalize.json` and `-summary.json` | Full and compact finalize receipts, published atomically through a temp file plus binding |
| `.archify-delivery-*` directory | Staging: spec snapshot, render candidate, `.previous-*` backups; cleaned up normally, kept when recovery is needed |

## After an interrupted delivery, in what order do you check? {#recovery}

- [ ] Read `failedStage` and `diagnostics[].supportedFixes` in `.finalize-summary.json` to see which gate stopped it.
- [ ] On `recoveryRequired`, run `node bin/recover-output.mjs <staging directory>` to restore the kept old files.
- [ ] On a leftover lock or pending file, recover serially as the diagnostics say, then retry; do not delete the files directly.
- [ ] To get back an older version after the commit point, use your own version control or backups.
