// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Pure matching logic for stitching a runtime-async continuation chain into a native CPU
    /// callstack.  Kept free of TraceLog / StackSource types so it can be unit-tested with plain
    /// integer method-id arrays.
    ///
    /// With runtime-async, a CPU sample's native stack contains the *currently executing* async
    /// method (reached through threadpool dispatch → ContinuationWrapper → IL_STUB_AsyncResume
    /// plumbing) plus any synchronous callees below it.  The method's logical async CALLERS are
    /// not physically on the stack — they are tracked separately by the async profiler.  The
    /// captured async chain is ordered leaf → root (innermost first), with an optional
    /// <c>AsyncHelpers.Await</c> bridge frame as the very first (innermost) entry.
    ///
    /// The stitch therefore anchors on the chain's current method (the innermost real frame) and,
    /// once located in the native stack, the resume plumbing above it is replaced by the logical
    /// caller chain while the method and its native callees are kept.
    /// </summary>
    public static class AsyncStackStitcher
    {
        /// <summary>
        /// Locate the async chain's current method (<paramref name="asyncRealLeafToRoot"/>[0],
        /// the innermost real frame after any await-bridge frames have been stripped) within the
        /// native sampled stack (<paramref name="nativeRootToLeaf"/>).  Returns the native index
        /// of that method — the splice anchor.  Native frames at indices &lt; the anchor (the
        /// resume plumbing / threadpool dispatch) are replaced by the logical async callers; the
        /// anchor frame and everything at indices &gt; it (the genuine synchronous callees where
        /// CPU is being spent) are kept.
        ///
        /// Fails closed (returns false) when the current method is unresolved, absent from the
        /// native stack, or ambiguous (appears more than once, e.g. recursion) — in which case the
        /// caller should not fabricate a splice.
        ///
        /// Method ids are opaque non-negative integers (e.g. MethodIndex cast to int); a negative
        /// value means "unresolved".
        /// </summary>
        public static bool TryFindAnchor(IReadOnlyList<int> asyncRealLeafToRoot,
            IReadOnlyList<int> nativeRootToLeaf, out int nativeAnchorIndex)
        {
            nativeAnchorIndex = -1;
            if (asyncRealLeafToRoot == null || nativeRootToLeaf == null)
            {
                return false;
            }

            if (asyncRealLeafToRoot.Count == 0 || nativeRootToLeaf.Count == 0)
            {
                return false;
            }

            int current = asyncRealLeafToRoot[0];
            if (current < 0)
            {
                return false; // unresolved current method can't anchor a splice.
            }

            int found = -1;
            int count = 0;
            for (int i = 0; i < nativeRootToLeaf.Count; i++)
            {
                if (nativeRootToLeaf[i] == current)
                {
                    found = i;
                    count++;
                }
            }

            if (count != 1)
            {
                return false; // absent or ambiguous → fail closed.
            }

            nativeAnchorIndex = found;
            return true;
        }
    }
}
