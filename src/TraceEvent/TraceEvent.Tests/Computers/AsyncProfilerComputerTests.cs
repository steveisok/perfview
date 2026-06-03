// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Xunit;

namespace TraceEventTests
{
    /// <summary>
    /// Unit tests for <see cref="AsyncProfilerComputer"/>.  Because the computer
    /// implements <see cref="IAsyncProfilerSubEventSink"/> directly, tests can drive its
    /// state machine by handing it to <see cref="AsyncProfilerTraceEventParser.ParseBuffer"/>
    /// without instantiating a TraceEventSource.
    /// </summary>
    public class AsyncProfilerComputerTests
    {
        private const byte VERSION = AsyncProfilerTraceEventParser.SupportedBufferVersion;

        /// <summary>Process ID surfaced via sub-events when no raw event is attached (tests pass <c>null</c>).</summary>
        private const int Pid = 0;

        [Fact]
        public void Metadata_PopulatesClockAndFiresUpdate()
        {
            var computer = new AsyncProfilerComputer();
            int updates = 0;
            computer.ClockUpdated += _ => updates++;

            var b = new AsyncProfilerBufferBuilder(VERSION, asyncCtxId: 1, osThreadId: 7, startQpc: 0);
            b.AddAsyncProfilerMetadata(deltaTicks: 1, qpcFreq: 10_000_000, qpcSync: 100, utcSync: 200, bufSize: 4096, wrapperCount: 8);
            Drive(computer, b);

            Assert.True(computer.ClockKnown);
            Assert.Equal(10_000_000ul, computer.QpcFrequency);
            Assert.Equal(100ul, computer.QpcSync);
            Assert.Equal(200ul, computer.UtcSync);
            Assert.Equal((byte)8, computer.WrapperCount);
            Assert.Equal(1, updates);
        }

        [Fact]
        public void SyncClock_RefreshesSyncPointAndKeepsFrequency()
        {
            var computer = new AsyncProfilerComputer();
            int updates = 0;
            computer.ClockUpdated += _ => updates++;

            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 7, 0);
            b.AddAsyncProfilerMetadata(1, 10_000_000, 100, 200, 4096, 1);
            b.AddAsyncProfilerSyncClock(1, qpcSync: 555, utcSync: 999);
            Drive(computer, b);

            Assert.Equal(10_000_000ul, computer.QpcFrequency);
            Assert.Equal(555ul, computer.QpcSync);
            Assert.Equal(999ul, computer.UtcSync);
            Assert.Equal(2, updates);
        }

        [Fact]
        public void SyncClock_BeforeMetadata_DoesNotFireUpdate()
        {
            var computer = new AsyncProfilerComputer();
            int updates = 0;
            computer.ClockUpdated += _ => updates++;

            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, 0);
            b.AddAsyncProfilerSyncClock(1, 5, 5);
            Drive(computer, b);

            Assert.False(computer.ClockKnown);
            Assert.Equal(0, updates);
        }

        [Fact]
        public void Callstack_IsRememberedAndAttachedToNextResume()
        {
            var computer = new AsyncProfilerComputer();
            var resumes = new List<AsyncResumeInfo>();
            computer.AsyncContextResumed += resumes.Add;

            var frames = new[]
            {
                new AsyncProfilerTestFrame(0x4000_0000ul, 0),
                new AsyncProfilerTestFrame(0x4000_0010ul, 1),
            };
            var b = new AsyncProfilerBufferBuilder(VERSION, asyncCtxId: 5, osThreadId: 9, startQpc: 0);
            b.AddCreateAsyncContext(1, taskId: 42);
            b.AddCreateAsyncCallstack(1, AsyncProfilerTraceEventParser.AsyncCallstackType.Compiler, cached: false, callstackId: 0, taskId: 42, frames);
            b.AddSuspendAsyncContext(1);
            b.AddResumeAsyncContext(1, taskId: 42);
            Drive(computer, b);

            var resume = Assert.Single(resumes);
            Assert.Equal(42ul, resume.TaskId);
            Assert.NotNull(resume.Callstack);
            Assert.Equal(2, resume.Callstack.Value.Frames.Length);
            Assert.Equal(0x4000_0010ul, resume.Callstack.Value.Frames[1].NativeIP);
            Assert.Equal(Pid, resume.ProcessID);
        }

        [Fact]
        public void CachedCallstack_DoesNotOverwriteCurrentStack()
        {
            var computer = new AsyncProfilerComputer();
            var realFrames = new[] { new AsyncProfilerTestFrame(0x1234ul, 0) };
            var bogusFrames = new[] { new AsyncProfilerTestFrame(0x9999ul, 0) };

            var b = new AsyncProfilerBufferBuilder(VERSION, asyncCtxId: 1, osThreadId: 7, startQpc: 0);
            b.AddCreateAsyncContext(1, taskId: 1);
            b.AddCreateAsyncCallstack(1, AsyncProfilerTraceEventParser.AsyncCallstackType.Runtime, cached: false, callstackId: 0, taskId: 1, realFrames);
            b.AddResumeAsyncCallstack(1, AsyncProfilerTraceEventParser.AsyncCallstackType.Runtime, cached: true, callstackId: 0, taskId: 1, bogusFrames);
            Drive(computer, b);

            var stack = computer.GetCurrentAsyncStack(Pid, asyncContextId: 1);
            Assert.NotNull(stack);
            Assert.Single(stack.Value.Frames);
            Assert.Equal(0x1234ul, stack.Value.Frames[0].NativeIP);
        }

        [Fact]
        public void CompleteContext_DropsState()
        {
            var computer = new AsyncProfilerComputer();
            var frames = new[] { new AsyncProfilerTestFrame(0xABCDul, 0) };
            var b = new AsyncProfilerBufferBuilder(VERSION, asyncCtxId: 3, osThreadId: 11, startQpc: 0);
            b.AddCreateAsyncContext(1, taskId: 99);
            b.AddCreateAsyncCallstack(1, AsyncProfilerTraceEventParser.AsyncCallstackType.Compiler, false, 0, 99, frames);
            b.AddCompleteAsyncContext(1);
            Drive(computer, b);

            Assert.Null(computer.GetCurrentAsyncStack(Pid, asyncContextId: 3));
        }

        [Fact]
        public void ResumeAfterComplete_StartsWithoutOldStack()
        {
            var computer = new AsyncProfilerComputer();
            var resumes = new List<AsyncResumeInfo>();
            computer.AsyncContextResumed += resumes.Add;

            var frames = new[] { new AsyncProfilerTestFrame(0x1ul, 0) };
            var b = new AsyncProfilerBufferBuilder(VERSION, asyncCtxId: 1, osThreadId: 1, startQpc: 0);
            b.AddCreateAsyncContext(1, taskId: 1);
            b.AddCreateAsyncCallstack(1, AsyncProfilerTraceEventParser.AsyncCallstackType.Compiler, false, 0, 1, frames);
            b.AddCompleteAsyncContext(1);
            b.AddCreateAsyncContext(1, taskId: 2);
            b.AddResumeAsyncContext(1, taskId: 2);   // same async-context id reused; stack should be null
            Drive(computer, b);

            var resume = Assert.Single(resumes);
            Assert.Null(resume.Callstack);
        }

        [Fact]
        public void ResetThreadContext_ClearsCurrentTaskOnThread()
        {
            var computer = new AsyncProfilerComputer();
            var b = new AsyncProfilerBufferBuilder(VERSION, asyncCtxId: 1, osThreadId: 7, startQpc: 0);
            b.AddCreateAsyncContext(1, taskId: 0xAAAA);
            b.AddResetAsyncThreadContext(1);
            Drive(computer, b);

            Assert.Equal(0ul, computer.GetCurrentTaskId(Pid, osThreadId: 7));
        }

        [Fact]
        public void Create_RecordsCurrentTaskOnThread()
        {
            var computer = new AsyncProfilerComputer();
            var b = new AsyncProfilerBufferBuilder(VERSION, asyncCtxId: 1, osThreadId: 88, startQpc: 0);
            b.AddCreateAsyncContext(1, taskId: 0xCAFE);
            Drive(computer, b);

            Assert.Equal(0xCAFEul, computer.GetCurrentTaskId(Pid, osThreadId: 88));
        }

        [Fact]
        public void QpcToDateTime_UsesObservedClock()
        {
            var computer = new AsyncProfilerComputer();
            Assert.Null(computer.QpcToDateTime(123));   // before clock known

            DateTime sync = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            ulong utcSync = (ulong)sync.ToFileTimeUtc();

            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, 0);
            b.AddAsyncProfilerMetadata(1, qpcFreq: 10_000_000, qpcSync: 0, utcSync: utcSync, bufSize: 1, wrapperCount: 0);
            Drive(computer, b);

            DateTime? got = computer.QpcToDateTime(10_000_000);   // 1s after sync
            Assert.Equal(sync.AddSeconds(1), got);
        }

        [Fact]
        public void ResumeSuspendComplete_FireInOrderWithResolvedQpc()
        {
            var computer = new AsyncProfilerComputer();
            var events = new List<(string kind, ulong task, long qpc)>();
            computer.AsyncContextResumed += e => events.Add(("resume", e.TaskId, e.ResolvedQpc));
            computer.AsyncContextSuspended += e => events.Add(("suspend", e.TaskId, e.ResolvedQpc));
            computer.AsyncContextCompleted += e => events.Add(("complete", e.TaskId, e.ResolvedQpc));

            var frames = new[] { new AsyncProfilerTestFrame(0x1ul, 0) };
            var b = new AsyncProfilerBufferBuilder(VERSION, asyncCtxId: 1, osThreadId: 1, startQpc: 1000);
            b.AddCreateAsyncContext(10, taskId: 5);
            b.AddCreateAsyncCallstack(10, AsyncProfilerTraceEventParser.AsyncCallstackType.Compiler, false, 0, 5, frames);
            b.AddSuspendAsyncContext(10);
            b.AddResumeAsyncContext(10, taskId: 5);
            b.AddCompleteAsyncContext(10);
            Drive(computer, b);

            Assert.Equal(3, events.Count);
            Assert.Equal(("suspend", 5ul, 1030L), events[0]);
            Assert.Equal(("resume", 5ul, 1040L), events[1]);
            Assert.Equal(("complete", 5ul, 1050L), events[2]);
        }

        [Fact]
        public void Suspend_CarriesLastFullCallstack()
        {
            var computer = new AsyncProfilerComputer();
            AsyncResumeInfo lastSuspend = null;
            computer.AsyncContextSuspended += e => lastSuspend = e;

            var frames = new[] { new AsyncProfilerTestFrame(0xBEEF_0001ul, 0) };
            var b = new AsyncProfilerBufferBuilder(VERSION, 9, 9, 0);
            b.AddCreateAsyncContext(1, taskId: 77);
            b.AddCreateAsyncCallstack(1, AsyncProfilerTraceEventParser.AsyncCallstackType.Compiler, false, 0, 77, frames);
            b.AddSuspendAsyncContext(1);
            Drive(computer, b);

            Assert.NotNull(lastSuspend);
            Assert.NotNull(lastSuspend.Callstack);
            Assert.Equal(0xBEEF_0001ul, lastSuspend.Callstack.Value.Frames[0].NativeIP);
        }

        [Fact]
        public void MultipleContexts_KeepIndependentState()
        {
            var computer = new AsyncProfilerComputer();
            var framesA = new[] { new AsyncProfilerTestFrame(0xAAAAul, 0) };
            var framesB = new[] { new AsyncProfilerTestFrame(0xBBBBul, 0) };
            var b = new AsyncProfilerBufferBuilder(VERSION, asyncCtxId: 1, osThreadId: 1, startQpc: 0);
            b.AddCreateAsyncContext(1, taskId: 1);
            b.AddCreateAsyncCallstack(1, AsyncProfilerTraceEventParser.AsyncCallstackType.Compiler, false, 0, 1, framesA);
            Drive(computer, b);

            var b2 = new AsyncProfilerBufferBuilder(VERSION, asyncCtxId: 2, osThreadId: 2, startQpc: 0);
            b2.AddCreateAsyncContext(1, taskId: 2);
            b2.AddCreateAsyncCallstack(1, AsyncProfilerTraceEventParser.AsyncCallstackType.Compiler, false, 0, 2, framesB);
            Drive(computer, b2);

            Assert.Equal(0xAAAAul, computer.GetCurrentAsyncStack(Pid, 1).Value.Frames[0].NativeIP);
            Assert.Equal(0xBBBBul, computer.GetCurrentAsyncStack(Pid, 2).Value.Frames[0].NativeIP);
        }

        [Fact]
        public void DefaultCtor_HasNoClockAndEmptyState()
        {
            var computer = new AsyncProfilerComputer();
            Assert.False(computer.ClockKnown);
            Assert.Equal(0ul, computer.QpcFrequency);
            Assert.Null(computer.GetCurrentAsyncStack(0, 1));
            Assert.Equal(0ul, computer.GetCurrentTaskId(0, 1));
            Assert.Null(computer.QpcToDateTime(123));
        }

        // ------- helpers -------

        /// <summary>
        /// Drive the computer by parsing the buffer through it as a sink.  Passes
        /// <c>null</c> as the raw event; <see cref="AsyncProfilerSubEventArgs.ProcessID"/>
        /// reads as 0 in that case.
        /// </summary>
        private static void Drive(AsyncProfilerComputer sink, AsyncProfilerBufferBuilder b)
        {
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);
        }
    }
}
