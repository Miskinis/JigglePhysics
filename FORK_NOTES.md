# Fork Notes — com.gator-dragon-games.jigglephysics (embedded)

This is a **vendored fork** of the upstream OpenUPM package `com.gator-dragon-games.jigglephysics`
by **naelstrof** (Gator Dragon Games), distributed under the **MIT License** (see `LICENSE`).
Modification is permitted and the license is retained.

| | |
|---|---|
| Upstream | `com.gator-dragon-games.jigglephysics` (OpenUPM: https://package.openupm.com) |
| Upstream author | naelstrof (Gator Dragon Games) |
| Forked version | **16.0.1** (embedded copy is byte-identical to the upstream release apart from the patches below) |
| License | MIT — `LICENSE` retained, `Copyright (c) naelstrof` |
| Embedded at | `Packages/com.gator-dragon-games.jigglephysics/` |
| Consumers | `com.oddlock.rigging`, `com.oddlock.character-editor`, `com.oddlock.appearance` (reference by package name) |

**Never edit `Library/PackageCache/`.** This embedded folder is the single source of truth for the
fork. If the package is ever re-imported from the registry, this folder must not be overwritten.

## Re-apply after upstream updates

When updating the upstream version, re-apply the patches below. Every patch site is marked in the
source with a `FORK PATCH (P1|P2|P3a|P3b)` comment so they can be found with:

```bash
grep -rn "FORK PATCH" Packages/com.gator-dragon-games.jigglephysics/Scripts/
```

Keep the package identity (`package.json` name, asmdef) unchanged. The fork stays a strict
superset of the upstream public API: no existing public members were removed or renamed.

---

## P1 — `JiggleRig.ResampleRestPose()` leaked one duplicate bus entry per call

**File:** `Scripts/JiggleRig.cs` (`ResampleRestPose()`)

**Problem.** The method called `segment.jiggleTree.SetDirty()` directly. `JiggleTree.SetDirty()`
sets `dirty = true` **before** firing its `dirtied` event, and `JiggleTreeSegment.SetDirty()`
only calls `JigglePhysics.ScheduleRemoveJiggleTree(...)` when it observes `dirty == false`.
By the time the event reached the segment the flag was already set, so the removal was skipped.
The subsequent regeneration in `JigglePhysics.GetJiggleTrees()` still called
`jobs.ScheduleAdd(seg.jiggleTree)` → **one extra memory-bus entry per call** (+1 tree per rig per
call). Live-proven: repeated resamples produced duplicate trees sharing a `rootID`, which the
memory bus cannot selectively remove (the reason `JigglePhysicsHelper.ResetJiggleRigs` exists).

**Fix (before → after).**

```csharp
// BEFORE
public void ResampleRestPose() {
    jiggleRigData.ResampleRestPose();
    if (segment != null && segment.jiggleTree != null) {
        segment.jiggleTree.SetDirty();
    }
}

// AFTER
public void ResampleRestPose() {
    jiggleRigData.ResampleRestPose();
    if (segment != null) {
        segment.SetDirty();   // guarded: schedules Remove before Add
    }
}
```

`JiggleTreeSegment.SetDirty()` schedules the removal and then sets dirty (the subsequent
`OnDirty` event re-entry is idempotent). Add and Remove now pair up.

---

## P2 — Rebind API for swapped body parts (restores jiggle on the new hierarchy)

**Files:**
- `Scripts/JiggleRig.cs` — `TryRebind(...)`, `ResetRebindWarning()`, `ShouldLogRebindFailure()`
- `Scripts/JiggleRigData.cs` — `TryRebind(...)`
- `Scripts/JiggleTreeSegment.cs` — `SetRootTransform(...)`
- `Packages/com.oddlock.rigging/Runtime/JigglePhysicsHelper.cs` — `RebindJiggleRigs(...)`
- `Packages/com.oddlock.rigging/Runtime/RigExtensions.cs` — call inside `AttachBodyPartMesh`

**Problem.** A body-part prefab ships its own duplicate skeleton. `AttachBodyPartMesh` instantiates
it, remaps the SMR bones to the shared skeleton by name, then destroys the prefab's duplicate
hierarchy. A `JiggleRig` inside the prefab had cached its chain against the **prefab's** bone
transforms (now destroyed), and its `rootBone` points into that dead hierarchy. The existing
lifecycle guard keeps such rigs disabled — correct safety, but the jiggle is lost forever on any
body-part swap (hair, tail, ears, …). Re-enabling such a rig would register a tree against dead
transforms (`rootBone == null` or destroyed transforms), i.e. the exact failure mode the guard
was added for.

**Design.** Name-based, all-or-nothing rebind:

1. Resolve every transform in `jiggleRigData.transformCachedData` through a caller-supplied
   `Func<string, Transform>` using the bone's name. Names survive `RemapBones` (which rebinds
   SMR bones by name against the shared skeleton), so the rig's cached names are exactly the keys
   to resolve.
2. **Fail-safe:** if ANY name cannot be resolved (or two names collapse to one transform, or a
   resolved bone is missing from the rebuilt chain), return `false` **without touching the rig**.
3. On success: repoint `rootBone`, rebuild the normalized-distance cache via
   `BuildNormalizedDistanceFromRootList()` (fresh `normalizedDistanceFromRoot`, `lossyScale`,
   rest local pose) and resample the rest pose from the new hierarchy.
4. If the rig is enabled, run `disable → rebind → enable` so the old tree is unregistered through
   `OnDisable`/`OnRemove` (removal-scheduled) and the new one registers through the normal
   `OnEnable`/`OnInitialize` lifecycle.

**Root resolution contract (important).** `newRoot` is the replacement for **THIS rig's own root
bone** — the caller resolves the rig's current root name (the serialized chain root, index 0 of
`transformCachedData`, via `JiggleRigData.GetCachedRootName()`) through the resolver and passes that
transform. It is **not** the hierarchy/skeleton root: passing the skeleton root binds every rig to
one transform, the package rejects the duplicates ("Multiple Jiggle trees detected targeting the
same root transform"), and only one tree survives. `JiggleRigData.TryRebind` enforces this with a
defensive name check: a `newRoot` whose name differs from the rig root name fails with
`"newRoot 'X' does not match rig root 'Y'"` and the rig is untouched. Rigs with no cached chain and
no existing root accept any non-null root (fresh/uninitialized).

> **Live evidence for the defensive check.** The first wiring pass called
> `RebindJiggleRigs(instance, rig.skeletonRoot, resolver)` and every one of 4 body-part rigs rebound
> with `rootBone = "root"` (the whole skeleton) and `cachedChainLen = 191`: only 1 tree registered,
> the console logged `Multiple Jiggle trees detected targeting the same root transform`, and the
> other 3 rigs were inert. The helper now resolves each rig's own root name first (and skips the rig
> if it cannot be resolved); the package-side name check makes that class of miswiring impossible to
> commit silently.

**Live-verification adjustment — ambiguous root names.** A body-part prefab can have its JiggleRig
root literally named like the skeleton root (`Human Penis.prefab` → `root`). Name resolution then
correctly returns the shared skeleton root, and the rig would bind to the whole skeleton
(`cachedChainLen = 191`), become the parent segment of the breast/ass chains (absorbing them into
one 263-point tree) and simulate the entire character. Before the fork that rig went dormant after
attach — the helper now restores that safe outcome with two skip conditions, checked before
`TryRebind`:

1. **Resolved root is the skeleton root** (`resolvedRoot == skeletonRoot`) — skip, warn once:
   `"root '<name>' resolves to the shared skeleton root"`.
2. **Implausible cached chain length** (`transformCachedData.Length > MaxPlausibleRebindChainLength`,
   const `64`) — skip, warn once: `"implausible cached chain length (N > 64)"`. Real garment/prop
   chains are tiny (live: breast=2, ass=1); 191 is the collision signature.

Skipped rigs stay disabled and use the same per-rig warn-once flag as the other failure paths. The
skeleton root is therefore passed to the helper **only** as the ambiguity reference — never as a
rebind target.

**New API surface (additive, upstream-compatible):**

```csharp
// JiggleRig
public bool TryRebind(Transform newRoot, System.Func<string, Transform> resolveByName, out string failReason);
public bool TryRebind(Transform newRoot, System.Func<string, Transform> resolveByName);
public void ResetRebindWarning();
public bool ShouldLogRebindFailure();

// JiggleRigData
public bool TryRebind(Transform newRoot, System.Func<string, Transform> resolveByName, out string failReason);
public string GetCachedRootName();

// JiggleTreeSegment
public void SetRootTransform(Transform newRoot);

// Oddlock.Rigging.JigglePhysicsHelper
public static int RebindJiggleRigs(GameObject root, Transform skeletonRoot,
    System.Func<string, Transform> resolveByName);
```

**Lifecycle contract (documented on `JiggleRig.TryRebind`):**

```
hierarchy change (body-part swap)
  → rigs disabled (JigglePhysicsHelper.DisableJiggleRigs / JiggleDisableOnSpawn)
  → RemapBones (SMR bones re-pointed at shared skeleton by name)
  → JigglePhysicsHelper.RebindJiggleRigs(instance, rig.skeletonRoot, rig.GetBoneByActualName)
      per rig: resolve the rig's OWN root name → reject ambiguous roots (skeleton root /
      chain > 64) → TryRebind(that transform, resolver)
      success → rig rebinds; unresolved root / ambiguous root / implausible chain /
      unresolvable chain → rig left disabled + warn once
  → JigglePhysicsHelper.EnableJiggleRigs (RuntimeAppearanceApplySystem / MaterialColorUpdateSystem)
      → OnEnable → OnInitialize → fresh tree on the new hierarchy
```

The warning is throttled by `JiggleRig.ShouldLogRebindFailure()` (per-rig one-shot flag, reset on
a successful rebind) so a persistently unresolvable rig does not spam the console. Unresolvable
rigs stay **disabled** — no behavior change for them beyond the one-time warning.

**Evidence.** The attach path previously could not restore jiggle after a swap at all: the rig's
`rootBone` referenced the destroyed prefab hierarchy and `EnableJiggleRigs` skipped the rig with
"rootBone is null". No mechanism existed to re-point a rig at the shared skeleton (upstream ships
no rebind API in 16.0.1).

---

## P3a — Child jiggle-tree segments were never pruned when their transforms died

**File:** `Scripts/JigglePhysics.cs` (`GetJiggleTrees()`, `ReparentOrphanedChildren(...)`)

**Problem.** The dead-segment pruning pass only iterated `rootJiggleTreeSegments`. Segments whose
root is a **child** of another segment live only in `jiggleRootLookup` (they are registered as
child segments, never added to the root list). When such a transform was destroyed out-of-band
(asset unload, teardown, skipped `OnDisable`), its entry was never pruned: the segment stayed in
the lookup referencing dead transforms and kept getting regenerated through its parent's dirty
chain. A dead chain nested under a live root stayed registered forever.

**Fix.**

- Extracted the orphan-reparenting logic into `ReparentOrphanedChildren(segment)` (shared by
  `RemoveJiggleTreeSegment` and the new pruning path so they cannot drift).
- After the root pass, walk `jiggleRootLookup`; collect dead child segments into a scratch list
  (never mutate a dictionary while enumerating it), then for each: remove the lookup entry,
  reparent its orphaned children to its parent, add newly-parentless children to the root list,
  dirty the old parent, and detach the dead segment. Dead root/child trees are still removal-
  scheduled where a tree exists.

---

## P3b — One invalid frame froze the simulation forever

**Files:**
- `Scripts/JiggleJobSimulate.cs` (`Execute`)
- `Scripts/JiggleTreeJobData.cs` (`ValidateAndSanitize`, `TryReportSanitize`,
  `lastSanitizeReportedFrame`)
- `Scripts/JiggleSimulatedPoint.cs` (`GetIsValid(..., pointIndex, ...)`, `Sanitize(pointCount, pointIndex)`)

**Problem.** `Execute` called `Validate(tree)` which **threw** `InvalidOperationException` on the
first invalid frame — *before* `Sanitize` ran. A NaN or a corrupt parent/child index therefore
aborted the job and, because nothing ever repaired the state, the simulation was frozen on every
subsequent frame. Additionally `Sanitize()` only repaired NaN scalars: structure fields
(`childrenCount`, `childrenIndices`, `parentIndex`) were never repaired, so structural corruption
could never self-heal.

**Fix.**

- `Execute` now calls `ValidateAndSanitize`: validate → if invalid, `Sanitize()` in place → validate
  again. The tree is simulated when it is safe; if it is still corrupt after repair the tree is
  **skipped for that tick** (indexing garbage links would corrupt more state) while every other
  tree keeps simulating. No throw — one bad tree can no longer stop the whole job.
- `JiggleSimulatedPoint.Sanitize(pointCount, pointIndex)` now also clamps `childrenCount`,
  zeroes out-of-range child indices, and resets invalid/self-referential `parentIndex` to `-1`.
- Reporting is throttled by `JiggleTreeJobData.lastSanitizeReportedFrame` (stored in the tree
  slice, so it survives across frames) — at most one warning per tree per second window, not one
  per simulation tick. A regenerated tree resets the throttle in `Set(...)`.

**Evidence.** Live: a single NaN/corrupt frame permanently stopped all jiggle by throwing out of
the job; the only recovery was `JigglePhysicsHelper.ResetJiggleRigs` (full simulator purge).

---

## P3c — Rejected segments must never evict the winning registration

**Files:** `Scripts/JigglePhysics.cs` (`AddJiggleTreeSegment`, `RemoveJiggleTreeSegment`),
`Scripts/JiggleRig.cs` (`OnInitialize`)

**Problem.** `JiggleRig.OnInitialize` set `addedToJiggleTreeSegments = true` even when
`JigglePhysics.AddJiggleTreeSegment` **rejected** the segment (duplicate root → the
`jiggleRootLookup.TryAdd` failed and the error was logged). The rejected rig then believed it was
registered, and its later `OnRemove` called `RemoveJiggleTreeSegment`, whose unconditional
`jiggleRootLookup.Remove(segment.transform)` evicted the **valid** segment that had won the
duplicate race — leaving the winner registered in name only. This is exactly what happened in the
failed P2 wiring pass: after 4 rigs were forced onto the same root, disabling/teardown of the 3
rejected rigs could knock out the 1 valid tree.

**Fix.**

```csharp
// JigglePhysics.AddJiggleTreeSegment now returns bool
public static bool AddJiggleTreeSegment(JiggleTreeSegment jiggleTreeSegment) {
    if (jiggleTreeSegment.transform == null) return false;
    if (!jiggleRootLookup.TryAdd(...)) { Debug.LogError(...); return false; }
    ...
    return true;
}

// JiggleRig.OnInitialize: only claim registration when it actually happened
addedToJiggleTreeSegments = JigglePhysics.AddJiggleTreeSegment(segment);

// JigglePhysics.RemoveJiggleTreeSegment: only drop the entry that belongs to this segment
if (key != null && jiggleRootLookup.TryGetValue(key, out var current) && current == jiggleTreeSegment)
    jiggleRootLookup.Remove(key);
```

Both sides are guarded: the flag is truthful, and removal is ownership-checked, so a rejected
segment can never evict another segment's registration. (`AddJiggleTreeSegment` also now
rejects a null root — public callers included.)

---

## Non-code change

- `Packages/com.oddlock.rigging/package.json` — dependency bumped `15.0.0` → **`16.0.1`** to match
  the embedded package version. No API changes were required for this (the fork keeps the upstream
  public surface plus additions).
