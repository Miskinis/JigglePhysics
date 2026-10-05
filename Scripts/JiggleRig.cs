using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GatorDragonGames.JigglePhysics {

public class JiggleRig : MonoBehaviour, IJiggleParameterProvider {
    [SerializeField] private JiggleRigData jiggleRigData;
    [SerializeField, Tooltip("Whether to check if parameters have been changed each frame.")] private bool animatedParameters = false;
    
    [NonSerialized] private JiggleTreeSegment segment;
    private bool addedToJiggleTreeSegments = false;
    // FORK PATCH (P2): one-shot guard for rebind failures during hierarchy swaps.
    // Kept on the component (not a static collection) so it dies with the rig and
    // survives the prefab-instantiation lifecycle without ID reuse hazards.
    [NonSerialized] private bool rebindFailureWarned;

    public JiggleRigData GetJiggleRigData() => jiggleRigData;

    public JiggleTreeInputParameters GetInputParameters() => jiggleRigData.jiggleTreeInputParameters;
    
    
    /// <summary>
    /// Sets the jiggle tree input parameters, but only locally, to send it to jobs either make sure animatedParameters is true, or call UpdateParameters after you're done making changes.
    /// </summary>
    /// <param name="newParameters">The parameters that the jiggles should use to determine their motion.</param>
    public void SetInputParameters(JiggleTreeInputParameters newParameters) {
        jiggleRigData.jiggleTreeInputParameters = newParameters;
    }

    #if !JIGGLEPHYSICS_DISABLE_ON_ENABLE
    private void OnEnable() {
        OnInitialize();
    }
    #endif
    
    #if !JIGGLEPHYSICS_DISABLE_ON_DISABLE
    private void OnDisable() {
        OnRemove();
    }
    #endif

    public void OnInitialize() {
        if (jiggleRigData.rootBone == null) {
            throw new UnityException("Jiggle Rig enabled without a root bone assigned!");
        }

        jiggleRigData.RegenerateCacheLookup();

        segment ??= new JiggleTreeSegment(this);
        segment.SetDirty();
        if (!addedToJiggleTreeSegments) {
            // FORK PATCH (P3): only claim registration when the segment actually won (a duplicate
            // root is rejected). Otherwise a later OnRemove could evict the winning segment.
            addedToJiggleTreeSegments = JigglePhysics.AddJiggleTreeSegment(segment);
        }
    }

    public void OnRemove() {
        if (segment != null && addedToJiggleTreeSegments) {
            JigglePhysics.RemoveJiggleTreeSegment(segment);
            addedToJiggleTreeSegments = false;
        }
    }

    /// <summary>
    /// Immediately resamples the rest pose of the bones in the tree. This can be useful if you have modified the bones' transforms on initialization and want to control when the rest pose is sampled.
    /// </summary>
    public void ResampleRestPose() {
        jiggleRigData.ResampleRestPose();
        if (segment != null) {
            // FORK PATCH (P1): route through the segment guard. Calling
            // segment.jiggleTree.SetDirty() directly skipped the dirty-flag check in
            // JiggleTreeSegment.SetDirty() (SetDirty() sets dirty=true *before* firing the
            // dirtied event), so ScheduleRemoveJiggleTree was never called and each resample
            // leaked one duplicate memory-bus entry.
            segment.SetDirty();
        }
    }

    public void SnapToRestPose() {
        jiggleRigData.SnapToRestPose();
    }

    /// <summary>
    /// Re-points this rig's root bone and entire chain at a replacement hierarchy by NAME.
    /// <para>
    /// <paramref name="newRoot"/> is the replacement for THIS rig's root bone — the caller must
    /// resolve the rig's own root name through <paramref name="resolveByName"/> (see
    /// <see cref="JiggleRigData.GetCachedRootName"/>) and pass the resulting transform. Do NOT
    /// pass a hierarchy/skeleton root: a name mismatch fails the rebind with a clear reason and
    /// leaves the rig untouched.
    /// </para>
    /// <para>
    /// Every transform in the rig's serialized <see cref="JiggleTransformCachedData"/> is resolved
    /// through <paramref name="resolveByName"/> using its (possibly newly remapped) name. If any
    /// name cannot be resolved this returns <c>false</c> and the rig is left COMPLETELY untouched
    /// (safe no-op) — the caller is expected to warn once and keep the rig disabled.
    /// </para>
    /// <para>
    /// Expected lifecycle: hierarchy change → rig already disabled (no registered tree) →
    /// <see cref="TryRebind"/> → rig re-enabled, which registers a fresh tree through
    /// <see cref="OnInitialize"/>. This method also supports being called while the rig is enabled:
    /// it performs a disable → rebind → enable cycle so the old tree is unregistered cleanly and
    /// the new one registers through the normal lifecycle paths.
    /// </para>
    /// </summary>
    /// <param name="newRoot">Replacement for this rig's own root bone (resolve the current root name via <paramref name="resolveByName"/>), not a hierarchy root.</param>
    /// <param name="resolveByName">Name → Transform resolver (e.g. CharacterRig.GetBoneByActualName). May return null for names not present in the new hierarchy.</param>
    /// <param name="failReason">Human-readable reason when this returns false (mismatched root or an unresolvable chain name).</param>
    /// <returns>True when the rebind succeeded and cached data was rebuilt; false when nothing was changed.</returns>
    public bool TryRebind(Transform newRoot, Func<string, Transform> resolveByName, out string failReason) {
        if (newRoot == null) {
            failReason = "newRoot is null";
            return false;
        }
        if (resolveByName == null) {
            failReason = "resolveByName is null";
            return false;
        }

        // Disable → rebind → enable: when the rig is already registered this unregisters the old
        // tree through OnDisable/OnRemove (which schedules removal) before any cached data moves.
        // The disable is idempotent when the caller (attach path) already disabled the rig.
        // Track the enabled flag (not isActiveAndEnabled) so a disabled rig stays disabled even
        // when its GameObject is inactive and OnEnable could not have run.
        bool wasEnabled = enabled;
        if (wasEnabled) {
            enabled = false;
        }

        if (!jiggleRigData.TryRebind(newRoot, resolveByName, out failReason)) {
            if (wasEnabled) {
                enabled = true;
            }
            return false;
        }

        if (segment != null) {
            // The segment's registered root object must track the new bone so
            // AddJiggleTreeSegment/RemoveJiggleTreeSegment key the lookup correctly.
            segment.SetRootTransform(newRoot);
            // Dirty through the guarded path: a clean, unregistered tree may be re-added by the
            // next appearance pass; a stale registered tree schedules removal and gets rebuilt.
            segment.SetDirty();
        }

        if (wasEnabled) {
            enabled = true;
        }

        failReason = null;
        return true;
    }

    /// <summary>Convenience overload of <see cref="TryRebind(Transform, Func{string, Transform}, out string)"/>.</summary>
    public bool TryRebind(Transform newRoot, Func<string, Transform> resolveByName) =>
        TryRebind(newRoot, resolveByName, out _);

    /// <summary>
    /// Clears the one-shot rebind-failure warning flag (wired to a successful rebind).
    /// </summary>
    public void ResetRebindWarning() {
        rebindFailureWarned = false;
    }
    
    /// <summary>
    /// Returns true exactly once per rig for a given failure; used by external lifecycle helpers
    /// to warn about an unresolvable rebind without spamming the console.
    /// </summary>
    public bool ShouldLogRebindFailure() {
        if (rebindFailureWarned) {
            return false;
        }
        rebindFailureWarned = true;
        return true;
    }
    
    /// <summary>
    /// Sends updated parameters to the jiggle tree on the jobs side.
    /// </summary>
    public void UpdateParameters() {
        if (segment == null) {
            return;
        }
        segment.UpdateParameters();
    }

    public bool HasAnimatedParameters {
        get => animatedParameters;
        set => animatedParameters = value;
    }

    private void OnValidate() {
        if (!jiggleRigData.hasSerializedData) {
            jiggleRigData = JiggleRigData.Default();
        }
        jiggleRigData.OnValidate();
        if (Application.isPlaying) {
            UpdateParameters();
        }
    }

    private void OnDrawGizmosSelected() {
        if (!isActiveAndEnabled) {
            return;
        }
        jiggleRigData.OnDrawGizmosSelected();
    }

}

}
