#nullable enable

using Cysharp.Threading.Tasks;
using MajdataViewX.Managers;
using MajdataViewX.Types.Enums;
using MajdataViewX.Types.MajWs;
using MemoryPack;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using WebSocketSharp;
using WebSocketSharp.Server;
using static MajdataViewX.Base.MajCtx;
using Debug = UnityEngine.Debug;

namespace MajdataViewX
{
    public class WsServer : MonoBehaviour
    {
        public static readonly ConcurrentQueue<byte[]> MessageQueue = new();
        private WebSocketServer? webSocket;
        private CancellationToken _lifetimeCancellationToken;

        private void Awake()
        {
            _wsServer = this;
        }

        // 这里是游戏及游戏外部的初始化
        void Start()
        {
            QualitySettings.vSyncCount = 1;

            webSocket = new WebSocketServer("ws://127.0.0.1:8083");
            webSocket.AddWebSocketService<MajdataWsService>("/majdata");
            webSocket.Start();
            _lifetimeCancellationToken = this.GetCancellationTokenOnDestroy();
            ProcessQueue(_lifetimeCancellationToken).Forget();
            BroadcastHeartbeat(_lifetimeCancellationToken).Forget();

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        // 补全 Mac 常见的环境变量路径（Homebrew 在 Intel 和 Apple Silicon 的路径不同）
        var currentPath = Environment.GetEnvironmentVariable("PATH");
        var extraPath = "/usr/local/bin:/opt/homebrew/bin:/opt/homebrew/sbin";
        Environment.SetEnvironmentVariable("PATH", $"{currentPath}:{extraPath}");
#endif
        }

        private async UniTaskVoid ProcessQueue(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (MessageQueue.TryDequeue(out var bytes))
                    {
                        while (_playManager == null ||
                               PlayManager.Summary.State == ViewStatus.Busy)
                            await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);

                        await HandleMessageAsync(bytes);
                    }
                    else
                    {
                        await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async UniTaskVoid BroadcastHeartbeat(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await UniTask.Delay(
                        TimeSpan.FromSeconds(1),
                        DelayType.Realtime,
                        PlayerLoopTiming.Update,
                        cancellationToken);
                    Response(MajWsResponseType.Heartbeat, PlayManager.Summary);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async UniTask HandleMessageAsync(byte[] bytes)
        {
            try
            {
                var req = MemoryPackSerializer.Deserialize<MajWsRequest>(bytes);
                switch (req)
                {
                    case MajWsSettingRequest r:
                        _playManager.Setting(r.ViewSetting, r.VolumeSetting);
                        Response(MajWsResponseType.Ok, PlayManager.Summary);
                        Debug.Log("request finished: Setting");
                        break;
                    case MajWsLoadRequest r:
                        await _playManager.LoadAsync(r.TrackPath, r.ImagePath, r.VideoPath);
                        Response(MajWsResponseType.LoadOk, PlayManager.Summary);
                        Debug.Log("request finished: Load");
                        break;
                    case MajWsUpdateRequest r:
                        // 图数据经共享内存传输：FileLength/ChartLength 即 Edit 写入的两段 MemoryPack 字节数
                        await _playManager.UpdateAsync(r.FileLength, r.ChartLength, r.SelectedDifficulty, r.PvOffset);
                        Response(MajWsResponseType.Ok, PlayManager.Summary);
                        Debug.Log("request finished: Update");
                        break;
                    case MajWsPlayRequest r:
                        await _playManager.PlayAsync(
                            r.Mode, r.StartAt, r.Speed, r.MaidataPath ?? string.Empty);
                        if (r.Mode != PlaybackMode.Record)
                            Response(MajWsResponseType.PlayStarted, PlayManager.Summary);
                        Debug.Log("request finished: Play");
                        break;
                    case MajWsPauseRequest:
                        if (_screenRecorder.IsRecording) break;
                        await _playManager.PauseAsync();
                        Response(MajWsResponseType.PlayPaused, PlayManager.Summary);
                        Debug.Log("request finished: Pause");
                        break;
                    case MajWsStopRequest:
                        await _playManager.StopAsync();
                        Response(MajWsResponseType.PlayStopped, PlayManager.Summary);
                        Debug.Log("request finished: Stop");
                        break;
                    case MajWsStateRequest:
                        Response(MajWsResponseType.Ok, PlayManager.Summary);
                        Debug.Log("request finished: State");
                        break;
                    case MajWsResetRequest:
                        await _playManager.ResetAsync();
                        Response(MajWsResponseType.Ok, PlayManager.Summary);
                        Debug.Log("request finished: Reset");
                        break;
                    default:
                        Error("Not Supported");
                        Debug.LogError("request failed: Not Supported");
                        break;
                }
            }
            catch (Exception ex)
            {
                Error(ex);
            }
        }

        // for self stopping without request
        public void SendStopResponse()
        {
            Response(MajWsResponseType.PlayStopped, PlayManager.Summary);
        }

        private void Response(MajWsResponseType type, ViewSummary? summary = null, string? error = null)
        {
            var rsp = new MajWsResponse
            {
                ResponseType = type,
                Summary = summary ?? PlayManager.Summary,
                Error = error
            };
            webSocket?.WebSocketServices["/majdata"].Sessions.
                Broadcast(MemoryPackSerializer.Serialize(rsp));
        }

        public void Error<T>(T exception) where T : Exception
        {
            Response(MajWsResponseType.Error, error: exception.ToString());
        }

        public void Error(string errMsg)
        {
            Response(MajWsResponseType.Error, error: errMsg);
        }

        void OnDestroy()
        {
            if (webSocket is not null)
            {
                webSocket.RemoveWebSocketService("/majdata");
                webSocket.Stop();
            }
        }
    }

    public class MajdataWsService : WebSocketBehavior
    {
        protected override void OnMessage(MessageEventArgs e)
        {
            // 二进制帧为 MemoryPack 消息；旧文本帧按 UTF-8 编码后同样入队（反序列化会失败并回 Error）
            var data = e.IsBinary ? e.RawData : Encoding.UTF8.GetBytes(e.Data);
            if (data.Length == 0)
                return;

            WsServer.MessageQueue.Enqueue(data);
        }
    }
}
