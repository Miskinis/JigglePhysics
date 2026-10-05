using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.UIElements;
#endif

namespace GatorDragonGames.JigglePhysics {

[Serializable]
public struct JiggleTransformCachedData {
    public Transform bone;
    public float normalizedDistanceFromRoot;
    public float lossyScale;
    public Vector3 restLocalPosition;
    public Vector4 restLocalRotation;
}

[Serializable]
public struct JiggleRigData {
    [SerializeField] public bool hasSerializedData;
    [SerializeField] public string serializedVersion;
    [SerializeField] public Transform rootBone;
    [SerializeField] public bool excludeRoot;
    [SerializeField] public JiggleTreeInputParameters jiggleTreeInputParameters;
    [SerializeField] public Transform[] excludedTransforms;
    [SerializeField, HideInInspector] public JiggleTransformCachedData[] transformCachedData;
    [SerializeField] public JiggleColliderSerializable[] jiggleColliders;
    
    [NonSerialized]
    private Dictionary<Transform, JiggleTransformCachedData> transformToCachedDataMap;

    private bool TryUpdateSerialization() {
        switch (serializedVersion) {
            case "v0.0.0": // Collision radius local space -> world space
                if (rootBone == null) {
                    return false;
                }
                var cachedScale = GetCache(rootBone);
                var scale = rootBone.lossyScale;
                var scaleSample = (scale.x + scale.y + scale.z)/3f;
                var scaleCorrection = cachedScale.lossyScale*(1f/(scaleSample*scaleSample));
                jiggleTreeInputParameters.collisionRadius.value *= scaleCorrection;
                serializedVersion = "v0.0.1";
                return true;
            case "v0.0.1": // rest pose is now serialized on author, generate if missing.
                var length = transformCachedData.Length;
                for (int i = 0; i < length; i++) {
                    var cachedData = transformCachedData[i];
                    var t = cachedData.bone;
                    if (!t) continue;
                    t.GetLocalPositionAndRotation(out var localPosition, out var localRotation);
                    cachedData.restLocalPosition = localPosition;
                    cachedData.restLocalRotation = new Vector4(localRotation.x, localRotation.y, localRotation.z, localRotation.w);
                    transformCachedData[i] = cachedData;
                }
                serializedVersion = "v0.0.2";
                return true;
            default:
                return false;
        }
    }

    public void ResampleRestPose() {
        var length = transformCachedData.Length;
        for (int i = 0; i < length; i++) {
            var cachedData = transformCachedData[i];
            var t = cachedData.bone;
            if (!t) continue;
            t.GetLocalPositionAndRotation(out var localPosition, out var localRotation);
            cachedData.restLocalPosition = localPosition;
            cachedData.restLocalRotation = new Vector4(localRotation.x, localRotation.y, localRotation.z, localRotation.w);
            transformCachedData[i] = cachedData;
        }
        RegenerateCacheLookup();
    }

    public void SnapToRestPose() {
        var length = transformCachedData.Length;
        for (int i = 0; i < length; i++) {
            var cachedData = transformCachedData[i];
            var t = cachedData.bone;
            if (!t || t == rootBone) continue;
            t.SetLocalPositionAndRotation(cachedData.restLocalPosition, new Quaternion(cachedData.restLocalRotation.x, cachedData.restLocalRotation.y, cachedData.restLocalRotation.z, cachedData.restLocalRotation.w));
        }
    }

    public void RegenerateCacheLookup() {
        transformToCachedDataMap = new Dictionary<Transform, JiggleTransformCachedData>();
        var count = transformCachedData.Length;
        for (int i = 0; i < count; i++) {
            var cachedData = transformCachedData[i];
            transformToCachedDataMap[cachedData.bone] = cachedData;
        }
    }

    public bool GetIsExcluded(Transform t) {
        var count = excludedTransforms.Length;
        for (int i = 0; i < count; i++) {
            if (excludedTransforms[i] == t) {
                return true;
            }
        }
        return false;
    }
    
    public void GetJiggleColliders(List<JiggleCollider> colliders) {
        colliders.Clear();
        var count = jiggleColliders.Length;
        for(int i=0;i<count;i++) {
            colliders.Add(jiggleColliders[i].collider);
        }
    }

    void ValidateCurve(ref AnimationCurve animationCurve) {
        if (animationCurve == null || animationCurve.length == 0) {
            animationCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        }
    }

    public void OnValidate() {
        jiggleTreeInputParameters.OnValidate();
        excludedTransforms ??= Array.Empty<Transform>();
        ValidateCurve(ref jiggleTreeInputParameters.stiffness.curve);
        ValidateCurve(ref jiggleTreeInputParameters.angleLimit.curve);
        ValidateCurve(ref jiggleTreeInputParameters.stretch.curve);
        ValidateCurve(ref jiggleTreeInputParameters.drag.curve);
        ValidateCurve(ref jiggleTreeInputParameters.airDrag.curve);
        ValidateCurve(ref jiggleTreeInputParameters.gravity.curve);
        ValidateCurve(ref jiggleTreeInputParameters.collisionRadius.curve);
        BuildNormalizedDistanceFromRootList();
        for (int i = 0; i < 100; i++) {
            if (!TryUpdateSerialization()) {
                break;
            }
        }
        if (jiggleColliders is { Length: > 32 }) {
            Debug.LogWarning("JigglePhysics: Maximum of 32 personal Jiggle Colliders are supported per tree. Extra colliders will be dropped.");
            Array.Resize(ref jiggleColliders, 32);
        }
    }
    public void BuildNormalizedDistanceFromRootList() {
        if (!rootBone) {
            return;
        }
        JigglePhysics.VisitForLength(rootBone, this, rootBone.position, 0f, out var totalLength);
        var data = new List<JiggleTransformCachedData>();
        VisitAndSetCacheData(data, rootBone, rootBone.position, 0f, totalLength);
        transformCachedData = data.ToArray();
        RegenerateCacheLookup();
    }
    
    private void VisitAndSetCacheData(List<JiggleTransformCachedData> data, Transform t, Vector3 lastPosition, float currentLength, float totalLength) {
        if (t == null || GetIsExcluded(t)) {
            return;
        }
        var validChildrenCount = GetValidChildrenCount(t);
        var scale = t.lossyScale;
        currentLength += Vector3.Distance(lastPosition, t.position);
        t.GetLocalPositionAndRotation(out var localPosition, out var localRotation);
        var position = t.position;
        data.Add(new JiggleTransformCachedData() {
            bone = t,
            restLocalPosition = localPosition,
            restLocalRotation = new Vector4(localRotation.x, localRotation.y, localRotation.z, localRotation.w),
            normalizedDistanceFromRoot = currentLength / totalLength,
            lossyScale = (scale.x + scale.y + scale.z)/3f,
        });
        for (int i = 0; i < validChildrenCount; i++) {
            var child = GetValidChild(t, i);
            VisitAndSetCacheData(data, child, position, currentLength, totalLength);
        }
    }

    public int GetValidChildrenCount(Transform t) {
        if (t == null) return 0;
        int count = 0;
        var childCount = t.childCount;
        for(int i=0;i<childCount;i++) {
            var child = t.GetChild(i);
            if (child == null || GetIsExcluded(child)) continue;
            count++;
        }
        return count;
    }

    public Transform GetValidChild(Transform t, int index) {
        if (t == null) return null;
        int count = 0;
        var childCount = t.childCount;
        for(int i=0;i<childCount;i++) {
            var child = t.GetChild(i);
            if (child == null || GetIsExcluded(child)) continue;
            if (count == index) {
                return child;
            }
            count++;
        }
        return null;
    }
    
    public void GetJiggleColliderTransforms(List<Transform> colliderTransforms) {
        colliderTransforms.Clear();
        var count = jiggleColliders.Length;
        for(int i=0;i<count;i++) {
            colliderTransforms.Add(jiggleColliders[i].transform);
        }
    }
    
    public bool GetHasRootTransformError() => !rootBone;
    public bool GetCacheIsValid() {
        if (transformCachedData is not { Length: > 0 } || transformToCachedDataMap == null || transformToCachedDataMap.Count != transformCachedData.Length) {
            return false;
        }
        var count = transformCachedData.Length;
        for (int i = 0; i < count; i++) {
            if (!transformCachedData[i].bone) return false;
        }
        return true;
    }
    public JiggleTransformCachedData GetCache(Transform t) {
#if UNITY_EDITOR
        if (transformToCachedDataMap == null) {
            throw new InvalidOperationException("JiggleRigData: Cache lookup not initialized. Call RegenerateCacheLookup() first.");
        }
        if (!transformToCachedDataMap.TryGetValue(t, out var cachedData)) {
            throw new KeyNotFoundException($"JiggleRigData: Transform '{t.name}' not found in cache. Ensure it is a child of the root bone and not excluded.");
        }
        return cachedData;
#else
        return transformToCachedDataMap[t];
#endif
    }

    /// <summary>
    /// The rig root's current name: the serialized chain root (index 0 is always the root) when
    /// present, otherwise the live <see cref="rootBone"/>. Null when neither exists.
    /// </summary>
    public string GetCachedRootName() {
        var cached = transformCachedData;
        if (cached is { Length: > 0 } && cached[0].bone != null) {
            return cached[0].bone.name;
        }
        return rootBone != null ? rootBone.name : null;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> may serve as this rig's new root: its name equals
    /// the rig's current root name. When the rig has no cached chain and no existing root there is
    /// nothing to compare, so any non-null candidate is accepted (fresh/uninitialized rig).
    /// </summary>
    private bool IsValidNewRoot(Transform candidate) {
        var expectedName = GetCachedRootName();
        if (string.IsNullOrEmpty(expectedName)) {
            return true;
        }
        return candidate.name == expectedName;
    }

    /// <summary>
    /// FORK PATCH (P2): re-points the rig at a replacement hierarchy by resolving every cached
    /// chain transform through <paramref name="resolveByName"/> using its ORIGINAL name, which
    /// stays valid after RemapBones/name-based rebinding of a swapped body part.
    /// <para>
    /// <paramref name="newRoot"/> must be the replacement for THIS rig's root bone (normally
    /// obtained by resolving the rig's own root name through <paramref name="resolveByName"/>),
    /// NOT a hierarchy root such as the whole skeleton. The name of the supplied root is checked
    /// against the rig's current root name and a mismatch fails the rebind with a clear reason, so
    /// a wrong wiring can never silently commit.
    /// </para>
    /// <para>
    /// All-or-nothing and side-effect-free on failure: every name is resolved (and the new
    /// hierarchy validated) before any field is touched. On failure the rig is untouched.
    /// On success the root, normalized-distance cache, and rest-pose cache are rebuilt from the
    /// new hierarchy, and the transform lookup is regenerated for the new transform set.
    /// </para>
    /// </summary>
    /// <param name="newRoot">Replacement for this rig's root bone (not a hierarchy/skeleton root).</param>
    /// <param name="resolveByName">Name → Transform resolver (null return = unresolvable).</param>
    /// <param name="failReason">Why the rebind failed, when false is returned.</param>
    public bool TryRebind(Transform newRoot, Func<string, Transform> resolveByName, out string failReason) {
        if (newRoot == null) {
            failReason = "newRoot is null";
            return false;
        }
        if (resolveByName == null) {
            failReason = "resolveByName is null";
            return false;
        }

        // FORK PATCH (P2): guard against a newRoot that belongs to a different rig (e.g. the
        // hierarchy root being passed for every rig). Accept any non-null root only when the rig
        // has no cached chain and no existing root to compare against (fresh/uninitialized data).
        if (!IsValidNewRoot(newRoot)) {
            var expectedName = GetCachedRootName();
            failReason = $"newRoot '{newRoot.name}' does not match rig root '{expectedName}'";
            return false;
        }

        var oldCachedData = transformCachedData;
        if (oldCachedData is not { Length: > 0 }) {
            // No cached chain to follow (fresh/invalid data) — rebuild straight from the new root.
            rootBone = newRoot;
            BuildNormalizedDistanceFromRootList();
            failReason = null;
            return true;
        }

        // Phase 1 — RESOLVE: validate that the entire chain maps into the new hierarchy by name.
        // No mutations happen in this phase, so an unresolvable name leaves the rig untouched.
        var resolved = new Transform[oldCachedData.Length];
        for (int i = 0; i < oldCachedData.Length; i++) {
            var bone = oldCachedData[i].bone;
            if (bone == null) {
                failReason = $"cached chain transform at index {i} is null";
                return false;
            }
            var match = resolveByName(bone.name);
            if (match == null) {
                failReason = $"could not resolve bone '{bone.name}' in the new hierarchy";
                return false;
            }
            resolved[i] = match;
        }

        // Sanity-check the resolved hierarchy before committing: two different source bones must
        // not collapse onto the same transform (a resolver that returns the wrong node would
        // otherwise build a corrupt tree).
        for (int i = 0; i < resolved.Length; i++) {
            for (int j = i + 1; j < resolved.Length; j++) {
                if (resolved[i] == resolved[j]) {
                    failReason = $"bones '{oldCachedData[i].bone.name}' and '{oldCachedData[j].bone.name}' both resolved to '{resolved[i].name}'";
                    return false;
                }
            }
        }

        // Phase 2 — BUILD: regenerate the full cache from the new hierarchy on a LOCAL copy so a
        // failed coverage check below still leaves the rig completely untouched.
        var candidate = this;
        candidate.rootBone = newRoot;
        candidate.BuildNormalizedDistanceFromRootList();
        var newCachedData = candidate.transformCachedData;

        // Phase 3 — VERIFY: every resolved chain bone must be represented in the rebuilt cache.
        // If the new hierarchy is missing a bone (or excludes it), fail rather than silently
        // simulating a shorter/inconsistent chain.
        var rebuiltBones = new HashSet<Transform>();
        for (int j = 0; j < newCachedData.Length; j++) {
            rebuiltBones.Add(newCachedData[j].bone);
        }
        for (int i = 0; i < resolved.Length; i++) {
            if (!rebuiltBones.Contains(resolved[i])) {
                failReason = $"bone '{oldCachedData[i].bone.name}' resolved to '{resolved[i].name}' but is missing from the rebuilt chain (excluded or not a descendant of the new root)";
                return false;
            }
        }

        // Phase 4 — COMMIT: apply the new hierarchy and rebuild rest/normalized caches.
        // ResampleRestPose() also regenerates the lookup map for the new transform set.
        transformCachedData = newCachedData;
        transformToCachedDataMap = candidate.transformToCachedDataMap;
        rootBone = newRoot;
        ResampleRestPose();
        failReason = null;
        return true;
    }

    /// <summary>
    /// Sends updated parameters to the jiggle tree on the jobs side. Uses the provided list to prevent allocations.
    /// </summary>
    /// <param name="tree">Tree to update</param>
    /// <param name="parameters">empty list purely used to prevent allocations</param>
    public void UpdateParameters(JiggleTree tree, List<JigglePointParameters> parameters) {
        parameters.Clear();
        var bones = tree.bones;
        if (bones == null) {
            return;
        }
        var boneCount = bones.Length;
        for (int i = 0; i < boneCount; i++) {
            var bone = bones[i];
            var cache = GetCache(bone);
            parameters.Add(GetJiggleBoneParameter(cache.normalizedDistanceFromRoot));
        }
        tree.SetParameters(parameters);
    }
    
    public JigglePointParameters GetJiggleBoneParameter(float normalizedDistanceFromRoot) {
        return jiggleTreeInputParameters.ToJigglePointParameters(normalizedDistanceFromRoot);
    }
    
    public Transform[] GetJiggleBoneTransforms() {
        return rootBone.GetComponentsInChildren<Transform>();
    }
    
    public bool IsValid(Transform root) => (rootBone && rootBone.IsChildOf(root));
    public static JiggleRigData Default() {
        return new JiggleRigData {
            rootBone = null,
            serializedVersion = "v0.0.2",
            hasSerializedData = true,
            excludeRoot = false,
            jiggleTreeInputParameters = JiggleTreeInputParameters.Default(),
            excludedTransforms = Array.Empty<Transform>(),
            transformCachedData = Array.Empty<JiggleTransformCachedData>(),
            jiggleColliders = Array.Empty<JiggleColliderSerializable>() 
        };
    }

    public void OnDrawGizmosSelected() {
        if (jiggleColliders != null) {
            var count = jiggleColliders.Length;
            for(int i=0;i<count;i++) {
                jiggleColliders[i].OnDrawGizmosSelected();
            }
        }
        
        if (!rootBone) return;
        Gizmos.color = new Color(0.9607844f, 0.9607844f, 0.9607844f, 1f);
        var jiggleTree = JigglePhysics.CreateJiggleTree(this, null);
        var points = jiggleTree.points;
        var parameters = jiggleTree.parameters;
        var pointCount = points.Length;
        var cam = Camera.current;
        for (var index = 0; index < pointCount; index++) {
            var simulatedPoint = points[index];
            if (simulatedPoint.parentIndex == -1) continue;
            if (!points[simulatedPoint.parentIndex].hasTransform) continue;
            DrawBone(points[simulatedPoint.parentIndex].position, simulatedPoint.position, jiggleTree.bones[index].lossyScale, parameters[simulatedPoint.parentIndex], cam);
        }
    }
    
    private static void DrawWireDisc(Vector3 center, Vector3 normal, float radius, int segmentCount = 32) {
        normal.Normalize();
        Vector3 up = normal;
        Vector3 forward = Vector3.Slerp(up, -up, 0.5f);
        Vector3 right = Vector3.Cross(up, forward).normalized * radius;

        float angleStep = 360f / segmentCount;
        Vector3 prevPoint = center + right;
        for (int i = 1; i <= segmentCount; i++) {
            float angle = angleStep * i;
            Quaternion rot = Quaternion.AngleAxis(angle, up);
            Vector3 nextPoint = center + rot * right;
            Gizmos.DrawLine(prevPoint, nextPoint);
            prevPoint = nextPoint;
        }
    }
    
    private void DrawBone(Vector3 boneHead, Vector3 boneTail, Vector3 boneScale, JigglePointParameters jigglePointParameters, Camera cam) {
        var camForward = cam.transform.forward;
        var scale = jigglePointParameters.collisionRadius * (boneScale.x + boneScale.y + boneScale.z)/3f;
        DrawWireDisc(boneHead, camForward, scale);
        Gizmos.DrawLine(boneHead, boneTail);
        var boneDirection = (boneTail - boneHead).normalized;
        var angleLimitScale = 0.05f;
        DrawWireDisc(boneHead + boneDirection * (angleLimitScale * Mathf.Cos(jigglePointParameters.angleLimit * Mathf.Deg2Rad)),
            boneDirection,
            angleLimitScale * Mathf.Sin(jigglePointParameters.angleLimit * Mathf.Deg2Rad));
    }
#if UNITY_EDITOR
#endif
}
}
