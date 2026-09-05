#pragma once

#include <stdint.h>
#include <wchar.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct FfmpegDecoder FfmpegDecoder;

/*
 * Starts ffmpeg decoding a video to raw RGBA on its stdout.
 *
 * cmdline    : full Windows command line (UTF-16). First token must be the
 *              quoted absolute ffmpeg.exe path; remaining tokens are the fixed
 *              decode arguments (never user-supplied shell text).
 * cwd        : child working directory, UTF-16. Pass the ffmpeg.exe directory
 *              or NULL/empty to inherit the parent working directory.
 * frame_bytes: exact bytes per output frame (width * height * 4).
 * err_buf    : receives a NUL-terminated error message on failure (bytes).
 * err_cap    : capacity of err_buf in bytes.
 *
 * Returns a decoder handle, or NULL on failure.
 */
__declspec(dllexport) FfmpegDecoder* __stdcall ffmpeg_decoder_create(
    const wchar_t* cmdline,
    const wchar_t* cwd,
    int frame_bytes,
    char* err_buf,
    int err_cap);

/*
 * Blocks until buf_len bytes are read (call with buf_len == frame_bytes).
 * Returns 1 when a complete frame was read, 0 on EOF, -1 on error/cancellation.
 */
__declspec(dllexport) int __stdcall ffmpeg_decoder_read_frame(
    FfmpegDecoder* decoder,
    void* buf,
    int buf_len);

/*
 * Returns 1 and stores the exit code when the child has exited, 0 otherwise.
 */
__declspec(dllexport) int __stdcall ffmpeg_decoder_poll_exit(
    FfmpegDecoder* decoder,
    int* exit_code);

/*
 * Copies the trailing stderr text (NUL-terminated) into out_buf.
 * Returns the number of bytes copied, excluding the terminator.
 */
__declspec(dllexport) int __stdcall ffmpeg_decoder_get_error(
    FfmpegDecoder* decoder,
    char* out_buf,
    int out_cap);

/*
 * Unblocks any pending read_frame, terminates the child if still running and
 * closes all handles. Idempotent.
 */
__declspec(dllexport) void __stdcall ffmpeg_decoder_stop(
    FfmpegDecoder* decoder);

/*
 * Releases the decoder handle. NULL-safe and idempotent.
 */
__declspec(dllexport) void __stdcall ffmpeg_decoder_free(
    FfmpegDecoder* decoder);

#ifdef __cplusplus
}
#endif
