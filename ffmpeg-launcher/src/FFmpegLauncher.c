#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include "FFmpegLauncher.h"

#include <process.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define ERROR_TAIL_CAPACITY 1024

struct FfmpegDecoder
{
    HANDLE process;
    HANDLE process_thread;
    HANDLE stdout_read;
    HANDLE stdout_write;
    HANDLE stderr_read;
    HANDLE stderr_write;
    HANDLE error_thread;

    CRITICAL_SECTION error_lock;
    char error_tail[ERROR_TAIL_CAPACITY];
    int error_len;

    LONG stop_flag;
    int frame_bytes;
    int read_error;
};

static void set_error(FfmpegDecoder* decoder, const char* text)
{
    size_t n;

    if (decoder == NULL || text == NULL)
        return;

    n = strlen(text);
    EnterCriticalSection(&decoder->error_lock);
    if (n >= ERROR_TAIL_CAPACITY)
    {
        memcpy(decoder->error_tail,
               text + (n - (ERROR_TAIL_CAPACITY - 1)),
               ERROR_TAIL_CAPACITY - 1);
        decoder->error_len = ERROR_TAIL_CAPACITY - 1;
    }
    else
    {
        size_t free_space = ERROR_TAIL_CAPACITY - 1 - decoder->error_len;
        if (n > free_space)
        {
            size_t overflow = n - free_space;
            memmove(decoder->error_tail,
                    decoder->error_tail + overflow,
                    decoder->error_len - overflow);
            decoder->error_len -= overflow;
        }
        memcpy(decoder->error_tail + decoder->error_len, text, n);
        decoder->error_len += (int)n;
    }
    decoder->error_tail[decoder->error_len] = '\0';
    LeaveCriticalSection(&decoder->error_lock);
}

static unsigned int __stdcall error_reader(void* parameter)
{
    FfmpegDecoder* decoder = (FfmpegDecoder*)parameter;
    char buffer[512];

    for (;;)
    {
        DWORD bytes_read = 0;
        if (!ReadFile(decoder->stderr_read, buffer, sizeof(buffer), &bytes_read, NULL))
            break;
        if (bytes_read == 0)
            break;

        EnterCriticalSection(&decoder->error_lock);
        {
            size_t keep = bytes_read > ERROR_TAIL_CAPACITY - 1
                ? ERROR_TAIL_CAPACITY - 1
                : bytes_read;
            const char* source = buffer + (bytes_read - keep);
            memcpy(decoder->error_tail, source, keep);
            decoder->error_len = (int)keep;
            decoder->error_tail[decoder->error_len] = '\0';
        }
        LeaveCriticalSection(&decoder->error_lock);
    }

    return 0;
}

static int is_eof_error(void)
{
    DWORD error = GetLastError();
    return error == ERROR_BROKEN_PIPE || error == ERROR_PIPE_NOT_CONNECTED ||
           error == ERROR_OPERATION_ABORTED;
}

FfmpegDecoder* __stdcall ffmpeg_decoder_create(
    const wchar_t* cmdline,
    const wchar_t* cwd,
    int frame_bytes,
    char* err_buf,
    int err_cap)
{
    FfmpegDecoder* decoder;
    SECURITY_ATTRIBUTES security;
    PROCESS_INFORMATION process_info;
    STARTUPINFOW startup_info;
    wchar_t* cmdline_copy;
    size_t cmdline_len;

    if (err_buf != NULL && err_cap > 0)
        err_buf[0] = '\0';

    if (frame_bytes <= 0)
    {
        if (err_buf != NULL && err_cap > 0)
            _snprintf_s(err_buf, err_cap, _TRUNCATE, "invalid frame size");
        return NULL;
    }

    if (cmdline == NULL || cmdline[0] == L'\0')
    {
        if (err_buf != NULL && err_cap > 0)
            _snprintf_s(err_buf, err_cap, _TRUNCATE, "empty command line");
        return NULL;
    }

    /* CreateProcessW may modify the command line buffer in place, so work on a copy. */
    cmdline_len = wcslen(cmdline) + 1;
    cmdline_copy = (wchar_t*)malloc(cmdline_len * sizeof(wchar_t));
    if (cmdline_copy == NULL)
    {
        if (err_buf != NULL && err_cap > 0)
            _snprintf_s(err_buf, err_cap, _TRUNCATE, "out of memory");
        return NULL;
    }
    memcpy(cmdline_copy, cmdline, cmdline_len * sizeof(wchar_t));

    decoder = (FfmpegDecoder*)calloc(1, sizeof(FfmpegDecoder));
    if (decoder == NULL)
    {
        free(cmdline_copy);
        if (err_buf != NULL && err_cap > 0)
            _snprintf_s(err_buf, err_cap, _TRUNCATE, "out of memory");
        return NULL;
    }

    decoder->frame_bytes = frame_bytes;
    InitializeCriticalSection(&decoder->error_lock);

    security.nLength = sizeof(security);
    security.lpSecurityDescriptor = NULL;
    security.bInheritHandle = TRUE;

    if (!CreatePipe(&decoder->stdout_read, &decoder->stdout_write, &security, 0))
    {
        set_error(decoder, "CreatePipe(stdout) failed");
        goto fail;
    }
    if (!CreatePipe(&decoder->stderr_read, &decoder->stderr_write, &security, 0))
    {
        set_error(decoder, "CreatePipe(stderr) failed");
        goto fail;
    }

    SetHandleInformation(decoder->stdout_read, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(decoder->stderr_read, HANDLE_FLAG_INHERIT, 0);

    memset(&startup_info, 0, sizeof(startup_info));
    startup_info.cb = sizeof(startup_info);
    startup_info.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup_info.hStdOutput = decoder->stdout_write;
    startup_info.hStdError = decoder->stderr_write;
    startup_info.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    startup_info.wShowWindow = SW_HIDE;

    if (!CreateProcessW(
            NULL,
            cmdline_copy,
            NULL,
            NULL,
            TRUE,
            CREATE_NO_WINDOW,
            NULL,
            (LPCWSTR)cwd,
            &startup_info,
            &process_info))
    {
        DWORD error = GetLastError();
        char message[256];
        _snprintf_s(message, sizeof(message), _TRUNCATE,
                    "CreateProcessW failed: %lu", (unsigned long)error);
        set_error(decoder, message);
        goto fail;
    }

    free(cmdline_copy);
    cmdline_copy = NULL;

    decoder->process = process_info.hProcess;
    decoder->process_thread = process_info.hThread;

    decoder->error_thread = (HANDLE)_beginthreadex(
        NULL, 0, error_reader, decoder, 0, NULL);

    CloseHandle(decoder->stdout_write);
    decoder->stdout_write = NULL;
    CloseHandle(decoder->stderr_write);
    decoder->stderr_write = NULL;

    return decoder;

fail:
    free(cmdline_copy);
    if (decoder->stdout_read != NULL) CloseHandle(decoder->stdout_read);
    if (decoder->stdout_write != NULL) CloseHandle(decoder->stdout_write);
    if (decoder->stderr_read != NULL) CloseHandle(decoder->stderr_read);
    if (decoder->stderr_write != NULL) CloseHandle(decoder->stderr_write);
    if (err_buf != NULL && err_cap > 0)
    {
        EnterCriticalSection(&decoder->error_lock);
        _snprintf_s(err_buf, err_cap, _TRUNCATE, "%s", decoder->error_tail);
        LeaveCriticalSection(&decoder->error_lock);
    }
    DeleteCriticalSection(&decoder->error_lock);
    free(decoder);
    return NULL;
}

int __stdcall ffmpeg_decoder_read_frame(
    FfmpegDecoder* decoder,
    void* buf,
    int buf_len)
{
    int offset = 0;
    char* target = (char*)buf;

    if (decoder == NULL || buf == NULL || buf_len <= 0)
        return -1;

    while (offset < buf_len)
    {
        DWORD bytes_read = 0;

        if (InterlockedCompareExchange(&decoder->stop_flag, 0, 0) != 0)
            return -1;

        if (!ReadFile(decoder->stdout_read,
                      target + offset,
                      (DWORD)(buf_len - offset),
                      &bytes_read,
                      NULL))
        {
            if (is_eof_error())
                return offset > 0 ? -1 : 0;
            decoder->read_error = 1;
            return -1;
        }

        if (bytes_read == 0)
            return offset > 0 ? -1 : 0;

        offset += (int)bytes_read;
    }

    return 1;
}

int __stdcall ffmpeg_decoder_poll_exit(
    FfmpegDecoder* decoder,
    int* exit_code)
{
    DWORD code = 0;

    if (decoder == NULL)
        return 0;

    if (WaitForSingleObject(decoder->process, 0) != WAIT_OBJECT_0)
        return 0;

    if (exit_code != NULL && GetExitCodeProcess(decoder->process, &code))
        *exit_code = (int)code;

    return 1;
}

int __stdcall ffmpeg_decoder_get_error(
    FfmpegDecoder* decoder,
    char* out_buf,
    int out_cap)
{
    int copied = 0;

    if (decoder == NULL || out_buf == NULL || out_cap <= 0)
        return 0;

    EnterCriticalSection(&decoder->error_lock);
    if (decoder->error_len > 0)
    {
        copied = decoder->error_len < out_cap - 1 ? decoder->error_len : out_cap - 1;
        memcpy(out_buf, decoder->error_tail, copied);
    }
    out_buf[copied] = '\0';
    LeaveCriticalSection(&decoder->error_lock);

    return copied;
}

void __stdcall ffmpeg_decoder_stop(FfmpegDecoder* decoder)
{
    if (decoder == NULL)
        return;

    InterlockedExchange(&decoder->stop_flag, 1);

    if (decoder->process != NULL)
    {
        if (WaitForSingleObject(decoder->process, 0) != WAIT_OBJECT_0)
            TerminateProcess(decoder->process, 1);
    }
}

void __stdcall ffmpeg_decoder_free(FfmpegDecoder* decoder)
{
    if (decoder == NULL)
        return;

    ffmpeg_decoder_stop(decoder);

    if (decoder->stdout_read != NULL)
    {
        /* Closing from here is safe only after the reader thread has returned;
         * the caller must ensure no read_frame is still pending. */
        CloseHandle(decoder->stdout_read);
        decoder->stdout_read = NULL;
    }
    if (decoder->stderr_read != NULL)
    {
        CloseHandle(decoder->stderr_read);
        decoder->stderr_read = NULL;
    }
    if (decoder->error_thread != NULL)
    {
        WaitForSingleObject(decoder->error_thread, 1000);
        CloseHandle(decoder->error_thread);
        decoder->error_thread = NULL;
    }
    if (decoder->process_thread != NULL)
    {
        CloseHandle(decoder->process_thread);
        decoder->process_thread = NULL;
    }
    if (decoder->process != NULL)
    {
        CloseHandle(decoder->process);
        decoder->process = NULL;
    }

    DeleteCriticalSection(&decoder->error_lock);
    free(decoder);
}
