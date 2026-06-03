// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Parsers
{
    /// <summary>
    /// Parser for the runtime's <c>System.Runtime.CompilerServices.AsyncProfilerEventSource</c>
    /// EventSource (introduced in .NET 11).  The provider emits a single ETW/EventPipe event
    /// <c>AsyncEvents</c> (id=1) whose payload is a packed binary buffer containing many
    /// sub-events with delta-encoded timestamps and (for callstacks) delta-encoded IPs.
    ///
    /// <para>The parser registers the raw <c>AsyncEvents</c> template so that the event shows
    /// up unaltered in the Events View / AllEvents stream (the payload is exposed as
    /// <c>Length</c> and <c>Buffer</c>).  In addition, it parses each emitted buffer and fires
    /// strongly-typed callbacks for every contained sub-event.  Sub-event timestamps are
    /// resolved to absolute QPC values; helper methods convert QPC to <see cref="DateTime"/>
    /// using the <c>AsyncProfilerMetadata</c> / <c>AsyncProfilerSyncClock</c> records emitted
    /// by the provider.</para>
    ///
    /// <para>The wire format is documented in the runtime source under
    /// <c>System.Runtime.CompilerServices.AsyncProfiler</c>.  See also
    /// <c>AsyncProfilerTests.cs</c> in dotnet/runtime for the canonical reference parser.</para>
    /// </summary>
    public sealed class AsyncProfilerTraceEventParser : TraceEventParser
    {
        /// <summary>
        /// EventSource name for the runtime async profiler.
        /// </summary>
        public const string ProviderName = "System.Runtime.CompilerServices.AsyncProfilerEventSource";

        /// <summary>
        /// Provider GUID derived from <see cref="ProviderName"/> via the standard EventSource
        /// name-to-GUID SHA-1 algorithm (see <c>TraceEventProviders.GetEventSourceGuidFromName</c>).
        /// </summary>
        public static readonly Guid ProviderGuid = new Guid(
            unchecked((int)0xf8f29278),
            unchecked((short)0x1df8),
            unchecked((short)0xd650),
            0xae, 0xa7, 0x59, 0x4d, 0x84, 0x6c, 0x22, 0x74);

        /// <summary>
        /// Wire-format version this parser understands.
        /// </summary>
        public const byte SupportedBufferVersion = 1;

        /// <summary>
        /// Size of the per-buffer header preceding the sub-events.
        /// </summary>
        public const int BufferHeaderSize = 37;

        /// <summary>
        /// FlushCommand value to send via <c>EventSource.SendCommand</c> / TraceEventSession
        /// to force the provider to ship all in-flight thread buffers.
        /// </summary>
        public const int FlushCommand = 1;

        /// <summary>
        /// Keyword bitmask covering every async profiler sub-event.
        /// </summary>
        [Flags]
        public enum Keywords : long
        {
            None = 0,
            CreateAsyncContext = 0x1,
            ResumeAsyncContext = 0x2,
            SuspendAsyncContext = 0x4,
            CompleteAsyncContext = 0x8,
            UnwindAsyncException = 0x10,
            CreateAsyncCallstack = 0x20,
            ResumeAsyncCallstack = 0x40,
            SuspendAsyncCallstack = 0x80,
            ResumeAsyncMethod = 0x100,
            CompleteAsyncMethod = 0x200,
            All = CreateAsyncContext | ResumeAsyncContext | SuspendAsyncContext | CompleteAsyncContext |
                  UnwindAsyncException | CreateAsyncCallstack | ResumeAsyncCallstack | SuspendAsyncCallstack |
                  ResumeAsyncMethod | CompleteAsyncMethod,
        }

        /// <summary>
        /// Identifier for each sub-event packed inside an <c>AsyncEvents</c> buffer.
        /// Values are stable; they match the runtime's <c>AsyncEventID</c> enum.
        /// </summary>
        public enum AsyncEventID : byte
        {
            CreateAsyncContext = 1,
            ResumeAsyncContext = 2,
            SuspendAsyncContext = 3,
            CompleteAsyncContext = 4,
            UnwindAsyncException = 5,
            CreateAsyncCallstack = 6,
            ResumeAsyncCallstack = 7,
            SuspendAsyncCallstack = 8,
            ResumeAsyncMethod = 9,
            CompleteAsyncMethod = 10,
            ResetAsyncThreadContext = 11,
            ResetAsyncContinuationWrapperIndex = 12,
            AsyncProfilerMetadata = 13,
            AsyncProfilerSyncClock = 14,
        }

        /// <summary>
        /// Source of an async callstack.  Matches the runtime's <c>AsyncCallstackType</c> enum.
        /// </summary>
        public enum AsyncCallstackType : byte
        {
            Compiler = 1,
            Runtime = 2,
            CachedFlag = 0x80,
        }

        private static volatile TraceEvent[] s_templates;

        public AsyncProfilerTraceEventParser(TraceEventSource source) : base(source)
        {
            ((ITraceParserServices)source).RegisterEventTemplate(AsyncEventsTemplate(Dispatch));
        }

        protected override string GetProviderName() => ProviderName;

        protected internal override void EnumerateTemplates(Func<string, string, EventFilterResponse> eventsToObserve, Action<TraceEvent> callback)
        {
            if (s_templates == null)
            {
                var templates = new TraceEvent[1];
                templates[0] = AsyncEventsTemplate(null);
                s_templates = templates;
            }

            foreach (var template in s_templates)
            {
                if (eventsToObserve == null || eventsToObserve(template.ProviderName, template.EventName) == EventFilterResponse.AcceptEvent)
                    callback(template);
            }
        }

        /// <summary>
        /// Subscribe to the raw <c>AsyncEvents</c> event (carries the entire encoded buffer).
        /// </summary>
        public event Action<AsyncEventsTraceData> AsyncEvents
        {
            add { _rawAsyncEvents += value; }
            remove { _rawAsyncEvents -= value; }
        }

        /// <summary>Fired for each <c>CreateAsyncContext</c> sub-event.</summary>
        public event Action<CreateAsyncContextTraceData> CreateAsyncContext { add { _createAsyncContext += value; } remove { _createAsyncContext -= value; } }

        /// <summary>Fired for each <c>ResumeAsyncContext</c> sub-event.</summary>
        public event Action<ResumeAsyncContextTraceData> ResumeAsyncContext { add { _resumeAsyncContext += value; } remove { _resumeAsyncContext -= value; } }

        /// <summary>Fired for each <c>SuspendAsyncContext</c> sub-event.</summary>
        public event Action<SuspendAsyncContextTraceData> SuspendAsyncContext { add { _suspendAsyncContext += value; } remove { _suspendAsyncContext -= value; } }

        /// <summary>Fired for each <c>CompleteAsyncContext</c> sub-event.</summary>
        public event Action<CompleteAsyncContextTraceData> CompleteAsyncContext { add { _completeAsyncContext += value; } remove { _completeAsyncContext -= value; } }

        /// <summary>Fired for each <c>UnwindAsyncException</c> sub-event.</summary>
        public event Action<UnwindAsyncExceptionTraceData> UnwindAsyncException { add { _unwindAsyncException += value; } remove { _unwindAsyncException -= value; } }

        /// <summary>Fired for each <c>CreateAsyncCallstack</c> sub-event.</summary>
        public event Action<AsyncCallstackTraceData> CreateAsyncCallstack { add { _createAsyncCallstack += value; } remove { _createAsyncCallstack -= value; } }

        /// <summary>Fired for each <c>ResumeAsyncCallstack</c> sub-event.</summary>
        public event Action<AsyncCallstackTraceData> ResumeAsyncCallstack { add { _resumeAsyncCallstack += value; } remove { _resumeAsyncCallstack -= value; } }

        /// <summary>Fired for each <c>SuspendAsyncCallstack</c> sub-event.</summary>
        public event Action<AsyncCallstackTraceData> SuspendAsyncCallstack { add { _suspendAsyncCallstack += value; } remove { _suspendAsyncCallstack -= value; } }

        /// <summary>Fired for each <c>ResumeAsyncMethod</c> sub-event.</summary>
        public event Action<AsyncMethodTraceData> ResumeAsyncMethod { add { _resumeAsyncMethod += value; } remove { _resumeAsyncMethod -= value; } }

        /// <summary>Fired for each <c>CompleteAsyncMethod</c> sub-event.</summary>
        public event Action<AsyncMethodTraceData> CompleteAsyncMethod { add { _completeAsyncMethod += value; } remove { _completeAsyncMethod -= value; } }

        /// <summary>Fired for each <c>ResetAsyncThreadContext</c> sub-event.</summary>
        public event Action<ResetAsyncTraceData> ResetAsyncThreadContext { add { _resetAsyncThreadContext += value; } remove { _resetAsyncThreadContext -= value; } }

        /// <summary>Fired for each <c>ResetAsyncContinuationWrapperIndex</c> sub-event.</summary>
        public event Action<ResetAsyncTraceData> ResetAsyncContinuationWrapperIndex { add { _resetAsyncContinuationWrapperIndex += value; } remove { _resetAsyncContinuationWrapperIndex -= value; } }

        /// <summary>Fired for each <c>AsyncProfilerMetadata</c> sub-event.</summary>
        public event Action<AsyncProfilerMetadataTraceData> AsyncProfilerMetadata { add { _asyncProfilerMetadata += value; } remove { _asyncProfilerMetadata -= value; } }

        /// <summary>Fired for each <c>AsyncProfilerSyncClock</c> sub-event.</summary>
        public event Action<AsyncProfilerSyncClockTraceData> AsyncProfilerSyncClock { add { _asyncProfilerSyncClock += value; } remove { _asyncProfilerSyncClock -= value; } }

        /// <summary>
        /// Fired when a malformed buffer is detected.  Subscribers can use this to surface
        /// diagnostic information; the parser drops the offending buffer and continues.
        /// </summary>
        public event Action<AsyncProfilerParseError> ParseError { add { _parseError += value; } remove { _parseError -= value; } }

        private event Action<AsyncEventsTraceData> _rawAsyncEvents;
        private event Action<CreateAsyncContextTraceData> _createAsyncContext;
        private event Action<ResumeAsyncContextTraceData> _resumeAsyncContext;
        private event Action<SuspendAsyncContextTraceData> _suspendAsyncContext;
        private event Action<CompleteAsyncContextTraceData> _completeAsyncContext;
        private event Action<UnwindAsyncExceptionTraceData> _unwindAsyncException;
        private event Action<AsyncCallstackTraceData> _createAsyncCallstack;
        private event Action<AsyncCallstackTraceData> _resumeAsyncCallstack;
        private event Action<AsyncCallstackTraceData> _suspendAsyncCallstack;
        private event Action<AsyncMethodTraceData> _resumeAsyncMethod;
        private event Action<AsyncMethodTraceData> _completeAsyncMethod;
        private event Action<ResetAsyncTraceData> _resetAsyncThreadContext;
        private event Action<ResetAsyncTraceData> _resetAsyncContinuationWrapperIndex;
        private event Action<AsyncProfilerMetadataTraceData> _asyncProfilerMetadata;
        private event Action<AsyncProfilerSyncClockTraceData> _asyncProfilerSyncClock;
        private event Action<AsyncProfilerParseError> _parseError;

        private static AsyncEventsTraceData AsyncEventsTemplate(Action<AsyncEventsTraceData> action)
        {
            // eventID=1, task=1, opcode=Info (0), all part of the EventSource "AsyncEvents" event.
            return new AsyncEventsTraceData(action, 1, 1, "AsyncEvents", Guid.Empty, 0, "Info", ProviderGuid, ProviderName);
        }

        private void Dispatch(AsyncEventsTraceData data)
        {
            // 1. Fire the raw event so subscribers (and Events View) see the unaltered buffer.
            _rawAsyncEvents?.Invoke(data);

            // 2. Re-parse the buffer and dispatch typed sub-events.  Skip if nobody is listening.
            if (!HasAnySubEventSubscribers())
                return;

            byte[] buffer = data.Buffer;
            if (buffer == null || buffer.Length == 0)
                return;

            DispatchSubEvents(data, buffer);
        }

        /// <summary>
        /// Decodes an <c>AsyncEvents</c> buffer and forwards each contained sub-event to the
        /// supplied sink.  The buffer is the value of <see cref="AsyncEventsTraceData.Buffer"/>
        /// (i.e. NOT including the length-prefix); <paramref name="rawEvent"/> may be
        /// <c>null</c> for unit-testing scenarios.
        /// </summary>
        public static void ParseBuffer(AsyncEventsTraceData rawEvent, byte[] buffer, IAsyncProfilerSubEventSink sink)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            if (buffer == null || buffer.Length == 0) return;

            BufferHeader header;
            try
            {
                if (!TryReadBufferHeader(buffer, out header))
                {
                    sink.OnParseError(new AsyncProfilerParseError(rawEvent, "Malformed or unsupported buffer header"));
                    return;
                }

                int index = BufferHeaderSize;
                ulong currentTaskId = 0;
                long timestamp = (long)header.StartTimestamp;
                int subEventIndex = 0;
                // Bound the iteration so a corrupt EventCount cannot loop forever; the buffer
                // length itself is the ultimate guard.
                int maxIterations = (int)Math.Min(header.EventCount + 16u, (uint)buffer.Length);

                while (index < buffer.Length && subEventIndex < maxIterations)
                {
                    if (index + 1 > buffer.Length)
                        break;

                    AsyncEventID eventId = (AsyncEventID)buffer[index++];
                    if (!TryReadCompressedUInt64(buffer, ref index, out ulong deltaTicks))
                    {
                        sink.OnParseError(new AsyncProfilerParseError(rawEvent, $"Truncated timestamp delta at sub-event {subEventIndex}"));
                        return;
                    }
                    timestamp += (long)deltaTicks;
                    subEventIndex++;

                    if (!DispatchSingleSubEvent(rawEvent, buffer, ref index, eventId, timestamp, ref currentTaskId, header, sink))
                        return;
                }
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException)
            {
                sink.OnParseError(new AsyncProfilerParseError(rawEvent, "Buffer truncated: " + ex.Message));
            }
        }

        private bool HasAnySubEventSubscribers()
        {
            return _createAsyncContext != null || _resumeAsyncContext != null
                || _suspendAsyncContext != null || _completeAsyncContext != null
                || _unwindAsyncException != null
                || _createAsyncCallstack != null || _resumeAsyncCallstack != null || _suspendAsyncCallstack != null
                || _resumeAsyncMethod != null || _completeAsyncMethod != null
                || _resetAsyncThreadContext != null || _resetAsyncContinuationWrapperIndex != null
                || _asyncProfilerMetadata != null || _asyncProfilerSyncClock != null;
        }

        private void DispatchSubEvents(AsyncEventsTraceData rawEvent, byte[] buffer)
        {
            if (_dispatchSink == null)
                _dispatchSink = new EventDispatchSink(this);
            ParseBuffer(rawEvent, buffer, _dispatchSink);
        }

        private EventDispatchSink _dispatchSink;

        private static bool DispatchSingleSubEvent(AsyncEventsTraceData rawEvent, byte[] buffer, ref int index,
            AsyncEventID eventId, long timestamp, ref ulong currentTaskId, BufferHeader header, IAsyncProfilerSubEventSink sink)
        {
            switch (eventId)
            {
                case AsyncEventID.CreateAsyncContext:
                case AsyncEventID.ResumeAsyncContext:
                    {
                        if (!TryReadCompressedUInt64(buffer, ref index, out ulong id))
                        {
                            sink.OnParseError(new AsyncProfilerParseError(rawEvent, $"Truncated context id for {eventId}"));
                            return false;
                        }
                        currentTaskId = id;
                        if (eventId == AsyncEventID.CreateAsyncContext)
                            sink.OnCreateAsyncContext(new CreateAsyncContextTraceData(rawEvent, timestamp, header, id));
                        else
                            sink.OnResumeAsyncContext(new ResumeAsyncContextTraceData(rawEvent, timestamp, header, id));
                        return true;
                    }

                case AsyncEventID.SuspendAsyncContext:
                    sink.OnSuspendAsyncContext(new SuspendAsyncContextTraceData(rawEvent, timestamp, header, currentTaskId));
                    return true;

                case AsyncEventID.CompleteAsyncContext:
                    sink.OnCompleteAsyncContext(new CompleteAsyncContextTraceData(rawEvent, timestamp, header, currentTaskId));
                    return true;

                case AsyncEventID.ResumeAsyncMethod:
                    sink.OnResumeAsyncMethod(new AsyncMethodTraceData(rawEvent, timestamp, header, currentTaskId, isComplete: false));
                    return true;

                case AsyncEventID.CompleteAsyncMethod:
                    sink.OnCompleteAsyncMethod(new AsyncMethodTraceData(rawEvent, timestamp, header, currentTaskId, isComplete: true));
                    return true;

                case AsyncEventID.UnwindAsyncException:
                    {
                        if (!TryReadCompressedUInt32(buffer, ref index, out uint unwindFrames))
                        {
                            sink.OnParseError(new AsyncProfilerParseError(rawEvent, "Truncated UnwindAsyncException payload"));
                            return false;
                        }
                        sink.OnUnwindAsyncException(new UnwindAsyncExceptionTraceData(rawEvent, timestamp, header, currentTaskId, unwindFrames));
                        return true;
                    }

                case AsyncEventID.CreateAsyncCallstack:
                case AsyncEventID.ResumeAsyncCallstack:
                case AsyncEventID.SuspendAsyncCallstack:
                    {
                        if (!TryReadCallstackPayload(buffer, ref index, out var cs))
                        {
                            sink.OnParseError(new AsyncProfilerParseError(rawEvent, $"Truncated callstack payload for {eventId}"));
                            return false;
                        }
                        var args = new AsyncCallstackTraceData(rawEvent, eventId, timestamp, header, cs);
                        if (eventId == AsyncEventID.CreateAsyncCallstack) sink.OnCreateAsyncCallstack(args);
                        else if (eventId == AsyncEventID.ResumeAsyncCallstack) sink.OnResumeAsyncCallstack(args);
                        else sink.OnSuspendAsyncCallstack(args);
                        return true;
                    }

                case AsyncEventID.ResetAsyncThreadContext:
                    {
                        var prev = currentTaskId;
                        currentTaskId = 0;
                        sink.OnResetAsyncThreadContext(new ResetAsyncTraceData(rawEvent, eventId, timestamp, header, prev));
                        return true;
                    }

                case AsyncEventID.ResetAsyncContinuationWrapperIndex:
                    sink.OnResetAsyncContinuationWrapperIndex(new ResetAsyncTraceData(rawEvent, eventId, timestamp, header, currentTaskId));
                    return true;

                case AsyncEventID.AsyncProfilerMetadata:
                    {
                        if (!TryReadCompressedUInt64(buffer, ref index, out ulong qpcFreq) ||
                            !TryReadCompressedUInt64(buffer, ref index, out ulong qpcSync) ||
                            !TryReadCompressedUInt64(buffer, ref index, out ulong utcSync) ||
                            !TryReadCompressedUInt32(buffer, ref index, out uint bufSize) ||
                            index >= buffer.Length)
                        {
                            sink.OnParseError(new AsyncProfilerParseError(rawEvent, "Truncated AsyncProfilerMetadata payload"));
                            return false;
                        }
                        byte wrapperCount = buffer[index++];
                        sink.OnAsyncProfilerMetadata(new AsyncProfilerMetadataTraceData(rawEvent, timestamp, header, currentTaskId,
                            qpcFreq, qpcSync, utcSync, bufSize, wrapperCount));
                        return true;
                    }

                case AsyncEventID.AsyncProfilerSyncClock:
                    {
                        if (!TryReadCompressedUInt64(buffer, ref index, out ulong qpcSync) ||
                            !TryReadCompressedUInt64(buffer, ref index, out ulong utcSync))
                        {
                            sink.OnParseError(new AsyncProfilerParseError(rawEvent, "Truncated AsyncProfilerSyncClock payload"));
                            return false;
                        }
                        sink.OnAsyncProfilerSyncClock(new AsyncProfilerSyncClockTraceData(rawEvent, timestamp, header, currentTaskId, qpcSync, utcSync));
                        return true;
                    }

                default:
                    // Unknown sub-event with no known payload shape: we cannot safely advance
                    // past it.  Surface a parse error and abandon the rest of the buffer.
                    sink.OnParseError(new AsyncProfilerParseError(rawEvent, $"Unknown sub-event id 0x{(byte)eventId:X2} at offset {index}"));
                    return false;
            }
        }

        /// <summary>
        /// Sink that forwards parsed sub-events to the parser's typed C# events.  Used by
        /// <see cref="Dispatch(AsyncEventsTraceData)"/> when the buffer arrives via the
        /// normal ETW/EventPipe pipeline.
        /// </summary>
        private sealed class EventDispatchSink : IAsyncProfilerSubEventSink
        {
            private readonly AsyncProfilerTraceEventParser _p;
            public EventDispatchSink(AsyncProfilerTraceEventParser p) { _p = p; }
            public void OnCreateAsyncContext(CreateAsyncContextTraceData e) => _p._createAsyncContext?.Invoke(e);
            public void OnResumeAsyncContext(ResumeAsyncContextTraceData e) => _p._resumeAsyncContext?.Invoke(e);
            public void OnSuspendAsyncContext(SuspendAsyncContextTraceData e) => _p._suspendAsyncContext?.Invoke(e);
            public void OnCompleteAsyncContext(CompleteAsyncContextTraceData e) => _p._completeAsyncContext?.Invoke(e);
            public void OnUnwindAsyncException(UnwindAsyncExceptionTraceData e) => _p._unwindAsyncException?.Invoke(e);
            public void OnCreateAsyncCallstack(AsyncCallstackTraceData e) => _p._createAsyncCallstack?.Invoke(e);
            public void OnResumeAsyncCallstack(AsyncCallstackTraceData e) => _p._resumeAsyncCallstack?.Invoke(e);
            public void OnSuspendAsyncCallstack(AsyncCallstackTraceData e) => _p._suspendAsyncCallstack?.Invoke(e);
            public void OnResumeAsyncMethod(AsyncMethodTraceData e) => _p._resumeAsyncMethod?.Invoke(e);
            public void OnCompleteAsyncMethod(AsyncMethodTraceData e) => _p._completeAsyncMethod?.Invoke(e);
            public void OnResetAsyncThreadContext(ResetAsyncTraceData e) => _p._resetAsyncThreadContext?.Invoke(e);
            public void OnResetAsyncContinuationWrapperIndex(ResetAsyncTraceData e) => _p._resetAsyncContinuationWrapperIndex?.Invoke(e);
            public void OnAsyncProfilerMetadata(AsyncProfilerMetadataTraceData e) => _p._asyncProfilerMetadata?.Invoke(e);
            public void OnAsyncProfilerSyncClock(AsyncProfilerSyncClockTraceData e) => _p._asyncProfilerSyncClock?.Invoke(e);
            public void OnParseError(AsyncProfilerParseError e) => _p._parseError?.Invoke(e);
        }

        internal static bool TryReadBufferHeader(byte[] buffer, out BufferHeader header)
        {
            header = default;
            if (buffer == null || buffer.Length < BufferHeaderSize) return false;
            if (buffer[0] != SupportedBufferVersion) return false;

            int i = 1;
            uint totalSize = ReadUInt32LE(buffer, ref i);
            uint asyncCtx = ReadUInt32LE(buffer, ref i);
            ulong osThread = ReadUInt64LE(buffer, ref i);
            uint count = ReadUInt32LE(buffer, ref i);
            ulong startTs = ReadUInt64LE(buffer, ref i);
            ulong endTs = ReadUInt64LE(buffer, ref i);

            // Sanity: TotalSize must not exceed the actual byte[] we got.
            if (totalSize > (uint)buffer.Length) return false;

            header = new BufferHeader(SupportedBufferVersion, totalSize, asyncCtx, osThread, count, startTs, endTs);
            return true;
        }

        private static uint ReadUInt32LE(byte[] b, ref int i)
        {
            uint v = (uint)b[i] | ((uint)b[i + 1] << 8) | ((uint)b[i + 2] << 16) | ((uint)b[i + 3] << 24);
            i += 4;
            return v;
        }

        private static ulong ReadUInt64LE(byte[] b, ref int i)
        {
            ulong lo = ReadUInt32LE(b, ref i);
            ulong hi = ReadUInt32LE(b, ref i);
            return lo | (hi << 32);
        }

        internal static bool TryReadCompressedUInt64(byte[] buffer, ref int index, out ulong value)
        {
            value = 0;
            int shift = 0;
            // LEB128 caps at 10 bytes for a 64-bit value.
            for (int k = 0; k < 10; k++)
            {
                if (index >= buffer.Length) { value = 0; return false; }
                byte b = buffer[index++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }

        internal static bool TryReadCompressedUInt32(byte[] buffer, ref int index, out uint value)
        {
            value = 0;
            int shift = 0;
            for (int k = 0; k < 5; k++)
            {
                if (index >= buffer.Length) { value = 0; return false; }
                byte b = buffer[index++];
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }

        internal static bool TryReadCompressedInt64(byte[] buffer, ref int index, out long value)
        {
            value = 0;
            if (!TryReadCompressedUInt64(buffer, ref index, out ulong u)) return false;
            value = (long)((u >> 1) ^ (~(u & 1) + 1));
            return true;
        }

        internal static bool TryReadCompressedInt32(byte[] buffer, ref int index, out int value)
        {
            value = 0;
            if (!TryReadCompressedUInt32(buffer, ref index, out uint u)) return false;
            value = (int)((u >> 1) ^ (~(u & 1) + 1));
            return true;
        }

        private static bool TryReadCallstackPayload(byte[] buffer, ref int index, out CallstackPayload payload)
        {
            payload = default;
            if (index + 3 > buffer.Length) return false;

            byte rawType = buffer[index++];
            byte callstackId = buffer[index++];
            byte frameCount = buffer[index++];
            if (!TryReadCompressedUInt64(buffer, ref index, out ulong taskId)) return false;

            var frames = new AsyncFrame[frameCount];
            if (frameCount > 0)
            {
                if (!TryReadCompressedUInt64(buffer, ref index, out ulong firstIp)) return false;
                if (!TryReadCompressedInt32(buffer, ref index, out int firstState)) return false;
                frames[0] = new AsyncFrame(firstIp, firstState);
                ulong currentIp = firstIp;
                for (int f = 1; f < frameCount; f++)
                {
                    if (!TryReadCompressedInt64(buffer, ref index, out long delta)) return false;
                    if (!TryReadCompressedInt32(buffer, ref index, out int state)) return false;
                    currentIp = (ulong)((long)currentIp + delta);
                    frames[f] = new AsyncFrame(currentIp, state);
                }
            }

            bool cached = (rawType & (byte)AsyncCallstackType.CachedFlag) != 0;
            AsyncCallstackType baseType = (AsyncCallstackType)(rawType & ~(byte)AsyncCallstackType.CachedFlag);
            payload = new CallstackPayload(baseType, cached, callstackId, frameCount, taskId, frames);
            return true;
        }

        /// <summary>
        /// Convert a QPC timestamp resolved from the async profiler buffer to UTC, using
        /// the supplied sync record.  <paramref name="utcSync"/> is a Windows FILETIME
        /// (100ns ticks since 1601-01-01 UTC) as emitted by <c>DateTime.UtcNow.ToFileTimeUtc()</c>
        /// in the runtime metadata sub-event.  Returns <c>null</c> if
        /// <paramref name="qpcFrequency"/> is zero (i.e. metadata has not been observed yet).
        /// </summary>
        public static DateTime? QpcToDateTime(long qpc, ulong qpcFrequency, ulong qpcSync, ulong utcSync)
        {
            if (qpcFrequency == 0) return null;
            long deltaQpc = qpc - (long)qpcSync;
            double seconds = (double)deltaQpc / qpcFrequency;
            long utcFileTime = (long)utcSync + (long)(seconds * TimeSpan.TicksPerSecond);
            return DateTime.FromFileTimeUtc(utcFileTime);
        }
    }
}

namespace Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler
{
    /// <summary>
    /// Parsed contents of the per-buffer header.  Common to every sub-event dispatched
    /// from a single <c>AsyncEvents</c> ETW/EventPipe event.
    /// </summary>
    public readonly struct BufferHeader
    {
        public BufferHeader(byte version, uint totalSize, uint asyncThreadContextId, ulong osThreadId, uint eventCount, ulong startTimestamp, ulong endTimestamp)
        {
            Version = version;
            TotalSize = totalSize;
            AsyncThreadContextId = asyncThreadContextId;
            OsThreadId = osThreadId;
            EventCount = eventCount;
            StartTimestamp = startTimestamp;
            EndTimestamp = endTimestamp;
        }

        public byte Version { get; }
        public uint TotalSize { get; }
        public uint AsyncThreadContextId { get; }
        public ulong OsThreadId { get; }
        public uint EventCount { get; }
        public ulong StartTimestamp { get; }
        public ulong EndTimestamp { get; }
    }

    /// <summary>
    /// A single frame inside an async callstack payload.
    /// </summary>
    public readonly struct AsyncFrame
    {
        public AsyncFrame(ulong nativeIP, int state)
        {
            NativeIP = nativeIP;
            State = state;
        }

        /// <summary>Native instruction pointer, or 0 for handrolled continuations.</summary>
        public ulong NativeIP { get; }
        /// <summary>Continuation state machine value (0 when NativeIP==0).</summary>
        public int State { get; }
    }

    /// <summary>
    /// Decoded callstack payload (used for Create/Resume/Suspend AsyncCallstack events).
    /// </summary>
    public readonly struct CallstackPayload
    {
        public CallstackPayload(AsyncProfilerTraceEventParser.AsyncCallstackType type, bool cached, byte callstackId, byte frameCount, ulong taskId, AsyncFrame[] frames)
        {
            Type = type;
            Cached = cached;
            CallstackId = callstackId;
            FrameCount = frameCount;
            TaskId = taskId;
            Frames = frames;
        }

        public AsyncProfilerTraceEventParser.AsyncCallstackType Type { get; }
        public bool Cached { get; }
        public byte CallstackId { get; }
        public byte FrameCount { get; }
        public ulong TaskId { get; }
        public AsyncFrame[] Frames { get; }
    }

    /// <summary>
    /// Raw <c>AsyncEvents</c> ETW/EventPipe event template.  Exposes the encoded buffer
    /// untouched so that filters / Events View / TraceLog round-trip work unchanged.
    /// </summary>
    public sealed class AsyncEventsTraceData : TraceEvent
    {
        private static readonly string[] s_payloadNames = new[] { "Length", "Buffer" };

        private event Action<AsyncEventsTraceData> Action;

        internal AsyncEventsTraceData(Action<AsyncEventsTraceData> action, int eventID, int task, string taskName, Guid taskGuid, int opcode, string opcodeName, Guid providerGuid, string providerName)
            : base(eventID, task, taskName, taskGuid, opcode, opcodeName, providerGuid, providerName)
        {
            Action = action;
        }

        /// <summary>Length of the encoded buffer (EventSource int32 length prefix).</summary>
        public int Length { get { return GetInt32At(0); } }

        /// <summary>The encoded async profiler buffer.</summary>
        public byte[] Buffer { get { return GetByteArrayAt(4, Length); } }

        public override string[] PayloadNames { get { return s_payloadNames; } }

        public override object PayloadValue(int index)
        {
            switch (index)
            {
                case 0: return Length;
                case 1: return Buffer;
                default:
                    Debug.Assert(false, "Bad field index");
                    return null;
            }
        }

        public override StringBuilder ToXml(StringBuilder sb)
        {
            Prefix(sb);
            XmlAttrib(sb, "Length", Length);
            XmlAttribHex(sb, "Buffer", Buffer);
            sb.Append("/>");
            return sb;
        }

        protected internal override void Dispatch() { Action?.Invoke(this); }

        protected internal override Delegate Target
        {
            get { return Action; }
            set { Action = (Action<AsyncEventsTraceData>)value; }
        }

        private static void XmlAttribHex(StringBuilder sb, string name, byte[] bytes)
        {
            sb.Append(' ').Append(name).Append("=\"");
            if (bytes != null)
            {
                int max = Math.Min(bytes.Length, 64);
                for (int i = 0; i < max; i++)
                {
                    sb.Append(bytes[i].ToString("X2"));
                }
                if (bytes.Length > max) sb.Append("...");
            }
            sb.Append('"');
        }
    }

    /// <summary>
    /// Base class for typed sub-event arguments dispatched by <see cref="AsyncProfilerTraceEventParser"/>.
    /// Carries the originating raw event (for ProcessID/TimeStamp/etc. correlation) plus the
    /// resolved QPC timestamp and per-buffer context.
    /// </summary>
    public abstract class AsyncProfilerSubEventArgs
    {
        protected AsyncProfilerSubEventArgs(AsyncEventsTraceData rawEvent, AsyncProfilerTraceEventParser.AsyncEventID eventId, long resolvedQpc, BufferHeader header)
        {
            RawEvent = rawEvent;
            EventId = eventId;
            ResolvedQpc = resolvedQpc;
            Header = header;
            // Cache ProcessID at construction so consumers don't have to keep RawEvent live
            // — and so unit tests with a null/fake RawEvent can still inspect it.
            ProcessID = rawEvent != null ? rawEvent.ProcessID : 0;
        }

        /// <summary>The originating <c>AsyncEvents</c> trace event (for ProcessID/TimeStamp lookup).</summary>
        public AsyncEventsTraceData RawEvent { get; }
        /// <summary>Sub-event identifier.</summary>
        public AsyncProfilerTraceEventParser.AsyncEventID EventId { get; }
        /// <summary>Absolute QPC timestamp at which the sub-event occurred (NOT the buffer flush time).</summary>
        public long ResolvedQpc { get; }
        /// <summary>The buffer header context (AsyncThreadContextId, OsThreadId, etc.).</summary>
        public BufferHeader Header { get; }

        public int ProcessID { get; }
        public uint AsyncThreadContextId { get { return Header.AsyncThreadContextId; } }
        public ulong OsThreadId { get { return Header.OsThreadId; } }
    }

    public sealed class CreateAsyncContextTraceData : AsyncProfilerSubEventArgs
    {
        public CreateAsyncContextTraceData(AsyncEventsTraceData raw, long qpc, BufferHeader header, ulong taskId)
            : base(raw, AsyncProfilerTraceEventParser.AsyncEventID.CreateAsyncContext, qpc, header)
        { TaskId = taskId; }

        public ulong TaskId { get; }
    }

    public sealed class ResumeAsyncContextTraceData : AsyncProfilerSubEventArgs
    {
        public ResumeAsyncContextTraceData(AsyncEventsTraceData raw, long qpc, BufferHeader header, ulong taskId)
            : base(raw, AsyncProfilerTraceEventParser.AsyncEventID.ResumeAsyncContext, qpc, header)
        { TaskId = taskId; }

        public ulong TaskId { get; }
    }

    public sealed class SuspendAsyncContextTraceData : AsyncProfilerSubEventArgs
    {
        public SuspendAsyncContextTraceData(AsyncEventsTraceData raw, long qpc, BufferHeader header, ulong currentTaskId)
            : base(raw, AsyncProfilerTraceEventParser.AsyncEventID.SuspendAsyncContext, qpc, header)
        { TaskId = currentTaskId; }

        /// <summary>The task id active on the thread when the suspend was observed (carried from preceding Resume/Create).</summary>
        public ulong TaskId { get; }
    }

    public sealed class CompleteAsyncContextTraceData : AsyncProfilerSubEventArgs
    {
        public CompleteAsyncContextTraceData(AsyncEventsTraceData raw, long qpc, BufferHeader header, ulong currentTaskId)
            : base(raw, AsyncProfilerTraceEventParser.AsyncEventID.CompleteAsyncContext, qpc, header)
        { TaskId = currentTaskId; }

        public ulong TaskId { get; }
    }

    public sealed class UnwindAsyncExceptionTraceData : AsyncProfilerSubEventArgs
    {
        public UnwindAsyncExceptionTraceData(AsyncEventsTraceData raw, long qpc, BufferHeader header, ulong currentTaskId, uint unwoundFrames)
            : base(raw, AsyncProfilerTraceEventParser.AsyncEventID.UnwindAsyncException, qpc, header)
        { TaskId = currentTaskId; UnwoundFrames = unwoundFrames; }

        public ulong TaskId { get; }
        public uint UnwoundFrames { get; }
    }

    public sealed class AsyncCallstackTraceData : AsyncProfilerSubEventArgs
    {
        public AsyncCallstackTraceData(AsyncEventsTraceData raw, AsyncProfilerTraceEventParser.AsyncEventID eventId, long qpc, BufferHeader header, CallstackPayload payload)
            : base(raw, eventId, qpc, header)
        { Payload = payload; }

        public CallstackPayload Payload { get; }
        public ulong TaskId { get { return Payload.TaskId; } }
        public IReadOnlyList<AsyncFrame> Frames { get { return Payload.Frames; } }
        public AsyncProfilerTraceEventParser.AsyncCallstackType Type { get { return Payload.Type; } }
        public bool Cached { get { return Payload.Cached; } }
    }

    public sealed class AsyncMethodTraceData : AsyncProfilerSubEventArgs
    {
        public AsyncMethodTraceData(AsyncEventsTraceData raw, long qpc, BufferHeader header, ulong currentTaskId, bool isComplete)
            : base(raw, isComplete ? AsyncProfilerTraceEventParser.AsyncEventID.CompleteAsyncMethod : AsyncProfilerTraceEventParser.AsyncEventID.ResumeAsyncMethod, qpc, header)
        { TaskId = currentTaskId; IsComplete = isComplete; }

        public ulong TaskId { get; }
        public bool IsComplete { get; }
    }

    public sealed class ResetAsyncTraceData : AsyncProfilerSubEventArgs
    {
        public ResetAsyncTraceData(AsyncEventsTraceData raw, AsyncProfilerTraceEventParser.AsyncEventID eventId, long qpc, BufferHeader header, ulong taskIdBeforeReset)
            : base(raw, eventId, qpc, header)
        { TaskIdBeforeReset = taskIdBeforeReset; }

        public ulong TaskIdBeforeReset { get; }
    }

    public sealed class AsyncProfilerMetadataTraceData : AsyncProfilerSubEventArgs
    {
        public AsyncProfilerMetadataTraceData(AsyncEventsTraceData raw, long qpc, BufferHeader header, ulong currentTaskId,
            ulong qpcFrequency, ulong qpcSync, ulong utcSync, uint eventBufferSize, byte wrapperCount)
            : base(raw, AsyncProfilerTraceEventParser.AsyncEventID.AsyncProfilerMetadata, qpc, header)
        {
            TaskId = currentTaskId;
            QpcFrequency = qpcFrequency;
            QpcSync = qpcSync;
            UtcSync = utcSync;
            EventBufferSize = eventBufferSize;
            WrapperCount = wrapperCount;
        }

        public ulong TaskId { get; }
        public ulong QpcFrequency { get; }
        public ulong QpcSync { get; }
        public ulong UtcSync { get; }
        public uint EventBufferSize { get; }
        public byte WrapperCount { get; }
    }

    public sealed class AsyncProfilerSyncClockTraceData : AsyncProfilerSubEventArgs
    {
        public AsyncProfilerSyncClockTraceData(AsyncEventsTraceData raw, long qpc, BufferHeader header, ulong currentTaskId, ulong qpcSync, ulong utcSync)
            : base(raw, AsyncProfilerTraceEventParser.AsyncEventID.AsyncProfilerSyncClock, qpc, header)
        {
            TaskId = currentTaskId;
            QpcSync = qpcSync;
            UtcSync = utcSync;
        }

        public ulong TaskId { get; }
        public ulong QpcSync { get; }
        public ulong UtcSync { get; }
    }

    /// <summary>
    /// Diagnostic event raised when a buffer cannot be fully decoded.  The raw event is
    /// still delivered via the <see cref="AsyncProfilerTraceEventParser.AsyncEvents"/> event;
    /// the malformed sub-event stream is dropped.
    /// </summary>
    public sealed class AsyncProfilerParseError
    {
        public AsyncProfilerParseError(AsyncEventsTraceData rawEvent, string message)
        {
            RawEvent = rawEvent;
            Message = message;
        }

        public AsyncEventsTraceData RawEvent { get; }
        public string Message { get; }
    }

    /// <summary>
    /// Sink interface used by <see cref="Microsoft.Diagnostics.Tracing.Parsers.AsyncProfilerTraceEventParser.ParseBuffer"/>
    /// to deliver decoded sub-events.  Allows the buffer decoder to be exercised independently
    /// of a live <c>TraceEventSource</c>; the parser itself implements a private sink that
    /// forwards to its own typed C# events.
    /// </summary>
    public interface IAsyncProfilerSubEventSink
    {
        void OnCreateAsyncContext(CreateAsyncContextTraceData e);
        void OnResumeAsyncContext(ResumeAsyncContextTraceData e);
        void OnSuspendAsyncContext(SuspendAsyncContextTraceData e);
        void OnCompleteAsyncContext(CompleteAsyncContextTraceData e);
        void OnUnwindAsyncException(UnwindAsyncExceptionTraceData e);
        void OnCreateAsyncCallstack(AsyncCallstackTraceData e);
        void OnResumeAsyncCallstack(AsyncCallstackTraceData e);
        void OnSuspendAsyncCallstack(AsyncCallstackTraceData e);
        void OnResumeAsyncMethod(AsyncMethodTraceData e);
        void OnCompleteAsyncMethod(AsyncMethodTraceData e);
        void OnResetAsyncThreadContext(ResetAsyncTraceData e);
        void OnResetAsyncContinuationWrapperIndex(ResetAsyncTraceData e);
        void OnAsyncProfilerMetadata(AsyncProfilerMetadataTraceData e);
        void OnAsyncProfilerSyncClock(AsyncProfilerSyncClockTraceData e);
        void OnParseError(AsyncProfilerParseError e);
    }
}
