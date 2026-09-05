#nullable enable

using Cysharp.Threading.Tasks;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MajdataViewX.Managers
{
    internal sealed class ExternalPvDecoder : IDisposable
    {
        public const int QueueCapacity = 32;

        internal sealed class Frame
        {
            internal Frame(long index, byte[] data)
            {
                Index = index;
                Data = data;
            }

            internal long Index { get; }
            internal byte[] Data { get; }
        }

        private readonly string _ffmpegPath;
        private readonly string _inputPath;
        private readonly int _fps;
        private readonly int _width;
        private readonly int _height;
        private readonly int _frameBytes;
        private readonly BlockingCollection<byte[]> _freeBuffers;
        private readonly BlockingCollection<Frame> _frames;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly object _stateGate = new();
        private IntPtr _handle;
        private Task? _readerTask;
        private Frame? _lookahead;
        private Exception? _failure;
        private bool _completed;
        private bool _disposed;

        internal ExternalPvDecoder(
            string ffmpegPath,
            string inputPath,
            int fps,
            int width,
            int height)
        {
            if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            _ffmpegPath = ffmpegPath;
            _inputPath = inputPath;
            _fps = fps;
            _width = width;
            _height = height;
            _frameBytes = checked(width * height * 4);
            _freeBuffers = new BlockingCollection<byte[]>(QueueCapacity);
            _frames = new BlockingCollection<Frame>(QueueCapacity);

            for (var i = 0; i < QueueCapacity; i++)
                _freeBuffers.Add(new byte[_frameBytes]);
        }

        internal bool HasFailed
        {
            get
            {
                lock (_stateGate)
                    return _failure != null;
            }
        }

        internal bool IsCompleted
        {
            get
            {
                lock (_stateGate)
                    return _completed;
            }
        }

        internal bool IsStopped
        {
            get
            {
                lock (_stateGate)
                    return _disposed;
            }
        }

        internal string FailureMessage
        {
            get
            {
                lock (_stateGate)
                    return _failure?.Message ?? string.Empty;
            }
        }

        internal bool TryStart(out string error)
        {
            error = string.Empty;
            try
            {
                var cmdline = QuoteWindowsArgument(_ffmpegPath) + " " + BuildArguments();
                var cwd = Path.GetDirectoryName(_ffmpegPath) ?? string.Empty;
                _handle = FFmpegLauncher.Create(cmdline, cwd, _frameBytes, out var createError);
                if (_handle == IntPtr.Zero)
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(createError) ? "FFmpegLauncher could not start ffmpeg." : createError);

                _readerTask = Task.Run(ReadFrames);
                return true;
            }
            catch (Exception ex)
            {
                SetFailure(ex);
                error = FailureMessage;
                return false;
            }
        }

        internal async UniTask<Frame?> ReadFrameAtOrBeforeAsync(
            long targetIndex,
            bool startup,
            CancellationToken cancellationToken)
        {
            var selected = default(Frame);
            var deadline = startup
                ? Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5
                : long.MaxValue;

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsStopped)
                        throw new OperationCanceledException();

                    var next = TakeNextFrame();
                    if (next != null)
                    {
                        if (next.Index <= targetIndex)
                        {
                            if (selected != null)
                                ReleaseFrame(selected);
                            selected = next;
                            if (next.Index == targetIndex)
                                return selected;
                            continue;
                        }

                        _lookahead = next;
                        return selected;
                    }

                    if (HasFailed)
                        throw new InvalidOperationException(
                            $"External PV decoding failed: {FailureMessage}");

                    if (IsCompleted)
                        return selected;

                    if (Stopwatch.GetTimestamp() >= deadline)
                        throw new TimeoutException("External PV decoder did not produce the requested frame.");

                    await UniTask.Yield();
                }
            }
            catch
            {
                if (selected != null)
                    ReleaseFrame(selected);
                throw;
            }
        }

        internal void ReleaseFrame(Frame frame)
        {
            if (!_freeBuffers.IsAddingCompleted)
                _freeBuffers.TryAdd(frame.Data);
        }

        private Frame? TakeNextFrame()
        {
            if (_lookahead != null)
            {
                var frame = _lookahead;
                _lookahead = null;
                return frame;
            }

            return _frames.TryTake(out var next) ? next : null;
        }

        private void ReadFrames()
        {
            long frameIndex = 0;
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var buffer = _freeBuffers.Take(_cancellation.Token);
                    var result = FFmpegLauncher.ReadFrame(_handle, buffer, _frameBytes);
                    if (result == 0)
                    {
                        _freeBuffers.TryAdd(buffer);
                        break; // EOF
                    }
                    if (result < 0)
                    {
                        _freeBuffers.TryAdd(buffer);
                        if (!_cancellation.IsCancellationRequested)
                            SetFailure(new InvalidOperationException(
                                $"FFmpeg decode failed: {FFmpegLauncher.GetError(_handle)}"));
                        break;
                    }

                    _frames.Add(new Frame(frameIndex++, buffer), _cancellation.Token);
                }

                if (!_cancellation.IsCancellationRequested)
                    WaitForExit();
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                SetFailure(ex);
            }
            finally
            {
                lock (_stateGate)
                    _completed = true;
                _frames.CompleteAdding();
            }
        }

        private void WaitForExit()
        {
            var spin = 0;
            int exitCode = 0;
            while (FFmpegLauncher.PollExit(_handle, out exitCode) == 0)
            {
                if (spin++ > 5000)
                {
                    SetFailure(new TimeoutException("ffmpeg did not exit after EOF."));
                    return;
                }
                Thread.Sleep(1);
            }

            if (exitCode != 0)
                SetFailure(new InvalidOperationException(
                    $"ffmpeg exited with code {exitCode}: {FFmpegLauncher.GetError(_handle)}"));
        }

        private string BuildArguments()
        {
            // 保持比例自动 resize，剩余填充黑边，输出 RGBA32
            // 用 gte(iw*H, ih*W) 判断水平还是垂直填满
            var scaleExpr = $"if(gte(iw*{_height},ih*{_width}),{_width},-1):if(gte(iw*{_height},ih*{_width}),-1,{_height})";
            scaleExpr = scaleExpr.Replace(",", "\\,");
            var pad = $"{_width}:{_height}:(ow-iw)/2:(oh-ih)/2:black";
            var filter = $"fps={_fps},scale={scaleExpr},pad={pad},vflip,format=rgba";
            var arguments = new[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-nostdin",
                "-vsync", "0",
                "-i", _inputPath,
                "-map", "0:v:0",
                "-an",
                "-sn",
                "-dn",
                "-vf", filter,
                "-f", "rawvideo",
                "-pix_fmt", "rgba",
                "pipe:1",
            };

            var builder = new StringBuilder();
            foreach (var argument in arguments)
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(QuoteWindowsArgument(argument));
            }

            return builder.ToString();
        }

        private static string QuoteWindowsArgument(string value)
        {
            if (value.Length == 0) return "\"\"";

            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                builder.Append('\\', backslashes);
                builder.Append(character);
                backslashes = 0;
            }

            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }

        private void SetFailure(Exception exception)
        {
            lock (_stateGate)
            {
                if (_failure == null)
                    _failure = exception;
            }
        }

        public void Dispose()
        {
            lock (_stateGate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            _cancellation.Cancel();
            if (_handle != IntPtr.Zero)
                FFmpegLauncher.Stop(_handle);

            try { _readerTask?.Wait(1000); } catch { }

            while (_frames.TryTake(out var frame))
                ReleaseFrame(frame);
            if (_lookahead != null)
            {
                ReleaseFrame(_lookahead);
                _lookahead = null;
            }

            try { _frames.CompleteAdding(); } catch { }
            try { _freeBuffers.CompleteAdding(); } catch { }

            if (_handle != IntPtr.Zero)
            {
                FFmpegLauncher.Free(_handle);
                _handle = IntPtr.Zero;
            }
            _cancellation.Dispose();
        }
    }
}
