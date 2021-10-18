// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using FFmpeg.AutoGen;
using osuTK;
using osu.Framework.Graphics.Textures;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Logging;
using osu.Framework.Platform;
using AGffmpeg = FFmpeg.AutoGen.ffmpeg;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// Represents a video decoder that can be used convert video streams and files into textures.
    /// </summary>
    public unsafe partial class VideoDecoder : IDisposable
    {
        /// <summary>
        /// Defines which pixel format is expected in <see cref="VideoTexture"/>
        /// </summary>
        private const AVPixelFormat expected_render_pixel_format = AVPixelFormat.AV_PIX_FMT_YUV420P;

        /// <summary>
        /// The duration of the video that is being decoded. Can only be queried after the decoder has started decoding has loaded. This value may be an estimate by FFmpeg, depending on the video loaded.
        /// </summary>
        public double Duration => stream == null ? 0 : duration * timeBaseInSeconds * 1000;

        /// <summary>
        /// True if the decoder currently does not decode any more frames, false otherwise.
        /// </summary>
        public bool IsRunning => State == DecoderState.Running;

        /// <summary>
        /// True if the decoder has faulted after starting to decode. You can try to restart a failed decoder by invoking <see cref="StartDecoding"/> again.
        /// </summary>
        public bool IsFaulted => State == DecoderState.Faulted;

        /// <summary>
        /// The timestamp of the last frame that was decoded by this video decoder, or 0 if no frames have been decoded.
        /// </summary>
        public float LastDecodedFrameTime => lastDecodedFrameTime;

        /// <summary>
        /// The frame rate of the video stream this decoder is decoding.
        /// </summary>
        public double FrameRate => stream == null ? 0 : stream->avg_frame_rate.GetValue();

        /// <summary>
        /// True if the decoder can seek, false otherwise. Determined by the stream this decoder was created with.
        /// </summary>
        public bool CanSeek => videoStream?.CanSeek == true;

        /// <summary>
        /// The current state of the decoding process.
        /// </summary>
        public DecoderState State { get; private set; }

        // libav-context-related
        private AVFormatContext* formatContext;
        private AVStream* stream;
        private AVCodecContext* codecContext;
        private SwsContext* swsContext;

        private byte* contextBuffer;
        private byte[] managedContextBuffer;

        private avio_alloc_context_read_packet readPacketCallback;
        private avio_alloc_context_seek seekCallback;

        private bool inputOpened;
        private bool isDisposed;
        private Stream videoStream;

        private double timeBaseInSeconds;
        private long duration;

        // active decoder state
        private volatile float lastDecodedFrameTime;

        private Task decodingTask;
        private CancellationTokenSource decodingTaskCancellationTokenSource;

        private double? skipOutputUntilTime;

        private readonly List<AVHWDeviceType> targetHwDecoders;

        private readonly ConcurrentQueue<DecodedFrame> decodedFrames;
        private readonly ConcurrentQueue<Action> decoderCommands;

        private readonly ConcurrentQueue<Texture> availableTextures;

        private ObjectHandle<VideoDecoder> handle;

        private readonly FFmpegFuncs ffmpeg;

        internal bool Looping;

        /// <summary>
        /// Creates a new video decoder that decodes the given video file.
        /// </summary>
        /// <param name="filename">The path to the file that should be decoded.</param>
        /// <param name="hwDecoder">The <see cref="HardwareVideoDecoder"/> that should be used for decode acceleration.</param>
        public VideoDecoder(string filename, HardwareVideoDecoder hwDecoder)
            : this(File.OpenRead(filename), hwDecoder)
        {
        }

        /// <summary>
        /// Creates a new video decoder that decodes the given video stream.
        /// </summary>
        /// <param name="videoStream">The stream that should be decoded.</param>
        /// <param name="hwDecoder">The <see cref="HardwareVideoDecoder"/> that should be used for decode acceleration.</param>
        public VideoDecoder(Stream videoStream, HardwareVideoDecoder hwDecoder)
        {
            ffmpeg = CreateFuncs();

            this.videoStream = videoStream;
            if (!videoStream.CanRead)
                throw new InvalidOperationException($"The given stream does not support reading. A stream used for a {nameof(VideoDecoder)} must support reading.");

            targetHwDecoders = hwDecoder.ToFfmpegHardwareDeviceTypes();
            State = DecoderState.Ready;
            decodedFrames = new ConcurrentQueue<DecodedFrame>();
            decoderCommands = new ConcurrentQueue<Action>();
            availableTextures = new ConcurrentQueue<Texture>(); // TODO: use "real" object pool when there's some public pool supporting disposables
            handle = new ObjectHandle<VideoDecoder>(this, GCHandleType.Normal);
        }

        /// <summary>
        /// Seek the decoder to the given timestamp. This will fail if <see cref="CanSeek"/> is false.
        /// </summary>
        /// <param name="targetTimestamp">The timestamp to seek to.</param>
        public void Seek(double targetTimestamp)
        {
            if (!CanSeek)
                throw new InvalidOperationException("This decoder cannot seek because the underlying stream used to decode the video does not support seeking.");

            decoderCommands.Enqueue(() =>
            {
                ffmpeg.av_seek_frame(formatContext, stream->index, (long)(targetTimestamp / timeBaseInSeconds / 1000.0), AGffmpeg.AVSEEK_FLAG_BACKWARD);
                skipOutputUntilTime = targetTimestamp;
                State = DecoderState.Ready;
            });
        }

        /// <summary>
        /// Returns the given frames back to the decoder, allowing the decoder to reuse the textures contained in the frames to draw new frames.
        /// </summary>
        /// <param name="frames">The frames that should be returned to the decoder.</param>
        public void ReturnFrames(IEnumerable<DecodedFrame> frames)
        {
            foreach (var f in frames)
            {
                ((VideoTexture)f.Texture.TextureGL).FlushUploads();
                availableTextures.Enqueue(f.Texture);
            }
        }

        /// <summary>
        /// Starts the decoding process. The decoding will happen asynchronously in a separate thread. The decoded frames can be retrieved by using <see cref="GetDecodedFrames"/>.
        /// </summary>
        public void StartDecoding()
        {
            if (decodingTask != null)
                throw new InvalidOperationException($"Cannot start decoding once already started. Call {nameof(StopDecoding)} first.");

            // only prepare for decoding if this is our first time starting the decoding process
            if (formatContext == null)
            {
                try
                {
                    prepareDecoding();
                }
                catch (Exception e)
                {
                    Logger.Log($"VideoDecoder faulted: {e}");
                    State = DecoderState.Faulted;
                    return;
                }
            }

            decodingTaskCancellationTokenSource = new CancellationTokenSource();
            decodingTask = Task.Factory.StartNew(() => decodingLoop(decodingTaskCancellationTokenSource.Token), decodingTaskCancellationTokenSource.Token, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Stops the decoding process. Optionally waits for the decoder thread to terminate.
        /// </summary>
        /// <param name="waitForDecoderExit">True if this method should wait for the decoder thread to terminate, false otherwise.</param>
        public void StopDecoding(bool waitForDecoderExit)
        {
            if (decodingTask == null)
                return;

            decodingTaskCancellationTokenSource.Cancel();
            if (waitForDecoderExit)
                decodingTask.Wait();

            decodingTask = null;
            decodingTaskCancellationTokenSource.Dispose();
            decodingTaskCancellationTokenSource = null;

            State = DecoderState.Ready;
        }

        /// <summary>
        /// Gets all frames that have been decoded by the decoder up until the point in time when this method was called.
        /// Retrieving decoded frames using this method consumes them, ie calling this method again will never retrieve the same frame twice.
        /// </summary>
        /// <returns>The frames that have been decoded up until the point in time this method was called.</returns>
        public IEnumerable<DecodedFrame> GetDecodedFrames()
        {
            var frames = new List<DecodedFrame>(decodedFrames.Count);
            while (decodedFrames.TryDequeue(out var df))
                frames.Add(df);

            return frames;
        }

        // https://en.wikipedia.org/wiki/YCbCr
        public Matrix3 GetConversionMatrix()
        {
            if (codecContext == null)
                return Matrix3.Zero;

            switch (codecContext->colorspace)
            {
                case AVColorSpace.AVCOL_SPC_BT709:
                    return new Matrix3(1.164f, 1.164f, 1.164f,
                        0.000f, -0.213f, 2.112f,
                        1.793f, -0.533f, 0.000f);

                case AVColorSpace.AVCOL_SPC_UNSPECIFIED:
                case AVColorSpace.AVCOL_SPC_SMPTE170M:
                case AVColorSpace.AVCOL_SPC_SMPTE240M:
                default:
                    return new Matrix3(1.164f, 1.164f, 1.164f,
                        0.000f, -0.392f, 2.017f,
                        1.596f, -0.813f, 0.000f);
            }
        }

        [MonoPInvokeCallback(typeof(avio_alloc_context_read_packet))]
        private static int readPacket(void* opaque, byte* bufferPtr, int bufferSize)
        {
            var handle = new ObjectHandle<VideoDecoder>((IntPtr)opaque);
            if (!handle.GetTarget(out VideoDecoder decoder))
                return 0;

            if (bufferSize != decoder.managedContextBuffer.Length)
                decoder.managedContextBuffer = new byte[bufferSize];

            var bytesRead = decoder.videoStream.Read(decoder.managedContextBuffer, 0, bufferSize);
            Marshal.Copy(decoder.managedContextBuffer, 0, (IntPtr)bufferPtr, bytesRead);
            return bytesRead;
        }

        [MonoPInvokeCallback(typeof(avio_alloc_context_seek))]
        private static long streamSeekCallbacks(void* opaque, long offset, int whence)
        {
            var handle = new ObjectHandle<VideoDecoder>((IntPtr)opaque);
            if (!handle.GetTarget(out VideoDecoder decoder))
                return -1;

            if (!decoder.videoStream.CanSeek)
                throw new InvalidOperationException("Tried seeking on a video sourced by a non-seekable stream.");

            switch (whence)
            {
                case StdIo.SEEK_CUR:
                    decoder.videoStream.Seek(offset, SeekOrigin.Current);
                    break;

                case StdIo.SEEK_END:
                    decoder.videoStream.Seek(offset, SeekOrigin.End);
                    break;

                case StdIo.SEEK_SET:
                    decoder.videoStream.Seek(offset, SeekOrigin.Begin);
                    break;

                case AGffmpeg.AVSEEK_SIZE:
                    return decoder.videoStream.Length;

                default:
                    return -1;
            }

            return decoder.videoStream.Position;
        }

        /// <remarks>
        /// Returned HW devices are not guaranteed to be available on the current machine, they only represent what the loaded FFmpeg libraries support.
        /// </remarks>
        private IEnumerable<(FFmpegCodec codec, IEnumerable<AVHWDeviceType> usableHwDeviceTypes)> getAvailableDecoders(AVCodecID codecId)
        {
            var codecs = new List<(FFmpegCodec, IEnumerable<AVHWDeviceType>)>();
            FFmpegCodec? firstCodec = null;

            void* iterator = null;

            while (true)
            {
                var avCodec = ffmpeg.av_codec_iterate(&iterator);

                if (avCodec == null) break;

                var codec = new FFmpegCodec(avCodec, ffmpeg);
                if (codec.Id != codecId || !codec.IsDecoder) continue;

                firstCodec ??= codec;

                if (targetHwDecoders.Count == 0)
                    break;

                // Note: Intersect order here is important, order of the returned elements is determined by the first enumerable.
                // This means that we have better control over it by calling Intersect from `targetHwDecoders`.
                // See `HardwareVideoDecoder.ToFfmpegHardwareDeviceTypes`
                var usableHwDeviceTypes = targetHwDecoders.Intersect(codec.SupportedHwDeviceTypes.Value).ToList();
                if (usableHwDeviceTypes.Count == 0)
                    continue;

                codecs.Add((codec, usableHwDeviceTypes));
            }

            // default to the first codec that we found with no HW devices.
            // The first codec is what FFmpeg's `avcodec_find_decoder` would return so this way we'll automatically fallback to that.
            if (firstCodec.HasValue)
                codecs.Add((firstCodec.Value, Array.Empty<AVHWDeviceType>()));

            return codecs;
        }

        // sets up libavformat state: creates the AVFormatContext, the frames, etc. to start decoding, but does not actually start the decodingLoop
        private void prepareDecoding()
        {
            const int context_buffer_size = 4096;

            // the first call to FFmpeg will throw an exception if the libraries cannot be found
            // this will be safely handled in StartDecoding()
            var fcPtr = ffmpeg.avformat_alloc_context();
            formatContext = fcPtr;
            contextBuffer = (byte*)ffmpeg.av_malloc(context_buffer_size);
            managedContextBuffer = new byte[context_buffer_size];
            readPacketCallback = readPacket;
            seekCallback = streamSeekCallbacks;
            formatContext->pb = ffmpeg.avio_alloc_context(contextBuffer, context_buffer_size, 0, (void*)handle.Handle, readPacketCallback, null, seekCallback);

            int openInputResult = ffmpeg.avformat_open_input(&fcPtr, "dummy", null, null);
            inputOpened = openInputResult >= 0;
            if (!inputOpened)
                throw new InvalidOperationException($"Error opening file or stream: {getErrorMessage(openInputResult)}");

            int findStreamInfoResult = ffmpeg.avformat_find_stream_info(formatContext, null);
            if (findStreamInfoResult < 0)
                throw new InvalidOperationException($"Error finding stream info: {getErrorMessage(findStreamInfoResult)}");

            var streamIndex = ffmpeg.av_find_best_stream(formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (streamIndex < 0)
                throw new InvalidOperationException($"Couldn't find video stream: {getErrorMessage(streamIndex)}");

            stream = formatContext->streams[streamIndex];
            duration = stream->duration <= 0 ? formatContext->duration : stream->duration;
            timeBaseInSeconds = stream->time_base.GetValue();

            var codecParams = *stream->codecpar;
            bool openSuccessful = false;

            foreach (var (decoder, hwDeviceTypes) in getAvailableDecoders(codecParams.codec_id))
            {
                // free context in case it was allocated in a previous iteration.
                if (codecContext != null)
                {
                    fixed (AVCodecContext** ptr = &codecContext)
                        ffmpeg.avcodec_free_context(ptr);
                }

                codecContext = ffmpeg.avcodec_alloc_context3(decoder.Pointer);

                if (codecContext == null)
                {
                    Logger.Log($"Couldn't allocate codec context. Codec id: {codecParams.codec_id}");
                    continue;
                }

                int paramCopyResult = ffmpeg.avcodec_parameters_to_context(codecContext, &codecParams);

                if (paramCopyResult < 0)
                {
                    Logger.Log($"Couldn't copy codec parameters with id {codecParams.codec_id}: {getErrorMessage(paramCopyResult)}");
                    continue;
                }

                // initilize hardware decode device
                foreach (var hwDeviceType in hwDeviceTypes)
                {
                    int hwDeviceCreateResult = ffmpeg.av_hwdevice_ctx_create(&codecContext->hw_device_ctx, hwDeviceType, null, null, 0);

                    if (hwDeviceCreateResult < 0)
                    {
                        Logger.Log($"Couldn't open hardware video decoder {hwDeviceType}: {getErrorMessage(hwDeviceCreateResult)}");
                    }
                    else
                    {
                        Logger.Log($"Successfully opened hardware video decoder {hwDeviceType} for codec {codecParams.codec_id}");
                        break;
                    }
                }

                int openCodecResult = ffmpeg.avcodec_open2(codecContext, decoder.Pointer, null);

                if (openCodecResult < 0)
                {
                    Logger.Log($"Error trying to open codec with id {codecParams.codec_id}: {getErrorMessage(openCodecResult)}");
                    continue;
                }

                openSuccessful = true;
                break;
            }

            if (!openSuccessful)
                throw new InvalidOperationException("No usable decoder found");
        }

        private void decodingLoop(CancellationToken cancellationToken)
        {
            var packet = ffmpeg.av_packet_alloc();
            var receiveFrame = ffmpeg.av_frame_alloc();

            const int max_pending_frames = 3;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    switch (State)
                    {
                        case DecoderState.Ready:
                        case DecoderState.Running:
                            if (decodedFrames.Count < max_pending_frames)
                            {
                                decodeNextFrame(packet, receiveFrame);
                            }
                            else
                            {
                                // wait until existing buffers are consumed.
                                State = DecoderState.Ready;
                                Thread.Sleep(1);
                            }

                            break;

                        case DecoderState.EndOfStream:
                            // While at the end of the stream, avoid attempting to read further as this comes with a non-negligible overhead.
                            // A Seek() operation will trigger a state change, allowing decoding to potentially start again.
                            Thread.Sleep(50);
                            break;

                        default:
                            Debug.Fail($"Video decoder should never be in a \"{State}\" state during decode.");
                            return;
                    }

                    while (!decoderCommands.IsEmpty)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        if (decoderCommands.TryDequeue(out var cmd))
                            cmd();
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "VideoDecoder faulted");
                State = DecoderState.Faulted;
            }
            finally
            {
                ffmpeg.av_packet_free(&packet);
                ffmpeg.av_frame_free(&receiveFrame);

                if (State != DecoderState.Faulted)
                    State = DecoderState.Stopped;
            }
        }

        private void decodeNextFrame(AVPacket* packet, AVFrame* receiveFrame)
        {
            // read data from input into AVPacket.
            // only read if the packet is empty, otherwise we would overwrite what's already there which can lead to visual glitches.
            int readFrameResult = 0;
            if (packet->buf == null)
                readFrameResult = ffmpeg.av_read_frame(formatContext, packet);

            if (readFrameResult >= 0)
            {
                State = DecoderState.Running;

                bool unrefPacket = true;

                if (packet->stream_index == stream->index)
                {
                    // send the packet for decoding.
                    int sendPacketResult = ffmpeg.avcodec_send_packet(codecContext, packet);

                    // Note: EAGAIN can be returned if there's too many pending frames so we have to read them,
                    // otherwise we would get stuck in an infinite loop.
                    if (sendPacketResult == 0 || sendPacketResult == -AGffmpeg.EAGAIN)
                    {
                        readDecodedFrames(receiveFrame);

                        // keep the packet data for next frame if we didn't send it successfully.
                        if (sendPacketResult != 0)
                            unrefPacket = false;
                    }
                    else
                        Logger.Log($"Failed to send avcodec packet: {getErrorMessage(sendPacketResult)}");
                }

                if (unrefPacket)
                    ffmpeg.av_packet_unref(packet);
            }
            else if (readFrameResult == AGffmpeg.AVERROR_EOF)
            {
                if (Looping)
                {
                    Seek(0);
                }
                else
                {
                    // This marks the video stream as no longer relevant (until a future potential Seek operation).
                    State = DecoderState.EndOfStream;
                }
            }
            else if (readFrameResult == -AGffmpeg.EAGAIN)
            {
                State = DecoderState.Ready;
                Thread.Sleep(1);
            }
            else
            {
                Logger.Log($"Failed to read data into avcodec packet: {getErrorMessage(readFrameResult)}");
                Thread.Sleep(1);
            }
        }

        private readonly ConcurrentQueue<Frame> hwTransferFrames = new ConcurrentQueue<Frame>();
        private readonly ConcurrentQueue<Frame> scalerFrames = new ConcurrentQueue<Frame>();

        private void freeFrame(Frame frame) => ffmpeg.av_frame_free(&frame.Value);
        private void returnHwTransferFrame(Frame frame) => hwTransferFrames.Enqueue(frame);
        private void returnScalerFrame(Frame frame) => scalerFrames.Enqueue(frame);

        private void readDecodedFrames(AVFrame* receiveFrame)
        {
            while (true)
            {
                var receiveFrameResult = ffmpeg.avcodec_receive_frame(codecContext, receiveFrame);

                if (receiveFrameResult < 0)
                {
                    if (receiveFrameResult != -AGffmpeg.EAGAIN && receiveFrameResult != AGffmpeg.AVERROR_EOF)
                    {
                        Logger.Log($"Failed to receive frame from avcodec: {getErrorMessage(receiveFrameResult)}");
                    }

                    break;
                }

                var frameTime = (receiveFrame->pts - stream->start_time) * timeBaseInSeconds * 1000;

                if (skipOutputUntilTime > frameTime)
                    continue;

                // get final frame.
                Frame frame;

                if (((AVPixelFormat)receiveFrame->format).IsHardwarePixelFormat())
                {
                    // transfer data from HW decoder to RAM.
                    if (!hwTransferFrames.TryDequeue(out var hwTransferFrame))
                        hwTransferFrame = new Frame(ffmpeg.av_frame_alloc(), returnHwTransferFrame);

                    // WARNING: frames from `av_hwframe_transfer_data` have their timestamps set to AV_NOPTS_VALUE instead of real values.
                    // if you need to use them later, take them from `receiveFrame` before it's freed.
                    var transferResult = ffmpeg.av_hwframe_transfer_data(hwTransferFrame.Value, receiveFrame, 0);

                    if (transferResult < 0)
                    {
                        Logger.Log($"Failed to transfer frame from HW decoder: {getErrorMessage(transferResult)}");

                        // free the frame instead of enqueueing it in case that the failure was caused by it's configuration.
                        ffmpeg.av_frame_free(&hwTransferFrame.Value);
                        continue;
                    }

                    frame = hwTransferFrame;
                }
                else
                {
                    // copy data to a new AVFrame so that `readFrame` can be reused
                    frame = new Frame(ffmpeg.av_frame_alloc(), freeFrame);
                    ffmpeg.av_frame_move_ref(frame.Value, receiveFrame);
                }

                lastDecodedFrameTime = (float)frameTime;

                // if needed, convert the resulting frame to a format that we can render.
                var frameFormat = (AVPixelFormat)frame.Value->format;

                if (frameFormat != expected_render_pixel_format)
                {
                    swsContext = ffmpeg.sws_getCachedContext(
                        swsContext,
                        codecContext->width, codecContext->height, frameFormat,
                        codecContext->width, codecContext->height, expected_render_pixel_format,
                        1, null, null, null);

                    if (!scalerFrames.TryDequeue(out var scalerFrame))
                        scalerFrame = new Frame(ffmpeg.av_frame_alloc(), returnScalerFrame);

                    // set the scaler's output to the pix format that we need.
                    scalerFrame.Value->format = (int)expected_render_pixel_format;

                    // allocate buffer if the scaler frame settings don't match the decoded frame.
                    if (scalerFrame.Value->width != frame.Value->width || scalerFrame.Value->height != frame.Value->height)
                    {
                        scalerFrame.Value->width = frame.Value->width;
                        scalerFrame.Value->height = frame.Value->height;

                        var getBufferResult = ffmpeg.av_frame_get_buffer(scalerFrame.Value, 0);

                        if (getBufferResult < 0)
                        {
                            Logger.Log($"Failed to allocate SWS frame buffer: {getErrorMessage(getBufferResult)}");

                            frame.Return();
                            ffmpeg.av_frame_free(&scalerFrame.Value);
                            continue;
                        }
                    }

                    var scalerResult = ffmpeg.sws_scale(
                        swsContext,
                        frame.Value->data, frame.Value->linesize, 0, frame.Value->height,
                        scalerFrame.Value->data, scalerFrame.Value->linesize);

                    // return the original frame regardless of the scaler result.
                    frame.Return();

                    if (scalerResult < 0)
                    {
                        Logger.Log($"Failed to scale frame: {getErrorMessage(scalerResult)}");

                        ffmpeg.av_frame_free(&scalerFrame.Value);
                        continue;
                    }

                    frame = scalerFrame;
                }

                // create texture.
                if (!availableTextures.TryDequeue(out var tex))
                    tex = new Texture(new VideoTexture(frame.Value->width, frame.Value->height));

                var upload = new VideoTextureUpload(frame);

                tex.SetData(upload);
                decodedFrames.Enqueue(new DecodedFrame { Time = frameTime, Texture = tex });
            }
        }

        private string getErrorMessage(int errorCode)
        {
            const ulong buffer_size = 256;
            byte[] buffer = new byte[buffer_size];

            int strErrorCode;

            fixed (byte* bufPtr = buffer)
            {
                strErrorCode = ffmpeg.av_strerror(errorCode, bufPtr, buffer_size);
            }

            if (strErrorCode < 0)
                return $"{errorCode} (av_strerror failed with code {strErrorCode})";

            var messageLength = Math.Max(0, Array.IndexOf(buffer, (byte)0));
            return $"{Encoding.ASCII.GetString(buffer[..messageLength])} ({errorCode})";
        }

        protected virtual FFmpegFuncs CreateFuncs()
        {
            // other frameworks should handle native libraries themselves
#if NET5_0
            AGffmpeg.GetOrLoadLibrary = name =>
            {
                int version = AGffmpeg.LibraryVersionMap[name];

                string libraryName = null;

                // "lib" prefix and extensions are resolved by .net core
                switch (RuntimeInfo.OS)
                {
                    case RuntimeInfo.Platform.macOS:
                        libraryName = $"{name}.{version}";
                        break;

                    case RuntimeInfo.Platform.Windows:
                        libraryName = $"{name}-{version}";
                        break;

                    case RuntimeInfo.Platform.Linux:
                        libraryName = name;
                        break;
                }

                return NativeLibrary.Load(libraryName, System.Reflection.Assembly.GetEntryAssembly(), DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
            };
#endif

            return new FFmpegFuncs
            {
                av_frame_alloc = AGffmpeg.av_frame_alloc,
                av_frame_free = AGffmpeg.av_frame_free,
                av_frame_unref = AGffmpeg.av_frame_unref,
                av_frame_move_ref = AGffmpeg.av_frame_move_ref,
                av_frame_get_buffer = AGffmpeg.av_frame_get_buffer,
                av_strdup = AGffmpeg.av_strdup,
                av_strerror = AGffmpeg.av_strerror,
                av_malloc = AGffmpeg.av_malloc,
                av_packet_alloc = AGffmpeg.av_packet_alloc,
                av_packet_unref = AGffmpeg.av_packet_unref,
                av_packet_free = AGffmpeg.av_packet_free,
                av_read_frame = AGffmpeg.av_read_frame,
                av_seek_frame = AGffmpeg.av_seek_frame,
                av_hwdevice_ctx_create = AGffmpeg.av_hwdevice_ctx_create,
                av_hwframe_transfer_data = AGffmpeg.av_hwframe_transfer_data,
                av_codec_iterate = AGffmpeg.av_codec_iterate,
                av_codec_is_decoder = AGffmpeg.av_codec_is_decoder,
                avcodec_get_hw_config = AGffmpeg.avcodec_get_hw_config,
                avcodec_alloc_context3 = AGffmpeg.avcodec_alloc_context3,
                avcodec_free_context = AGffmpeg.avcodec_free_context,
                avcodec_parameters_to_context = AGffmpeg.avcodec_parameters_to_context,
                avcodec_open2 = AGffmpeg.avcodec_open2,
                avcodec_receive_frame = AGffmpeg.avcodec_receive_frame,
                avcodec_send_packet = AGffmpeg.avcodec_send_packet,
                avformat_alloc_context = AGffmpeg.avformat_alloc_context,
                avformat_close_input = AGffmpeg.avformat_close_input,
                avformat_find_stream_info = AGffmpeg.avformat_find_stream_info,
                avformat_open_input = AGffmpeg.avformat_open_input,
                av_find_best_stream = AGffmpeg.av_find_best_stream,
                avio_alloc_context = AGffmpeg.avio_alloc_context,
                sws_freeContext = AGffmpeg.sws_freeContext,
                sws_getCachedContext = AGffmpeg.sws_getCachedContext,
                sws_scale = AGffmpeg.sws_scale
            };
        }

        #region Disposal

        ~VideoDecoder()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (isDisposed)
                return;

            isDisposed = true;

            decoderCommands.Clear();

            StopDecoding(true);

            if (formatContext != null && inputOpened)
            {
                fixed (AVFormatContext** ptr = &formatContext)
                    ffmpeg.avformat_close_input(ptr);
            }

            if (codecContext != null)
            {
                fixed (AVCodecContext** ptr = &codecContext)
                    ffmpeg.avcodec_free_context(ptr);
            }

            seekCallback = null;
            readPacketCallback = null;
            managedContextBuffer = null;

            videoStream.Dispose();
            videoStream = null;

            // gets freed by libavformat when closing the input
            contextBuffer = null;

            if (swsContext != null)
                ffmpeg.sws_freeContext(swsContext);

            while (decodedFrames.TryDequeue(out var f))
            {
                ((VideoTexture)f.Texture.TextureGL).FlushUploads();
                f.Texture.Dispose();
            }

            while (availableTextures.TryDequeue(out var t))
                t.Dispose();

            while (hwTransferFrames.TryDequeue(out var hwF))
                ffmpeg.av_frame_free(&hwF.Value);

            while (scalerFrames.TryDequeue(out var sf))
                ffmpeg.av_frame_free(&sf.Value);

            handle.Dispose();
        }

        #endregion

        /// <summary>
        /// Represents the possible states the decoder can be in.
        /// </summary>
        public enum DecoderState
        {
            /// <summary>
            /// The decoder is ready to begin decoding. This is the default state before the decoder starts operations.
            /// </summary>
            Ready = 0,

            /// <summary>
            /// The decoder is currently running and decoding frames.
            /// </summary>
            Running = 1,

            /// <summary>
            /// The decoder has faulted with an exception.
            /// </summary>
            Faulted = 2,

            /// <summary>
            /// The decoder has reached the end of the video data.
            /// </summary>
            EndOfStream = 3,

            /// <summary>
            /// The decoder has been completely stopped and cannot be resumed.
            /// </summary>
            Stopped = 4,
        }

        public readonly struct Frame
        {
            public readonly AVFrame* Value;

            private readonly Action<Frame> returnDelegate;

            public Frame(AVFrame* ptr, Action<Frame> returnDelegate = null)
            {
                Value = ptr;
                this.returnDelegate = returnDelegate;
            }

            public void Return() => returnDelegate(this);
        }
    }
}
