using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MajdataViewX.Managers
{
    /// <summary>Native launcher that starts the external FFmpeg process without System.Diagnostics.Process.</summary>
    internal static class FFmpegLauncher
    {
        private const string DllName = "FFmpegLauncher";

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern IntPtr ffmpeg_decoder_create(
            string cmdline,
            string cwd,
            int frameBytes,
            [Out] byte[] errBuf,
            int errCap);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        internal static extern int ffmpeg_decoder_read_frame(IntPtr decoder, byte[] buf, int bufLen);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        internal static extern int ffmpeg_decoder_poll_exit(IntPtr decoder, out int exitCode);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        private static extern int ffmpeg_decoder_get_error(IntPtr decoder, [Out] byte[] outBuf, int outCap);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        internal static extern void ffmpeg_decoder_stop(IntPtr decoder);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        internal static extern void ffmpeg_decoder_free(IntPtr decoder);

        internal static int ReadFrame(IntPtr decoder, byte[] buf, int bufLen) =>
            ffmpeg_decoder_read_frame(decoder, buf, bufLen);

        internal static int PollExit(IntPtr decoder, out int exitCode) =>
            ffmpeg_decoder_poll_exit(decoder, out exitCode);

        internal static void Stop(IntPtr decoder) => ffmpeg_decoder_stop(decoder);

        internal static void Free(IntPtr decoder) => ffmpeg_decoder_free(decoder);

        internal static IntPtr Create(string cmdline, string cwd, int frameBytes, out string error)
        {
            var errBuf = new byte[1024];
            var handle = ffmpeg_decoder_create(cmdline, cwd, frameBytes, errBuf, errBuf.Length);
            error = handle == IntPtr.Zero ? Decode(errBuf, -1) : string.Empty;
            return handle;
        }

        internal static string GetError(IntPtr decoder)
        {
            if (decoder == IntPtr.Zero)
                return string.Empty;

            var buffer = new byte[1024];
            var count = ffmpeg_decoder_get_error(decoder, buffer, buffer.Length);
            return Decode(buffer, count);
        }

        private static string Decode(byte[] buffer, int count)
        {
            var length = count;
            if (length <= 0)
            {
                length = 0;
                for (var i = 0; i < buffer.Length; i++)
                {
                    if (buffer[i] == 0) break;
                    length++;
                }
            }

            if (length <= 0)
                return string.Empty;

            try
            {
                return Encoding.UTF8.GetString(buffer, 0, length);
            }
            catch
            {
                return Encoding.Default.GetString(buffer, 0, length);
            }
        }
    }
}
