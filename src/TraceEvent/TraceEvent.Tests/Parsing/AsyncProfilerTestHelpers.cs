// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace TraceEventTests
{
    /// <summary>
    /// A single frame in a synthesised async callstack payload.
    /// </summary>
    internal readonly struct AsyncProfilerTestFrame
    {
        public AsyncProfilerTestFrame(ulong ip, int state) { IP = ip; State = state; }
        public ulong IP { get; }
        public int State { get; }
    }

    /// <summary>
    /// Builds an AsyncEvents buffer for testing.  Mirrors the runtime's writer in
    /// <c>AsyncProfilerTests.cs</c>.  Back-patches the TotalSize, EventCount, and
    /// EndTimestamp header fields on <see cref="Finish"/>.
    /// </summary>
    internal sealed class AsyncProfilerBufferBuilder
    {
        private readonly MemoryStream _ms = new MemoryStream();
        private readonly BinaryWriter _bw;
        private long _lastQpc;
        private uint _eventCount;

        public AsyncProfilerBufferBuilder(byte version, uint asyncCtxId, ulong osThreadId, ulong startQpc)
        {
            _bw = new BinaryWriter(_ms);
            _lastQpc = (long)startQpc;
            _bw.Write(version);
            _bw.Write(0u);
            _bw.Write(asyncCtxId);
            _bw.Write(osThreadId);
            _bw.Write(0u);
            _bw.Write(startQpc);
            _bw.Write(0ul);
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
            bool cached, byte callstackId, ulong taskId, AsyncProfilerTestFrame[] frames)
        {
            AddAsyncCallstack(AsyncProfilerTraceEventParser.AsyncEventID.CreateAsyncCallstack, deltaTicks, type, cached, callstackId, taskId, frames);
        }

        public void AddResumeAsyncCallstack(ulong deltaTicks, AsyncProfilerTraceEventParser.AsyncCallstackType type,
            bool cached, byte callstackId, ulong taskId, AsyncProfilerTestFrame[] frames)
        {
            AddAsyncCallstack(AsyncProfilerTraceEventParser.AsyncEventID.ResumeAsyncCallstack, deltaTicks, type, cached, callstackId, taskId, frames);
        }

        public void AddSuspendAsyncCallstack(ulong deltaTicks, AsyncProfilerTraceEventParser.AsyncCallstackType type,
            bool cached, byte callstackId, ulong taskId, AsyncProfilerTestFrame[] frames)
        {
            AddAsyncCallstack(AsyncProfilerTraceEventParser.AsyncEventID.SuspendAsyncCallstack, deltaTicks, type, cached, callstackId, taskId, frames);
        }

        private void AddAsyncCallstack(AsyncProfilerTraceEventParser.AsyncEventID id, ulong deltaTicks,
            AsyncProfilerTraceEventParser.AsyncCallstackType type, bool cached, byte callstackId, ulong taskId, AsyncProfilerTestFrame[] frames)
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
            uint totalSize = (uint)buf.Length;
            buf[1] = (byte)(totalSize); buf[2] = (byte)(totalSize >> 8); buf[3] = (byte)(totalSize >> 16); buf[4] = (byte)(totalSize >> 24);
            buf[17] = (byte)(_eventCount); buf[18] = (byte)(_eventCount >> 8); buf[19] = (byte)(_eventCount >> 16); buf[20] = (byte)(_eventCount >> 24);
            ulong endTs = (ulong)_lastQpc;
            for (int i = 0; i < 8; i++) buf[29 + i] = (byte)(endTs >> (i * 8));
            return buf;
        }
    }

    /// <summary>
    /// Trivial sink that captures every dispatched sub-event so tests can assert on
    /// counts/payloads.
    /// </summary>
    internal sealed class AsyncProfilerCollectingSink : IAsyncProfilerSubEventSink
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
}
