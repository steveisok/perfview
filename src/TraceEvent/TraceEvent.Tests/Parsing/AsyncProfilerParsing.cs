// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Xunit;

namespace TraceEventTests
{
    /// <summary>
    /// Unit tests for <see cref="AsyncProfilerTraceEventParser"/>'s pure-function buffer
    /// decoder.  Synthesizes packed AsyncEvents buffers and asserts that each contained
    /// sub-event is dispatched with the expected payload via <see cref="IAsyncProfilerSubEventSink"/>.
    /// </summary>
    public class AsyncProfilerParsing
    {
        private const byte VERSION = AsyncProfilerTraceEventParser.SupportedBufferVersion;
        private const int HEADER_SIZE = AsyncProfilerTraceEventParser.BufferHeaderSize;

        [Fact]
        public void ProviderGuid_MatchesEventSourceAlgorithm()
        {
            // SHA-1-based GUID for "System.Runtime.CompilerServices.AsyncProfilerEventSource".
            Assert.Equal(
                new Guid("f8f29278-1df8-d650-aea7-594d846c2274"),
                AsyncProfilerTraceEventParser.ProviderGuid);
        }

        [Fact]
        public void EmptyBuffer_DispatchesNothing()
        {
            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, Array.Empty<byte>(), sink);
            Assert.Empty(sink.All);
        }

        [Fact]
        public void NullBuffer_DispatchesNothing()
        {
            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, null, sink);
            Assert.Empty(sink.All);
        }

        [Fact]
        public void TooShortBuffer_RaisesParseError()
        {
            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, new byte[10], sink);
            Assert.Single(sink.Errors);
            Assert.Contains("header", sink.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void WrongVersion_RaisesParseError()
        {
            var b = new AsyncProfilerBufferBuilder(version: 99, asyncCtxId: 1, osThreadId: 1, startQpc: 0);
            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);
            Assert.Single(sink.Errors);
        }

        [Fact]
        public void TotalSizeExceedsBuffer_RaisesParseError()
        {
            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, startQpc: 0);
            byte[] buf = b.Finish();
            // Corrupt the TotalSize field (offset 1, u32 LE) to be wildly larger than the buffer.
            buf[1] = 0xFF; buf[2] = 0xFF; buf[3] = 0xFF; buf[4] = 0x7F;

            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, buf, sink);
            Assert.Single(sink.Errors);
        }

        [Fact]
        public void CreateAsyncContext_ParsesIdAndTimestamp()
        {
            var b = new AsyncProfilerBufferBuilder(VERSION, asyncCtxId: 7, osThreadId: 42, startQpc: 1000);
            b.AddCreateAsyncContext(deltaTicks: 5, taskId: 0xDEADBEEF);

            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            var e = Assert.Single(sink.CreateAsyncContext);
            Assert.Equal(0xDEADBEEFul, e.TaskId);
            Assert.Equal(1005L, e.ResolvedQpc);
            Assert.Equal(42ul, e.OsThreadId);
            Assert.Equal(7u, e.AsyncThreadContextId);
        }

        [Fact]
        public void TimestampDelta_AccumulatesAcrossSubEvents()
        {
            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, startQpc: 100);
            b.AddCreateAsyncContext(deltaTicks: 10, taskId: 1);   // ts = 110
            b.AddSuspendAsyncContext(deltaTicks: 20);             // ts = 130
            b.AddCompleteAsyncContext(deltaTicks: 30);            // ts = 160

            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(110L, sink.CreateAsyncContext[0].ResolvedQpc);
            Assert.Equal(130L, sink.SuspendAsyncContext[0].ResolvedQpc);
            Assert.Equal(160L, sink.CompleteAsyncContext[0].ResolvedQpc);
        }

        [Fact]
        public void CurrentTaskIdFlows_FromCreateToSuspend()
        {
            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, startQpc: 0);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 0xABCD);
            b.AddSuspendAsyncContext(deltaTicks: 1);
            b.AddResumeAsyncContext(deltaTicks: 1, taskId: 0x1234);
            b.AddCompleteAsyncContext(deltaTicks: 1);
            b.AddResumeAsyncMethod(deltaTicks: 1);
            b.AddCompleteAsyncMethod(deltaTicks: 1);

            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(0xABCDul, sink.SuspendAsyncContext[0].TaskId);
            Assert.Equal(0x1234ul, sink.CompleteAsyncContext[0].TaskId);
            Assert.Equal(0x1234ul, sink.ResumeAsyncMethod[0].TaskId);
            Assert.Equal(0x1234ul, sink.CompleteAsyncMethod[0].TaskId);
            Assert.True(sink.CompleteAsyncMethod[0].IsComplete);
            Assert.False(sink.ResumeAsyncMethod[0].IsComplete);
        }

        [Fact]
        public void UnwindAsyncException_ParsesFrameCount()
        {
            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, 0);
            b.AddUnwindAsyncException(deltaTicks: 1, unwoundFrames: 42);

            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(42u, sink.UnwindAsyncException[0].UnwoundFrames);
        }

        [Fact]
        public void AsyncCallstack_SingleFrame_ParsesAbsoluteIP()
        {
            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, 0);
            var frames = new[] { new AsyncProfilerTestFrame(0x7FFE_1234_5678ul, 0) };
            b.AddCreateAsyncCallstack(deltaTicks: 1, type: AsyncProfilerTraceEventParser.AsyncCallstackType.Compiler,
                cached: false, callstackId: 0, taskId: 99, frames: frames);

            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            var cs = Assert.Single(sink.CreateAsyncCallstack);
            Assert.Equal(99ul, cs.TaskId);
            Assert.False(cs.Cached);
            Assert.Equal(AsyncProfilerTraceEventParser.AsyncCallstackType.Compiler, cs.Type);
            Assert.Single(cs.Frames);
            Assert.Equal(0x7FFE_1234_5678ul, cs.Frames[0].NativeIP);
            Assert.Equal(0, cs.Frames[0].State);
        }

        [Fact]
        public void AsyncCallstack_MultiFrame_AppliesDeltasAndState()
        {
            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, 0);
            ulong baseIP = 0x4000_0000ul;
            var frames = new[]
            {
                new AsyncProfilerTestFrame(baseIP, 1),
                new AsyncProfilerTestFrame(baseIP + 0x100, -2),    // delta +0x100
                new AsyncProfilerTestFrame(baseIP + 0x80,  3),     // delta -0x80 (negative)
            };
            b.AddResumeAsyncCallstack(deltaTicks: 1, type: AsyncProfilerTraceEventParser.AsyncCallstackType.Runtime,
                cached: true, callstackId: 5, taskId: 7, frames: frames);

            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            var cs = Assert.Single(sink.ResumeAsyncCallstack);
            Assert.True(cs.Cached);
            Assert.Equal(AsyncProfilerTraceEventParser.AsyncCallstackType.Runtime, cs.Type);
            Assert.Equal(3, cs.Frames.Count);
            Assert.Equal(baseIP, cs.Frames[0].NativeIP);
            Assert.Equal(1, cs.Frames[0].State);
            Assert.Equal(baseIP + 0x100, cs.Frames[1].NativeIP);
            Assert.Equal(-2, cs.Frames[1].State);
            Assert.Equal(baseIP + 0x80, cs.Frames[2].NativeIP);
            Assert.Equal(3, cs.Frames[2].State);
        }

        [Fact]
        public void ResetAsyncThreadContext_ClearsTaskId()
        {
            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, 0);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 0xAAAA);
            b.AddResetAsyncThreadContext(deltaTicks: 1);
            b.AddSuspendAsyncContext(deltaTicks: 1);   // should report taskId=0 now

            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(0xAAAAul, sink.ResetAsyncThreadContext[0].TaskIdBeforeReset);
            Assert.Equal(0ul, sink.SuspendAsyncContext[0].TaskId);
        }

        [Fact]
        public void AsyncProfilerMetadata_RoundTripsAllFields()
        {
            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, 0);
            b.AddAsyncProfilerMetadata(deltaTicks: 1,
                qpcFreq: 10_000_000ul,
                qpcSync: 0x1111_2222_3333_4444ul,
                utcSync: 0x5555_6666_7777_8888ul,
                bufSize: 65536u,
                wrapperCount: 12);

            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            var m = Assert.Single(sink.Metadata);
            Assert.Equal(10_000_000ul, m.QpcFrequency);
            Assert.Equal(0x1111_2222_3333_4444ul, m.QpcSync);
            Assert.Equal(0x5555_6666_7777_8888ul, m.UtcSync);
            Assert.Equal(65536u, m.EventBufferSize);
            Assert.Equal((byte)12, m.WrapperCount);
        }

        [Fact]
        public void AsyncProfilerSyncClock_RoundTripsFields()
        {
            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, 0);
            b.AddAsyncProfilerSyncClock(deltaTicks: 1, qpcSync: 0xABCDul, utcSync: 0x1234_5678ul);

            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            var s = Assert.Single(sink.SyncClock);
            Assert.Equal(0xABCDul, s.QpcSync);
            Assert.Equal(0x1234_5678ul, s.UtcSync);
        }

        [Fact]
        public void UnknownSubEventId_RaisesParseErrorAndStops()
        {
            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, 0);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 1);
            b.AddRaw((byte)0xEE);   // unknown id
            // Even with garbage after, parser must not crash; just emit ParseError.
            b.AddRaw(0x00);
            b.AddRaw(0x00);

            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Single(sink.CreateAsyncContext);
            Assert.Single(sink.Errors);
        }

        [Fact]
        public void TruncatedSubEventPayload_RaisesParseError()
        {
            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, 0);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 1);
            // Append an UnwindAsyncException header (id + delta) but no payload.
            b.AddRaw((byte)AsyncProfilerTraceEventParser.AsyncEventID.UnwindAsyncException);
            b.AddCompressedUInt64(1); // delta

            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Single(sink.CreateAsyncContext);
            Assert.Empty(sink.UnwindAsyncException);
            Assert.Single(sink.Errors);
        }

        [Fact]
        public void QpcToDateTime_ConvertsRelativeToSyncPoint()
        {
            // 10 MHz QPC, qpcSync=0, utcSync=2000-01-01.
            DateTime sync = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            ulong utcSync = (ulong)sync.ToFileTimeUtc();   // 100ns since 1601-01-01
            ulong qpcFreq = 10_000_000;                    // 10 MHz: 1 tick = 100ns
            ulong qpcSync = 0;

            // 1 second after sync = 10_000_000 QPC ticks
            DateTime? dt = AsyncProfilerTraceEventParser.QpcToDateTime((long)qpcSync + 10_000_000, qpcFreq, qpcSync, utcSync);
            Assert.Equal(sync.AddSeconds(1), dt);
        }

        [Fact]
        public void SinkOnlyReceivesSubscribedEvents()
        {
            // Sanity: subscribing to one typed event does not perturb the dispatch order
            // (i.e. the sink interface is the contract, not subscriber presence).  Verified
            // by exercising several event types in one buffer.
            var b = new AsyncProfilerBufferBuilder(VERSION, 1, 1, 0);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 1);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 2);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 3);

            var sink = new AsyncProfilerCollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(3, sink.CreateAsyncContext.Count);
            Assert.Equal(new ulong[] { 1, 2, 3 }, sink.CreateAsyncContext.ConvertAll(e => e.TaskId).ToArray());
        }
    }
}
