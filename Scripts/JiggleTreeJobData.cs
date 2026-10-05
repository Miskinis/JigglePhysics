using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;

namespace GatorDragonGames.JigglePhysics {
public unsafe struct JiggleTreeJobData {
    public static bool operator ==(JiggleTreeJobData left, JiggleTreeJobData right) => left.Equals(right);
    public static bool operator !=(JiggleTreeJobData left, JiggleTreeJobData right) => !left.Equals(right);

    public int rootID;
    public uint pointCount;
    public uint transformIndexOffset;
    public uint colliderIndexOffset;
    public uint colliderCount;
    
    public int2 minExtentPosition;
    public int2 maxExtentPosition;
    
    public JiggleSimulatedPoint* points;
    public JigglePointParameters* parameters;
    public const int MAX_POINTS = 10000;

    // FORK PATCH (P3b): last frame index for which a sanitize/report was emitted for this tree
    // slice. -1 means "not reported yet". Copied with the struct, so it throttles across frames.
    public int lastSanitizeReportedFrame;

    public JiggleTreeJobData(int rootID, int transformIndexOffset, int colliderIndexOffset, int colliderCount, JiggleSimulatedPoint[] inputPoints, JigglePointParameters[] inputParameters) {
        this.rootID = rootID;
        pointCount = (uint)inputPoints.Length;
        this.colliderIndexOffset = (uint)colliderIndexOffset;
        this.transformIndexOffset = (uint)transformIndexOffset;
        this.colliderCount = (uint)colliderCount;
        points = (JiggleSimulatedPoint*)UnsafeUtility.Malloc(
            sizeof(JiggleSimulatedPoint) * pointCount,
            UnsafeUtility.AlignOf<JiggleSimulatedPoint>(),
            Allocator.Persistent
        );
        parameters = (JigglePointParameters*)UnsafeUtility.Malloc(
            sizeof(JigglePointParameters) * pointCount,
            UnsafeUtility.AlignOf<JigglePointParameters>(),
            Allocator.Persistent
        );
        fixed (JiggleSimulatedPoint* src = inputPoints) {
            UnsafeUtility.MemCpy(points, src, sizeof(JiggleSimulatedPoint) * pointCount);
        }
        fixed (JigglePointParameters* src = inputParameters) {
            UnsafeUtility.MemCpy(parameters, src, sizeof(JigglePointParameters) * pointCount);
        }

        minExtentPosition = new int2(0);
        maxExtentPosition = new int2(0);
        lastSanitizeReportedFrame = -1;
    }

    public void Set(int newRootID, JiggleSimulatedPoint[] inputPoints, JigglePointParameters[] inputParameters, int colliderCount) {
        this.colliderCount = (uint)colliderCount;
        rootID = newRootID;
        // FORK PATCH (P3b): a regenerated tree is new corruption-tracking territory.
        lastSanitizeReportedFrame = -1;
        if (inputPoints.Length != pointCount) {
            Dispose();
            pointCount = (uint)inputPoints.Length;
            points = (JiggleSimulatedPoint*)UnsafeUtility.Malloc(
                Marshal.SizeOf<JiggleSimulatedPoint>() * pointCount,
                UnsafeUtility.AlignOf<JiggleSimulatedPoint>(),
                Allocator.Persistent
            );
            parameters = (JigglePointParameters*)UnsafeUtility.Malloc(
                Marshal.SizeOf<JigglePointParameters>() * pointCount,
                UnsafeUtility.AlignOf<JigglePointParameters>(),
                Allocator.Persistent
            );
        }
        fixed (JiggleSimulatedPoint* src = inputPoints) {
            UnsafeUtility.MemCpy(points, src, sizeof(JiggleSimulatedPoint) * pointCount);
        }
        fixed (JigglePointParameters* src = inputParameters) {
            UnsafeUtility.MemCpy(parameters, src, sizeof(JigglePointParameters) * pointCount);
        }
    }

    public void SetParameters(JigglePointParameters[] inputParameters) {
        Assert.AreEqual(pointCount, inputParameters.Length);
        fixed (JigglePointParameters* src = inputParameters) {
            UnsafeUtility.MemCpy(parameters, src, sizeof(JigglePointParameters) * pointCount);
        }
    }

    public void Dispose() {
        if (points != null) {
            JigglePhysics.FreeOnComplete((IntPtr)points);
            points = null;
        }
        if (parameters != null) {
            JigglePhysics.FreeOnComplete((IntPtr)parameters);
            parameters = null;
        }
    }

    public void OnDrawGizmosSelected() {
        for (int i = 0; i < pointCount; i++) {
            var point = points[i];
            if (point.hasTransform) {
                Gizmos.DrawWireSphere(point.position, point.worldRadius);
            } else {
                if (point.parentIndex == -1) {
                    Gizmos.color = Color.magenta;
                    Gizmos.DrawWireSphere(point.pose, 0.05f);
                } else {
                    Gizmos.color = Color.magenta;
                    Gizmos.DrawWireSphere(point.position, 0.05f);
                }
            }
            if (point.childrenCount != 0) {
                var child = points[point.childrenIndices[0]];
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(point.position, child.position);
            }
        }
    }

    public void Sanitize() {
        for (int i = 0; i < pointCount; i++) {
            points[i].Sanitize((int)pointCount, i);
        }
    }

    /// <summary>
    /// FORK PATCH (P3b): validate-then-repair entry point used by the simulate job instead of
    /// throwing on the first bad frame. Returns true when the tree is safe to simulate. The
    /// caller decides whether to emit a one-shot report (see <see cref="lastSanitizeReportedFrame"/>).
    /// </summary>
    public bool ValidateAndSanitize() {
        var valid = GetIsValid(out _);
        if (!valid) {
            Sanitize();
            valid = GetIsValid(out _);
        }
        return valid;
    }

    /// <summary>
    /// FORK PATCH (P3b): true exactly once per frame/window for this tree slice, so a persistently
    /// corrupt tree reports once instead of spamming every simulation tick or throwing forever.
    /// </summary>
    public bool TryReportSanitize(int frame) {
        if (lastSanitizeReportedFrame == frame) {
            return false;
        }
        lastSanitizeReportedFrame = frame;
        return true;
    }

    public bool GetIsValid(out string failReason) {
        if (pointCount == 0 || pointCount > MAX_POINTS) {
            failReason = $"Invalid point count {pointCount}";
            return false;
        }
        if (points == null) {
            failReason = "Points pointer is null";
            return false;
        }
        if (parameters == null) {
            failReason = "Parameters pointer is null";
            return false;
        }
        for (int i = 0; i < pointCount; i++) {
            var point = points[i];
            if (!point.GetIsValid((int)pointCount, i, out failReason)) {
                return false;
            }
        }

        failReason = "All good!";
        return true;
    }
}
}