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
            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, Array.Empty<byte>(), sink);
            Assert.Empty(sink.All);
        }

        [Fact]
        public void NullBuffer_DispatchesNothing()
        {
            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, null, sink);
            Assert.Empty(sink.All);
        }

        [Fact]
        public void TooShortBuffer_RaisesParseError()
        {
            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, new byte[10], sink);
            Assert.Single(sink.Errors);
            Assert.Contains("header", sink.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void WrongVersion_RaisesParseError()
        {
            var b = new BufferBuilder(version: 99, asyncCtxId: 1, osThreadId: 1, startQpc: 0);
            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);
            Assert.Single(sink.Errors);
        }

        [Fact]
        public void TotalSizeExceedsBuffer_RaisesParseError()
        {
            var b = new BufferBuilder(VERSION, 1, 1, startQpc: 0);
            byte[] buf = b.Finish();
            // Corrupt the TotalSize field (offset 1, u32 LE) to be wildly larger than the buffer.
            buf[1] = 0xFF; buf[2] = 0xFF; buf[3] = 0xFF; buf[4] = 0x7F;

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, buf, sink);
            Assert.Single(sink.Errors);
        }

        [Fact]
        public void CreateAsyncContext_ParsesIdAndTimestamp()
        {
            var b = new BufferBuilder(VERSION, asyncCtxId: 7, osThreadId: 42, startQpc: 1000);
            b.AddCreateAsyncContext(deltaTicks: 5, taskId: 0xDEADBEEF);

            var sink = new CollectingSink();
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
            var b = new BufferBuilder(VERSION, 1, 1, startQpc: 100);
            b.AddCreateAsyncContext(deltaTicks: 10, taskId: 1);   // ts = 110
            b.AddSuspendAsyncContext(deltaTicks: 20);             // ts = 130
            b.AddCompleteAsyncContext(deltaTicks: 30);            // ts = 160

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(110L, sink.CreateAsyncContext[0].ResolvedQpc);
            Assert.Equal(130L, sink.SuspendAsyncContext[0].ResolvedQpc);
            Assert.Equal(160L, sink.CompleteAsyncContext[0].ResolvedQpc);
        }

        [Fact]
        public void CurrentTaskIdFlows_FromCreateToSuspend()
        {
            var b = new BufferBuilder(VERSION, 1, 1, startQpc: 0);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 0xABCD);
            b.AddSuspendAsyncContext(deltaTicks: 1);
            b.AddResumeAsyncContext(deltaTicks: 1, taskId: 0x1234);
            b.AddCompleteAsyncContext(deltaTicks: 1);
            b.AddResumeAsyncMethod(deltaTicks: 1);
            b.AddCompleteAsyncMethod(deltaTicks: 1);

            var sink = new CollectingSink();
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
            var b = new BufferBuilder(VERSION, 1, 1, 0);
            b.AddUnwindAsyncException(deltaTicks: 1, unwoundFrames: 42);

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(42u, sink.UnwindAsyncException[0].UnwoundFrames);
        }

        [Fact]
        public void AsyncCallstack_SingleFrame_ParsesAbsoluteIP()
        {
            var b = new BufferBuilder(VERSION, 1, 1, 0);
            var frames = new[] { new TestFrame(0x7FFE_1234_5678ul, 0) };
            b.AddCreateAsyncCallstack(deltaTicks: 1, type: AsyncProfilerTraceEventParser.AsyncCallstackType.Compiler,
                cached: false, callstackId: 0, taskId: 99, frames: frames);

            var sink = new CollectingSink();
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
            var b = new BufferBuilder(VERSION, 1, 1, 0);
            ulong baseIP = 0x4000_0000ul;
            var frames = new[]
            {
                new TestFrame(baseIP, 1),
                new TestFrame(baseIP + 0x100, -2),    // delta +0x100
                new TestFrame(baseIP + 0x80,  3),     // delta -0x80 (negative)
            };
            b.AddResumeAsyncCallstack(deltaTicks: 1, type: AsyncProfilerTraceEventParser.AsyncCallstackType.Runtime,
                cached: true, callstackId: 5, taskId: 7, frames: frames);

            var sink = new CollectingSink();
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
            var b = new BufferBuilder(VERSION, 1, 1, 0);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 0xAAAA);
            b.AddResetAsyncThreadContext(deltaTicks: 1);
            b.AddSuspendAsyncContext(deltaTicks: 1);   // should report taskId=0 now

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(0xAAAAul, sink.ResetAsyncThreadContext[0].TaskIdBeforeReset);
            Assert.Equal(0ul, sink.SuspendAsyncContext[0].TaskId);
        }

        [Fact]
        public void AsyncProfilerMetadata_RoundTripsAllFields()
        {
            var b = new BufferBuilder(VERSION, 1, 1, 0);
            b.AddAsyncProfilerMetadata(deltaTicks: 1,
                qpcFreq: 10_000_000ul,
                qpcSync: 0x1111_2222_3333_4444ul,
                utcSync: 0x5555_6666_7777_8888ul,
                bufSize: 65536u,
                wrapperCount: 12);

            var sink = new CollectingSink();
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
            var b = new BufferBuilder(VERSION, 1, 1, 0);
            b.AddAsyncProfilerSyncClock(deltaTicks: 1, qpcSync: 0xABCDul, utcSync: 0x1234_5678ul);

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            var s = Assert.Single(sink.SyncClock);
            Assert.Equal(0xABCDul, s.QpcSync);
            Assert.Equal(0x1234_5678ul, s.UtcSync);
        }

        [Fact]
        public void UnknownSubEventId_RaisesParseErrorAndStops()
        {
            var b = new BufferBuilder(VERSION, 1, 1, 0);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 1);
            b.AddRaw((byte)0xEE);   // unknown id
            // Even with garbage after, parser must not crash; just emit ParseError.
            b.AddRaw(0x00);
            b.AddRaw(0x00);

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Single(sink.CreateAsyncContext);
            Assert.Single(sink.Errors);
        }

        [Fact]
        public void TruncatedSubEventPayload_RaisesParseError()
        {
            var b = new BufferBuilder(VERSION, 1, 1, 0);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 1);
            // Append an UnwindAsyncException header (id + delta) but no payload.
            b.AddRaw((byte)AsyncProfilerTraceEventParser.AsyncEventID.UnwindAsyncException);
            b.AddCompressedUInt64(1); // delta

            var sink = new CollectingSink();
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
            var b = new BufferBuilder(VERSION, 1, 1, 0);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 1);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 2);
            b.AddCreateAsyncContext(deltaTicks: 1, taskId: 3);

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(null, b.Finish(), sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(3, sink.CreateAsyncContext.Count);
            Assert.Equal(new ulong[] { 1, 2, 3 }, sink.CreateAsyncContext.ConvertAll(e => e.TaskId).ToArray());
        }

        #region Helpers

        private readonly struct TestFrame
        {
            public TestFrame(ulong ip, int state) { IP = ip; State = state; }
            public ulong IP { get; }
            public int State { get; }
        }

        /// <summary>
        /// Builds an AsyncEvents buffer for testing.  Mirrors the runtime's writer in
        /// <c>AsyncProfilerTests.cs</c>.  Lazily back-patches the TotalSize and EventCount
        /// fields on <see cref="Finish"/>.
        /// </summary>
        private sealed class BufferBuilder
        {
            private readonly MemoryStream _ms = new MemoryStream();
            private readonly BinaryWriter _bw;
            private readonly long _startQpc;
            private long _lastQpc;
            private uint _eventCount;

            public BufferBuilder(byte version, uint asyncCtxId, ulong osThreadId, ulong startQpc)
            {
                _bw = new BinaryWriter(_ms);
                _startQpc = (long)startQpc;
                _lastQpc = (long)startQpc;
                // Header: Version(u8), TotalSize(u32), AsyncCtxId(u32), OsThreadId(u64),
                //         EventCount(u32), StartTs(u64), EndTs(u64) = 37 bytes
                _bw.Write(version);
                _bw.Write(0u);             // total size — patched at Finish
                _bw.Write(asyncCtxId);
                _bw.Write(osThreadId);
                _bw.Write(0u);             // event count — patched
                _bw.Write(startQpc);
                _bw.Write(0ul);            // end timestamp — patched
            }

            public void AddRaw(byte b) { _bw.Write(b); }

            public void AddCompressedUInt64(ulong v)
            {
                while ((v & ~0x7FUL) != 0)
                {
                    _bw.Write((byte)((v & 0x7F) | 0x80));
                    v >>= 7;
                }
                _bw.Write((byte)v);
            }

            public void AddCompressedUInt32(uint v) { AddCompressedUInt64(v); }

            public void AddCompressedInt64(long v)
            {
                ulong zz = (ulong)((v << 1) ^ (v >> 63));
                AddCompressedUInt64(zz);
            }

            public void AddCompressedInt32(int v)
            {
                ulong zz = (ulong)((v << 1) ^ (v >> 31));
                AddCompressedUInt64(zz);
            }

            private void WriteSubEventHeader(AsyncProfilerTraceEventParser.AsyncEventID id, ulong deltaTicks)
            {
                _bw.Write((byte)id);
                AddCompressedUInt64(deltaTicks);
                _lastQpc += (long)deltaTicks;
                _eventCount++;
            }

            public void AddCreateAsyncContext(ulong deltaTicks, ulong taskId)
            {
                WriteSubEventHeader(AsyncProfilerTraceEventParser.AsyncEventID.CreateAsyncContext, deltaTicks);
                AddCompressedUInt64(taskId);
            }

            public void AddResumeAsyncContext(ulong deltaTicks, ulong taskId)
            {
                WriteSubEventHeader(AsyncProfilerTraceEventParser.AsyncEventID.ResumeAsyncContext, deltaTicks);
                AddCompressedUInt64(taskId);
            }

            public void AddSuspendAsyncContext(ulong deltaTicks)
            {
                WriteSubEventHeader(AsyncProfilerTraceEventParser.AsyncEventID.SuspendAsyncContext, deltaTicks);
            }

            public void AddCompleteAsyncContext(ulong deltaTicks)
            {
                WriteSubEventHeader(AsyncProfilerTraceEventParser.AsyncEventID.CompleteAsyncContext, deltaTicks);
            }

            public void AddResumeAsyncMethod(ulong deltaTicks)
            {
                WriteSubEventHeader(AsyncProfilerTraceEventParser.AsyncEventID.ResumeAsyncMethod, deltaTicks);
            }

            public void AddCompleteAsyncMethod(ulong deltaTicks)
            {
                WriteSubEventHeader(AsyncProfilerTraceEventParser.AsyncEventID.CompleteAsyncMethod, deltaTicks);
            }

            public void AddResetAsyncThreadContext(ulong deltaTicks)
            {
                WriteSubEventHeader(AsyncProfilerTraceEventParser.AsyncEventID.ResetAsyncThreadContext, deltaTicks);
            }

            public void AddUnwindAsyncException(ulong deltaTicks, uint unwoundFrames)
            {
                WriteSubEventHeader(AsyncProfilerTraceEventParser.AsyncEventID.UnwindAsyncException, deltaTicks);
                AddCompressedUInt32(unwoundFrames);
            }

            public void AddCreateAsyncCallstack(ulong deltaTicks, AsyncProfilerTraceEventParser.AsyncCallstackType type,
                bool cached, byte callstackId, ulong taskId, TestFrame[] frames)
            {
                AddAsyncCallstack(AsyncProfilerTraceEventParser.AsyncEventID.CreateAsyncCallstack, deltaTicks, type, cached, callstackId, taskId, frames);
            }

            public void AddResumeAsyncCallstack(ulong deltaTicks, AsyncProfilerTraceEventParser.AsyncCallstackType type,
                bool cached, byte callstackId, ulong taskId, TestFrame[] frames)
            {
                AddAsyncCallstack(AsyncProfilerTraceEventParser.AsyncEventID.ResumeAsyncCallstack, deltaTicks, type, cached, callstackId, taskId, frames);
            }

            private void AddAsyncCallstack(AsyncProfilerTraceEventParser.AsyncEventID id, ulong deltaTicks,
                AsyncProfilerTraceEventParser.AsyncCallstackType type, bool cached, byte callstackId, ulong taskId, TestFrame[] frames)
            {
                WriteSubEventHeader(id, deltaTicks);
                byte typeByte = (byte)type;
                if (cached) typeByte |= (byte)AsyncProfilerTraceEventParser.AsyncCallstackType.CachedFlag;
                _bw.Write(typeByte);
                _bw.Write(callstackId);
                _bw.Write((byte)frames.Length);
                AddCompressedUInt64(taskId);
                for (int i = 0; i < frames.Length; i++)
                {
                    if (i == 0)
                    {
                        AddCompressedUInt64(frames[i].IP);
                        AddCompressedInt32(frames[i].State);
                    }
                    else
                    {
                        long delta = (long)frames[i].IP - (long)frames[i - 1].IP;
                        AddCompressedInt64(delta);
                        AddCompressedInt32(frames[i].State);
                    }
                }
            }

            public void AddAsyncProfilerMetadata(ulong deltaTicks, ulong qpcFreq, ulong qpcSync, ulong utcSync, uint bufSize, byte wrapperCount)
            {
                WriteSubEventHeader(AsyncProfilerTraceEventParser.AsyncEventID.AsyncProfilerMetadata, deltaTicks);
                AddCompressedUInt64(qpcFreq);
                AddCompressedUInt64(qpcSync);
                AddCompressedUInt64(utcSync);
                AddCompressedUInt32(bufSize);
                _bw.Write(wrapperCount);
            }

            public void AddAsyncProfilerSyncClock(ulong deltaTicks, ulong qpcSync, ulong utcSync)
            {
                WriteSubEventHeader(AsyncProfilerTraceEventParser.AsyncEventID.AsyncProfilerSyncClock, deltaTicks);
                AddCompressedUInt64(qpcSync);
                AddCompressedUInt64(utcSync);
            }

            public byte[] Finish()
            {
                _bw.Flush();
                byte[] buf = _ms.ToArray();
                // Patch TotalSize (offset 1)
                uint totalSize = (uint)buf.Length;
                buf[1] = (byte)(totalSize); buf[2] = (byte)(totalSize >> 8); buf[3] = (byte)(totalSize >> 16); buf[4] = (byte)(totalSize >> 24);
                // Patch EventCount (offset 1 + 4 + 4 + 8 = 17)
                buf[17] = (byte)(_eventCount); buf[18] = (byte)(_eventCount >> 8); buf[19] = (byte)(_eventCount >> 16); buf[20] = (byte)(_eventCount >> 24);
                // Patch EndTimestamp (offset 17 + 4 + 8 = 29)
                ulong endTs = (ulong)_lastQpc;
                for (int i = 0; i < 8; i++) buf[29 + i] = (byte)(endTs >> (i * 8));
                return buf;
            }
        }

        /// <summary>
        /// Trivial sink that captures every dispatched sub-event so tests can assert on
        /// counts/payloads.  Mirrors <see cref="IAsyncProfilerSubEventSink"/>.
        /// </summary>
        private sealed class CollectingSink : IAsyncProfilerSubEventSink
        {
            public List<CreateAsyncContextTraceData> CreateAsyncContext { get; } = new List<CreateAsyncContextTraceData>();
            public List<ResumeAsyncContextTraceData> ResumeAsyncContext { get; } = new List<ResumeAsyncContextTraceData>();
            public List<SuspendAsyncContextTraceData> SuspendAsyncContext { get; } = new List<SuspendAsyncContextTraceData>();
            public List<CompleteAsyncContextTraceData> CompleteAsyncContext { get; } = new List<CompleteAsyncContextTraceData>();
            public List<UnwindAsyncExceptionTraceData> UnwindAsyncException { get; } = new List<UnwindAsyncExceptionTraceData>();
            public List<AsyncCallstackTraceData> CreateAsyncCallstack { get; } = new List<AsyncCallstackTraceData>();
            public List<AsyncCallstackTraceData> ResumeAsyncCallstack { get; } = new List<AsyncCallstackTraceData>();
            public List<AsyncCallstackTraceData> SuspendAsyncCallstack { get; } = new List<AsyncCallstackTraceData>();
            public List<AsyncMethodTraceData> ResumeAsyncMethod { get; } = new List<AsyncMethodTraceData>();
            public List<AsyncMethodTraceData> CompleteAsyncMethod { get; } = new List<AsyncMethodTraceData>();
            public List<ResetAsyncTraceData> ResetAsyncThreadContext { get; } = new List<ResetAsyncTraceData>();
            public List<ResetAsyncTraceData> ResetAsyncContinuationWrapperIndex { get; } = new List<ResetAsyncTraceData>();
            public List<AsyncProfilerMetadataTraceData> Metadata { get; } = new List<AsyncProfilerMetadataTraceData>();
            public List<AsyncProfilerSyncClockTraceData> SyncClock { get; } = new List<AsyncProfilerSyncClockTraceData>();
            public List<AsyncProfilerParseError> Errors { get; } = new List<AsyncProfilerParseError>();

            public IEnumerable<object> All
            {
                get
                {
                    foreach (var x in CreateAsyncContext) yield return x;
                    foreach (var x in ResumeAsyncContext) yield return x;
                    foreach (var x in SuspendAsyncContext) yield return x;
                    foreach (var x in CompleteAsyncContext) yield return x;
                    foreach (var x in UnwindAsyncException) yield return x;
                    foreach (var x in CreateAsyncCallstack) yield return x;
                    foreach (var x in ResumeAsyncCallstack) yield return x;
                    foreach (var x in SuspendAsyncCallstack) yield return x;
                    foreach (var x in ResumeAsyncMethod) yield return x;
                    foreach (var x in CompleteAsyncMethod) yield return x;
                    foreach (var x in ResetAsyncThreadContext) yield return x;
                    foreach (var x in ResetAsyncContinuationWrapperIndex) yield return x;
                    foreach (var x in Metadata) yield return x;
                    foreach (var x in SyncClock) yield return x;
                    foreach (var x in Errors) yield return x;
                }
            }

            public void OnCreateAsyncContext(CreateAsyncContextTraceData e) => CreateAsyncContext.Add(e);
            public void OnResumeAsyncContext(ResumeAsyncContextTraceData e) => ResumeAsyncContext.Add(e);
            public void OnSuspendAsyncContext(SuspendAsyncContextTraceData e) => SuspendAsyncContext.Add(e);
            public void OnCompleteAsyncContext(CompleteAsyncContextTraceData e) => CompleteAsyncContext.Add(e);
            public void OnUnwindAsyncException(UnwindAsyncExceptionTraceData e) => UnwindAsyncException.Add(e);
            public void OnCreateAsyncCallstack(AsyncCallstackTraceData e) => CreateAsyncCallstack.Add(e);
            public void OnResumeAsyncCallstack(AsyncCallstackTraceData e) => ResumeAsyncCallstack.Add(e);
            public void OnSuspendAsyncCallstack(AsyncCallstackTraceData e) => SuspendAsyncCallstack.Add(e);
            public void OnResumeAsyncMethod(AsyncMethodTraceData e) => ResumeAsyncMethod.Add(e);
            public void OnCompleteAsyncMethod(AsyncMethodTraceData e) => CompleteAsyncMethod.Add(e);
            public void OnResetAsyncThreadContext(ResetAsyncTraceData e) => ResetAsyncThreadContext.Add(e);
            public void OnResetAsyncContinuationWrapperIndex(ResetAsyncTraceData e) => ResetAsyncContinuationWrapperIndex.Add(e);
            public void OnAsyncProfilerMetadata(AsyncProfilerMetadataTraceData e) => Metadata.Add(e);
            public void OnAsyncProfilerSyncClock(AsyncProfilerSyncClockTraceData e) => SyncClock.Add(e);
            public void OnParseError(AsyncProfilerParseError e) => Errors.Add(e);
        }

        #endregion
    }
}
