// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Http.HPack;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2.FlowControl;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure.PipeWriterHelpers;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2;

internal class Http2FrameWriter
{
    // Literal Header Field without Indexing - Indexed Name (Index 8 - :status)
    // This uses C# compiler's ability to refer to static data directly. For more information see https://vcsjones.dev/2019/02/01/csharp-readonly-span-bytes-static
    private static ReadOnlySpan<byte> ContinueBytes => new byte[] { 0x08, 0x03, (byte)'1', (byte)'0', (byte)'0' };

    private readonly object _writeLock = new object();
    private readonly Http2Frame _outgoingFrame;
    private readonly Http2HeadersEnumerator _headersEnumerator = new Http2HeadersEnumerator();
    private readonly ConcurrentPipeWriter _outputWriter;
    private readonly BaseConnectionContext _connectionContext;
    private readonly Http2Connection _http2Connection;
    private readonly OutputFlowControl _connectionOutputFlowControl;
    private readonly string _connectionId;
    private readonly KestrelTrace _log;
    private readonly ITimeoutControl _timeoutControl;
    private readonly MinDataRate? _minResponseDataRate;
    private readonly TimingPipeFlusher _flusher;
    private readonly DynamicHPackEncoder _hpackEncoder;

    // This is only set to true by tests.
    private readonly bool _scheduleInline;

    private uint _maxFrameSize = Http2PeerSettings.MinAllowedMaxFrameSize;
    private byte[] _headerEncodingBuffer;
    private long _unflushedBytes;

    private bool _completed;
    private bool _aborted;

    public Http2FrameWriter(
        PipeWriter outputPipeWriter,
        BaseConnectionContext connectionContext,
        Http2Connection http2Connection,
        OutputFlowControl connectionOutputFlowControl,
        ITimeoutControl timeoutControl,
        MinDataRate? minResponseDataRate,
        string connectionId,
        MemoryPool<byte> memoryPool,
        ServiceContext serviceContext)
    {
        // Allow appending more data to the PipeWriter when a flush is pending.
        _outputWriter = new ConcurrentPipeWriter(outputPipeWriter, memoryPool, _writeLock);
        _connectionContext = connectionContext;
        _http2Connection = http2Connection;
        _connectionOutputFlowControl = connectionOutputFlowControl;
        _connectionId = connectionId;
        _log = serviceContext.Log;
        _timeoutControl = timeoutControl;
        _minResponseDataRate = minResponseDataRate;
        _flusher = new TimingPipeFlusher(timeoutControl, serviceContext.Log);
        _flusher.Initialize(_outputWriter);
        _outgoingFrame = new Http2Frame();
        _headerEncodingBuffer = new byte[_maxFrameSize];

        _scheduleInline = serviceContext.Scheduler == PipeScheduler.Inline;

        _hpackEncoder = new DynamicHPackEncoder(serviceContext.ServerOptions.AllowResponseHeaderCompression);
    }

    public void UpdateMaxHeaderTableSize(uint maxHeaderTableSize)
    {
        lock (_writeLock)
        {
            _hpackEncoder.UpdateMaxHeaderTableSize(maxHeaderTableSize);
        }
    }

    public void UpdateMaxFrameSize(uint maxFrameSize)
    {
        lock (_writeLock)
        {
            if (_maxFrameSize != maxFrameSize)
            {
                _maxFrameSize = maxFrameSize;
                _headerEncodingBuffer = new byte[_maxFrameSize];
            }
        }
    }

    public void Complete()
    {
        lock (_writeLock)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _connectionOutputFlowControl.Abort();
            _outputWriter.Abort();
        }
    }

    public void Abort(ConnectionAbortedException error)
    {
        lock (_writeLock)
        {
            if (_aborted)
            {
                return;
            }

            _aborted = true;
            _connectionContext.Abort(error);

            Complete();
        }
    }

    public ValueTask<FlushResult> FlushAsync(IHttpOutputAborter? outputAborter, CancellationToken cancellationToken)
    {
        lock (_writeLock)
        {
            if (_completed)
            {
                return default;
            }

            var bytesWritten = _unflushedBytes;
            _unflushedBytes = 0;

            return _flusher.FlushAsync(_minResponseDataRate, bytesWritten, outputAborter, cancellationToken);
        }
    }

    public ValueTask<FlushResult> Write100ContinueAsync(int streamId)
    {
        lock (_writeLock)
        {
            if (_completed)
            {
                return default;
            }

            _outgoingFrame.PrepareHeaders(Http2HeadersFrameFlags.END_HEADERS, streamId);
            _outgoingFrame.PayloadLength = ContinueBytes.Length;
            WriteHeaderUnsynchronized();
            _outputWriter.Write(ContinueBytes);
            return TimeFlushUnsynchronizedAsync();
        }
    }

    // Optional header fields for padding and priority are not implemented.
    /* https://tools.ietf.org/html/rfc7540#section-6.2
        +---------------+
        |Pad Length? (8)|
        +-+-------------+-----------------------------------------------+
        |E|                 Stream Dependency? (31)                     |
        +-+-------------+-----------------------------------------------+
        |  Weight? (8)  |
        +-+-------------+-----------------------------------------------+
        |                   Header Block Fragment (*)                 ...
        +---------------------------------------------------------------+
        |                           Padding (*)                       ...
        +---------------------------------------------------------------+
    */
    public void WriteResponseHeaders(int streamId, int statusCode, Http2HeadersFrameFlags headerFrameFlags, HttpResponseHeaders headers)
    {
        lock (_writeLock)
        {
            if (_completed)
            {
                return;
            }

            try
            {
                _headersEnumerator.Initialize(headers);
                _outgoingFrame.PrepareHeaders(headerFrameFlags, streamId);
                var buffer = _headerEncodingBuffer.AsSpan();
                var done = HPackHeaderWriter.BeginEncodeHeaders(statusCode, _hpackEncoder, _headersEnumerator, buffer, out var payloadLength);
                FinishWritingHeaders(streamId, payloadLength, done);
            }
            // Any exception from the HPack encoder can leave the dynamic table in a corrupt state.
            // Since we allow custom header encoders we don't know what type of exceptions to expect.
            catch (Exception ex)
            {
                _log.HPackEncodingError(_connectionId, streamId, ex);
                _http2Connection.Abort(new ConnectionAbortedException(ex.Message, ex));
                throw new InvalidOperationException(ex.Message, ex); // Report the error to the user if this was the first write.
            }
        }
    }

    public ValueTask<FlushResult> WriteResponseTrailersAsync(int streamId, HttpResponseTrailers headers)
    {
        lock (_writeLock)
        {
            if (_completed)
            {
                return default;
            }

            try
            {
                _headersEnumerator.Initialize(headers);
                _outgoingFrame.PrepareHeaders(Http2HeadersFrameFlags.END_STREAM, streamId);
                var buffer = _headerEncodingBuffer.AsSpan();
                var done = HPackHeaderWriter.BeginEncodeHeaders(_hpackEncoder, _headersEnumerator, buffer, out var payloadLength);
                FinishWritingHeaders(streamId, payloadLength, done);
            }
            // Any exception from the HPack encoder can leave the dynamic table in a corrupt state.
            // Since we allow custom header encoders we don't know what type of exceptions to expect.
            catch (Exception ex)
            {
                _log.HPackEncodingError(_connectionId, streamId, ex);
                _http2Connection.Abort(new ConnectionAbortedException(ex.Message, ex));
            }

            return TimeFlushUnsynchronizedAsync();
        }
    }

    private void FinishWritingHeaders(int streamId, int payloadLength, bool done)
    {
        var buffer = _headerEncodingBuffer.AsSpan();
        _outgoingFrame.PayloadLength = payloadLength;
        if (done)
        {
            _outgoingFrame.HeadersFlags |= Http2HeadersFrameFlags.END_HEADERS;
        }

        WriteHeaderUnsynchronized();
        _outputWriter.Write(buffer.Slice(0, payloadLength));

        while (!done)
        {
            _outgoingFrame.PrepareContinuation(Http2ContinuationFrameFlags.NONE, streamId);

            done = HPackHeaderWriter.ContinueEncodeHeaders(_hpackEncoder, _headersEnumerator, buffer, out payloadLength);
            _outgoingFrame.PayloadLength = payloadLength;

            if (done)
            {
                _outgoingFrame.ContinuationFlags = Http2ContinuationFrameFlags.END_HEADERS;
            }

            WriteHeaderUnsynchronized();
            _outputWriter.Write(buffer.Slice(0, payloadLength));
        }
    }

    public ValueTask<FlushResult> WriteDataAsync(int streamId, StreamOutputFlowControl flowControl, in ReadOnlySequence<byte> data, bool endStream, bool firstWrite, bool forceFlush)
    {
        // Logic in this method is replicated in WriteDataAndTrailersAsync.
        // Changes here may need to be mirrored in WriteDataAndTrailersAsync.

        // The Length property of a ReadOnlySequence can be expensive, so we cache the value.
        var dataLength = data.Length;

        lock (_writeLock)
        {
            if (_completed || flowControl.IsAborted)
            {
                return default;
            }

            // Zero-length data frames are allowed to be sent immediately even if there is no space available in the flow control window.
            // https://httpwg.org/specs/rfc7540.html#rfc.section.6.9.1
            if (dataLength != 0 && dataLength > flowControl.Available)
            {
                return WriteDataAsync(streamId, flowControl, data, dataLength, endStream, firstWrite);
            }

            // This cast is safe since if dataLength would overflow an int, it's guaranteed to be greater than the available flow control window.
            flowControl.Advance((int)dataLength);
            WriteDataUnsynchronized(streamId, data, dataLength, endStream);

            if (forceFlush)
            {
                return TimeFlushUnsynchronizedAsync();
            }

            return default;
        }
    }

    public ValueTask<FlushResult> WriteDataAndTrailersAsync(int streamId, StreamOutputFlowControl flowControl, in ReadOnlySequence<byte> data, bool firstWrite, HttpResponseTrailers headers)
    {
        // This method combines WriteDataAsync and WriteResponseTrailers.
        // Changes here may need to be mirrored in WriteDataAsync.

        // The Length property of a ReadOnlySequence can be expensive, so we cache the value.
        var dataLength = data.Length;

        lock (_writeLock)
        {
            if (_completed || flowControl.IsAborted)
            {
                return default;
            }

            // Zero-length data frames are allowed to be sent immediately even if there is no space available in the flow control window.
            // https://httpwg.org/specs/rfc7540.html#rfc.section.6.9.1
            if (dataLength != 0 && dataLength > flowControl.Available)
            {
                return WriteDataAndTrailersAsyncCore(this, streamId, flowControl, data, dataLength, firstWrite, headers);
            }

            // This cast is safe since if dataLength would overflow an int, it's guaranteed to be greater than the available flow control window.
            flowControl.Advance((int)dataLength);
            WriteDataUnsynchronized(streamId, data, dataLength, endStream: false);

            return WriteResponseTrailersAsync(streamId, headers);
        }

        static async ValueTask<FlushResult> WriteDataAndTrailersAsyncCore(Http2FrameWriter writer, int streamId, StreamOutputFlowControl flowControl, ReadOnlySequence<byte> data, long dataLength, bool firstWrite, HttpResponseTrailers headers)
        {
            await writer.WriteDataAsync(streamId, flowControl, data, dataLength, endStream: false, firstWrite);

            return await writer.WriteResponseTrailersAsync(streamId, headers);
        }
    }

    /*  Padding is not implemented
        +---------------+
        |Pad Length? (8)|
        +---------------+-----------------------------------------------+
        |                            Data (*)                         ...
        +---------------------------------------------------------------+
        |                           Padding (*)                       ...
        +---------------------------------------------------------------+
    */
    private void WriteDataUnsynchronized(int streamId, in ReadOnlySequence<byte> data, long dataLength, bool endStream)
    {
        Debug.Assert(dataLength == data.Length);

        // Note padding is not implemented
        _outgoingFrame.PrepareData(streamId);

        if (dataLength > _maxFrameSize) // Minus padding
        {
            TrimAndWriteDataUnsynchronized(in data, dataLength, endStream);
            return;
        }

        if (endStream)
        {
            _outgoingFrame.DataFlags |= Http2DataFrameFlags.END_STREAM;
        }

        _outgoingFrame.PayloadLength = (int)dataLength; // Plus padding

        WriteHeaderUnsynchronized();

        data.CopyTo(_outputWriter);

        // Plus padding
        return;

        void TrimAndWriteDataUnsynchronized(in ReadOnlySequence<byte> data, long dataLength, bool endStream)
        {
            Debug.Assert(dataLength == data.Length);

            var dataPayloadLength = (int)_maxFrameSize; // Minus padding

            Debug.Assert(dataLength > dataPayloadLength);

            var remainingData = data;
            do
            {
                var currentData = remainingData.Slice(0, dataPayloadLength);
                _outgoingFrame.PayloadLength = dataPayloadLength; // Plus padding

                WriteHeaderUnsynchronized();

                foreach (var buffer in currentData)
                {
                    _outputWriter.Write(buffer.Span);
                }

                // Plus padding
                dataLength -= dataPayloadLength;
                remainingData = remainingData.Slice(dataPayloadLength);

            } while (dataLength > dataPayloadLength);

            if (endStream)
            {
                _outgoingFrame.DataFlags |= Http2DataFrameFlags.END_STREAM;
            }

            _outgoingFrame.PayloadLength = (int)dataLength; // Plus padding

            WriteHeaderUnsynchronized();

            foreach (var buffer in remainingData)
            {
                _outputWriter.Write(buffer.Span);
            }

            // Plus padding
        }
    }

    private async ValueTask<FlushResult> WriteDataAsync(int streamId, StreamOutputFlowControl flowControl, ReadOnlySequence<byte> data, long dataLength, bool endStream, bool firstWrite)
    {
        FlushResult flushResult = default;

        while (dataLength > 0)
        {
            ValueTask<object?> availabilityTask;
            var writeTask = default(ValueTask<FlushResult>);

            lock (_writeLock)
            {
                if (_completed || flowControl.IsAborted)
                {
                    break;
                }

                // Observe HTTP/2 backpressure
                var actual = flowControl.AdvanceUpToAndWait(dataLength, out availabilityTask);

                var shouldFlush = false;

                if (actual > 0)
                {
                    if (actual < dataLength)
                    {
                        WriteDataUnsynchronized(streamId, data.Slice(0, actual), actual, endStream: false);
                        data = data.Slice(actual);
                        dataLength -= actual;
                    }
                    else
                    {
                        WriteDataUnsynchronized(streamId, data, actual, endStream);
                        dataLength = 0;
                    }

                    // Don't call FlushAsync() with the min data rate, since we time this write while also accounting for
                    // flow control induced backpressure below.
                    shouldFlush = true;
                }
                else if (firstWrite)
                {
                    // If we're facing flow control induced backpressure on the first write for a given stream's response body,
                    // we make sure to flush the response headers immediately.
                    shouldFlush = true;
                }

                if (shouldFlush)
                {
                    if (_minResponseDataRate != null)
                    {
                        // Call BytesWrittenToBuffer before FlushAsync() to make testing easier, otherwise the Flush can cause test code to run before the timeout
                        // control updates and if the test checks for a timeout it can fail
                        _timeoutControl.BytesWrittenToBuffer(_minResponseDataRate, _unflushedBytes);
                    }

                    _unflushedBytes = 0;

                    writeTask = _flusher.FlushAsync();
                }

                firstWrite = false;
            }

            // Avoid timing writes that are already complete. This is likely to happen during the last iteration.
            if (availabilityTask.IsCompleted && writeTask.IsCompleted)
            {
                continue;
            }

            if (_minResponseDataRate != null)
            {
                _timeoutControl.StartTimingWrite();
            }

            // This awaitable releases continuations in FIFO order when the window updates.
            // It should be very rare for a continuation to run without any availability.
            if (!availabilityTask.IsCompleted)
            {
                await availabilityTask;
            }

            flushResult = await writeTask;

            if (_minResponseDataRate != null)
            {
                _timeoutControl.StopTimingWrite();
            }
        }

        if (!_scheduleInline)
        {
            // Ensure that the application continuation isn't executed inline by ProcessWindowUpdateFrameAsync.
            await ThreadPoolAwaitable.Instance;
        }

        return flushResult;
    }

    /* https://tools.ietf.org/html/rfc7540#section-6.9
        +-+-------------------------------------------------------------+
        |R|              Window Size Increment (31)                     |
        +-+-------------------------------------------------------------+
    */
    public ValueTask<FlushResult> WriteWindowUpdateAsync(int streamId, int sizeIncrement)
    {
        lock (_writeLock)
        {
            if (_completed)
            {
                return default;
            }

            _outgoingFrame.PrepareWindowUpdate(streamId, sizeIncrement);
            WriteHeaderUnsynchronized();
            var buffer = _outputWriter.GetSpan(4);
            Bitshifter.WriteUInt31BigEndian(buffer, (uint)sizeIncrement, preserveHighestBit: false);
            _outputWriter.Advance(4);
            return TimeFlushUnsynchronizedAsync();
        }
    }

    /* https://tools.ietf.org/html/rfc7540#section-6.4
        +---------------------------------------------------------------+
        |                        Error Code (32)                        |
        +---------------------------------------------------------------+
    */
    public ValueTask<FlushResult> WriteRstStreamAsync(int streamId, Http2ErrorCode errorCode)
    {
        lock (_writeLock)
        {
            if (_completed)
            {
                return default;
            }

            _outgoingFrame.PrepareRstStream(streamId, errorCode);
            WriteHeaderUnsynchronized();
            var buffer = _outputWriter.GetSpan(4);
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)errorCode);
            _outputWriter.Advance(4);

            return TimeFlushUnsynchronizedAsync();
        }
    }

    /* https://tools.ietf.org/html/rfc7540#section-6.5.1
        List of:
        +-------------------------------+
        |       Identifier (16)         |
        +-------------------------------+-------------------------------+
        |                        Value (32)                             |
        +---------------------------------------------------------------+
    */
    public ValueTask<FlushResult> WriteSettingsAsync(List<Http2PeerSetting> settings)
    {
        lock (_writeLock)
        {
            if (_completed)
            {
                return default;
            }

            _outgoingFrame.PrepareSettings(Http2SettingsFrameFlags.NONE);
            var settingsSize = settings.Count * Http2FrameReader.SettingSize;
            _outgoingFrame.PayloadLength = settingsSize;
            WriteHeaderUnsynchronized();

            var buffer = _outputWriter.GetSpan(settingsSize).Slice(0, settingsSize); // GetSpan isn't precise
            WriteSettings(settings, buffer);
            _outputWriter.Advance(settingsSize);

            return TimeFlushUnsynchronizedAsync();
        }
    }

    internal static void WriteSettings(List<Http2PeerSetting> settings, Span<byte> destination)
    {
        foreach (var setting in settings)
        {
            BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)setting.Parameter);
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(2), setting.Value);
            destination = destination.Slice(Http2FrameReader.SettingSize);
        }
    }

    // No payload
    public ValueTask<FlushResult> WriteSettingsAckAsync()
    {
        lock (_writeLock)
        {
            if (_completed)
            {
                return default;
            }

            _outgoingFrame.PrepareSettings(Http2SettingsFrameFlags.ACK);
            WriteHeaderUnsynchronized();
            return TimeFlushUnsynchronizedAsync();
        }
    }

    /* https://tools.ietf.org/html/rfc7540#section-6.7
        +---------------------------------------------------------------+
        |                                                               |
        |                      Opaque Data (64)                         |
        |                                                               |
        +---------------------------------------------------------------+
    */
    public ValueTask<FlushResult> WritePingAsync(Http2PingFrameFlags flags, in ReadOnlySequence<byte> payload)
    {
        lock (_writeLock)
        {
            if (_completed)
            {
                return default;
            }

            _outgoingFrame.PreparePing(flags);
            Debug.Assert(payload.Length == _outgoingFrame.PayloadLength); // 8
            WriteHeaderUnsynchronized();
            foreach (var segment in payload)
            {
                _outputWriter.Write(segment.Span);
            }

            return TimeFlushUnsynchronizedAsync();
        }
    }

    /* https://tools.ietf.org/html/rfc7540#section-6.8
        +-+-------------------------------------------------------------+
        |R|                  Last-Stream-ID (31)                        |
        +-+-------------------------------------------------------------+
        |                      Error Code (32)                          |
        +---------------------------------------------------------------+
        |                  Additional Debug Data (*)                    | (not implemented)
        +---------------------------------------------------------------+
    */
    public ValueTask<FlushResult> WriteGoAwayAsync(int lastStreamId, Http2ErrorCode errorCode)
    {
        lock (_writeLock)
        {
            if (_completed)
            {
                return default;
            }

            _outgoingFrame.PrepareGoAway(lastStreamId, errorCode);
            WriteHeaderUnsynchronized();

            var buffer = _outputWriter.GetSpan(8);
            Bitshifter.WriteUInt31BigEndian(buffer, (uint)lastStreamId, preserveHighestBit: false);
            buffer = buffer.Slice(4);
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)errorCode);
            _outputWriter.Advance(8);

            return TimeFlushUnsynchronizedAsync();
        }
    }

    private void WriteHeaderUnsynchronized()
    {
        _log.Http2FrameSending(_connectionId, _outgoingFrame);
        WriteHeader(_outgoingFrame, _outputWriter);

        // We assume the payload will be written prior to the next flush.
        _unflushedBytes += Http2FrameReader.HeaderLength + _outgoingFrame.PayloadLength;
    }

    /* https://tools.ietf.org/html/rfc7540#section-4.1
        +-----------------------------------------------+
        |                 Length (24)                   |
        +---------------+---------------+---------------+
        |   Type (8)    |   Flags (8)   |
        +-+-------------+---------------+-------------------------------+
        |R|                 Stream Identifier (31)                      |
        +=+=============================================================+
        |                   Frame Payload (0...)                      ...
        +---------------------------------------------------------------+
    */
    internal static void WriteHeader(Http2Frame frame, PipeWriter output)
    {
        var buffer = output.GetSpan(Http2FrameReader.HeaderLength);

        Bitshifter.WriteUInt24BigEndian(buffer, (uint)frame.PayloadLength);
        buffer = buffer.Slice(3);

        buffer[0] = (byte)frame.Type;
        buffer[1] = frame.Flags;
        buffer = buffer.Slice(2);

        Bitshifter.WriteUInt31BigEndian(buffer, (uint)frame.StreamId, preserveHighestBit: false);

        output.Advance(Http2FrameReader.HeaderLength);
    }

    private ValueTask<FlushResult> TimeFlushUnsynchronizedAsync()
    {
        var bytesWritten = _unflushedBytes;
        _unflushedBytes = 0;

        return _flusher.FlushAsync(_minResponseDataRate, bytesWritten);
    }

    public bool TryUpdateConnectionWindow(int bytes)
    {
        lock (_writeLock)
        {
            return _connectionOutputFlowControl.TryUpdateWindow(bytes);
        }
    }

    public bool TryUpdateStreamWindow(StreamOutputFlowControl flowControl, int bytes)
    {
        lock (_writeLock)
        {
            return flowControl.TryUpdateWindow(bytes);
        }
    }

    public void AbortPendingStreamDataWrites(StreamOutputFlowControl flowControl)
    {
        lock (_writeLock)
        {
            flowControl.Abort();
        }
    }
}
