// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Computers;
using Xunit;

namespace TraceEventTests
{
    /// <summary>
    /// Unit tests for <see cref="AsyncProfilerStackIndex"/> (the per-thread async-chain time
    /// index) and the pure <see cref="AsyncStackStitcher"/> subsequence matcher.  The index's
    /// state machine is driven directly through its internal core methods; no TraceLog needed.
    /// </summary>
    public class AsyncProfilerStackIndexTests
    {
        private const int Pid = 1;

        private static AsyncProfilerStackIndex NewIndex(double traceEndMSec = 1000.0)
            => new AsyncProfilerStackIndex(traceEndMSec);

        private static void Resume(AsyncProfilerStackIndex idx, ulong thread, uint ctx, double t)
            => idx.OnResumeCore(Pid, thread, ctx, taskId: ctx, relMSec: t, callstack: null,
                callstackEventIndex: EventIndex.Invalid);

        private static void Suspend(AsyncProfilerStackIndex idx, ulong thread, uint ctx, double t)
            => idx.OnSuspendCore(Pid, thread, ctx, relMSec: t);

        // ---------------- index: basic bracket ----------------

        [Fact]
        public void SingleBracket_QueryInsideMatches_OutsideDoesNot()
        {
            var idx = NewIndex();
            Resume(idx, thread: 7, ctx: 100, t: 10);
            Suspend(idx, thread: 7, ctx: 100, t: 20);

            Assert.True(idx.TryGetActiveChainAt(Pid, 7, 15, out var hit));
            Assert.Equal(100u, hit.AsyncContextId);

            // Start is inclusive, end is exclusive, neighbors are misses.
            Assert.True(idx.TryGetActiveChainAt(Pid, 7, 10, out _));
            Assert.False(idx.TryGetActiveChainAt(Pid, 7, 20, out _));
            Assert.False(idx.TryGetActiveChainAt(Pid, 7, 9.999, out _));
            Assert.False(idx.TryGetActiveChainAt(Pid, 7, 25, out _));
        }

        [Fact]
        public void Gap_BetweenBrackets_IsNotCovered()
        {
            var idx = NewIndex();
            Resume(idx, 7, 100, 10);
            Suspend(idx, 7, 100, 20);
            Resume(idx, 7, 101, 30);
            Suspend(idx, 7, 101, 40);

            Assert.True(idx.TryGetActiveChainAt(Pid, 7, 15, out var a));
            Assert.Equal(100u, a.AsyncContextId);
            Assert.False(idx.TryGetActiveChainAt(Pid, 7, 25, out _)); // gap
            Assert.True(idx.TryGetActiveChainAt(Pid, 7, 35, out var b));
            Assert.Equal(101u, b.AsyncContextId);
        }

        [Fact]
        public void OtherThread_NotMatched()
        {
            var idx = NewIndex();
            Resume(idx, 7, 100, 10);
            Suspend(idx, 7, 100, 20);

            Assert.False(idx.TryGetActiveChainAt(Pid, 8, 15, out _));
            Assert.False(idx.TryGetActiveChainAt(Pid + 1, 7, 15, out _));
        }

        [Fact]
        public void UnclosedBracket_ClosesAtTraceEnd()
        {
            var idx = NewIndex(traceEndMSec: 500);
            Resume(idx, 7, 100, 10); // never suspended

            Assert.True(idx.TryGetActiveChainAt(Pid, 7, 400, out var hit));
            Assert.Equal(100u, hit.AsyncContextId);
            Assert.False(idx.TryGetActiveChainAt(Pid, 7, 500, out _)); // exclusive end
        }

        [Fact]
        public void ZeroLengthSegment_Skipped()
        {
            var idx = NewIndex();
            Resume(idx, 7, 100, 10);
            Suspend(idx, 7, 100, 10); // same time → no segment

            Assert.False(idx.TryGetActiveChainAt(Pid, 7, 10, out _));
        }

        // ---------------- index: nesting ----------------

        [Fact]
        public void NestedBrackets_InnerWinsThenOuterResumes()
        {
            // Outer ctx 100 resumes at 10; inner ctx 200 resumes at 12 and suspends at 15;
            // outer continues until it suspends at 20.
            var idx = NewIndex();
            Resume(idx, 7, 100, 10);
            Resume(idx, 7, 200, 12);
            Suspend(idx, 7, 200, 15);
            Suspend(idx, 7, 100, 20);

            Assert.True(idx.TryGetActiveChainAt(Pid, 7, 11, out var a));
            Assert.Equal(100u, a.AsyncContextId); // outer before inner

            Assert.True(idx.TryGetActiveChainAt(Pid, 7, 13, out var b));
            Assert.Equal(200u, b.AsyncContextId); // inner owns the nested time

            Assert.True(idx.TryGetActiveChainAt(Pid, 7, 17, out var c));
            Assert.Equal(100u, c.AsyncContextId); // outer resumes after inner suspends
        }

        [Fact]
        public void ThreadReuse_SameContextIdLaterIsDistinctSegment()
        {
            var idx = NewIndex();
            Resume(idx, 7, 100, 10);
            Suspend(idx, 7, 100, 20);
            Resume(idx, 7, 100, 50); // same ctx id reused later on same thread
            Suspend(idx, 7, 100, 60);

            Assert.False(idx.TryGetActiveChainAt(Pid, 7, 35, out _));
            Assert.True(idx.TryGetActiveChainAt(Pid, 7, 55, out var hit));
            Assert.Equal(100u, hit.AsyncContextId);
        }

        // ---------------- stitcher: anchor on current async method ----------------

        [Fact]
        public void Anchor_CurrentMethodFound_ReturnsItsNativeIndex()
        {
            // asyncReal leaf→root = [Transform(3), HandleRequest(2), WorkerLoop(1)];
            // native root→leaf = [plumbing(9), AsyncResume(8), Transform(3)].
            var asyncReal = new[] { 3, 2, 1 };
            var native = new[] { 9, 8, 3 };
            Assert.True(AsyncStackStitcher.TryFindAnchor(asyncReal, native, out int anchor));
            Assert.Equal(2, anchor); // index of the current method (3) in native
        }

        [Fact]
        public void Anchor_CurrentMethodHasNativeCallees()
        {
            // Transform(3) is above its sync callees in the native stack.
            var asyncReal = new[] { 3, 2, 1 };
            var native = new[] { 9, 8, 3, 4, 5 };
            Assert.True(AsyncStackStitcher.TryFindAnchor(asyncReal, native, out int anchor));
            Assert.Equal(2, anchor);
        }

        [Fact]
        public void Anchor_CurrentMethodAbsent_FailsClosed()
        {
            // async current = CommitAsync(7) but native executes a different continuation.
            var asyncReal = new[] { 7, 2, 1 };
            var native = new[] { 9, 8, 3 };
            Assert.False(AsyncStackStitcher.TryFindAnchor(asyncReal, native, out _));
        }

        [Fact]
        public void Anchor_RecursiveCurrentMethod_IsAmbiguous_FailsClosed()
        {
            var asyncReal = new[] { 3, 2, 1 };
            var native = new[] { 3, 8, 3 }; // method 3 twice → ambiguous
            Assert.False(AsyncStackStitcher.TryFindAnchor(asyncReal, native, out _));
        }

        [Fact]
        public void Anchor_UnresolvedCurrent_FailsClosed()
        {
            var asyncReal = new[] { -1, 2, 1 };
            var native = new[] { 9, 8, 3 };
            Assert.False(AsyncStackStitcher.TryFindAnchor(asyncReal, native, out _));
        }

        [Fact]
        public void Anchor_EmptyInputs_FailClosed()
        {
            Assert.False(AsyncStackStitcher.TryFindAnchor(new int[0], new[] { 1 }, out _));
            Assert.False(AsyncStackStitcher.TryFindAnchor(new[] { 1 }, new int[0], out _));
        }

        [Fact]
        public void Anchor_SingleFrameUniqueMatch()
        {
            var asyncReal = new[] { 5 };
            var native = new[] { 1, 5, 9 };
            Assert.True(AsyncStackStitcher.TryFindAnchor(asyncReal, native, out int anchor));
            Assert.Equal(1, anchor);
        }
    }
}
