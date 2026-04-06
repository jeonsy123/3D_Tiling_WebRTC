using UnityEngine;
using Unity.WebRTC;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;

// ==================================================================================
// [전역 Enum 정의]
// 송신측에도 이 정의가 반드시 필요합니다.
// ==================================================================================
public enum TileSize { _64, _128, _256, _512, _1024 }

public class TileSender : MonoBehaviour
{
    [Header("Signaling (Localhost)")]
    public string signalingUrl = "ws://127.0.0.1:3001?room=demo";

    [Header("XML / Files")]
    [Tooltip("RX(수신측)와 동일해야 함")]
    public TileSize tileSize = TileSize._128;

    [Header("Performance")]
    public int batchSize = 10;

    [Range(0f, 0.1f)]
    public float batchDelay = 0.01f;

    [Header("Chunk & Backpressure")]
    public int chunkSize = 1200;
    public ulong bufferedAmountLimit = 256 * 1024; // 256KB
    public int progressLogEveryNChunks = 64;

    [Header("Debug")]
    public bool verbose = true;

    // --- WebRTC ---
    RTCPeerConnection pc;
    RTCDataChannel ctrlDC;
    RTCDataChannel tilesDC;
    bool isCaller = true;

    // --- signaling ---
    ClientWebSocket ws;
    readonly ConcurrentQueue<string> rxSignal = new();
    CancellationTokenSource wsCts;

    // --- msg types ---
    [Serializable] class Sig { public string type, sdp, candidate, sdpMid; public int sdpMLineIndex; }
    [Serializable] class CtrlHello { public string type = "hello"; public string role = "sender"; }
    [Serializable] class CtrlPing { public string type = "ping"; public long t0; }
    [Serializable] class CtrlPong { public string type = "pong"; public long t0; public long t1; }
    [Serializable] class CtrlTypeOnly { public string type; }
    [Serializable] class RequestTileMsg { public string type = "requestTile"; public string relativePath; public int priority; }
    [Serializable] class FileStart { public string type = "file_start"; public string name; public int bytes; }
    [Serializable] class FileEnd { public string type = "file_end"; }

    // --- 전송 큐/상태 ---
    readonly ConcurrentQueue<string> sendQueue = new();
    readonly ConcurrentDictionary<string, byte> inQueue = new();
    bool sending = false;

    int latestRequestedFrameId = -1;
    ulong bytesSentTotal = 0;
    int batchCounter = 0;

    IEnumerator Start()
    {
        yield return new WaitForSeconds(1.0f);

        var cfg = default(RTCConfiguration);
        cfg.iceServers = new RTCIceServer[] { };
        pc = new RTCPeerConnection(ref cfg);

        // CTRL
        ctrlDC = pc.CreateDataChannel("ctrl", new RTCDataChannelInit { ordered = true });
        ctrlDC.OnOpen += HandleCtrlDCOpen;
        ctrlDC.OnMessage += OnCtrlMessage;

        // TILES
        tilesDC = pc.CreateDataChannel("tiles", new RTCDataChannelInit { ordered = true });
        tilesDC.OnOpen += HandleTilesDCOpen;

        pc.OnIceCandidate = cand =>
        {
            if (!string.IsNullOrEmpty(cand.Candidate))
            {
                SendSignal(new Sig
                {
                    type = "candidate",
                    candidate = cand.Candidate,
                    sdpMid = cand.SdpMid,
                    sdpMLineIndex = cand.SdpMLineIndex ?? 0
                });
            }
        };

        var connectTask = ConnectSignaling();
        while (!connectTask.IsCompleted) yield return null;

        StartCoroutine(ProcessIncomingSignals());
        StartCoroutine(KeepAliveLoop());

        if (isCaller)
        {
            var offerOp = pc.CreateOffer();
            yield return offerOp;
            var setLocal = pc.SetLocalDescription();
            while (!setLocal.IsDone) yield return null;
            SendSignal(new Sig { type = "offer", sdp = pc.LocalDescription.sdp });
        }
    }

    IEnumerator KeepAliveLoop()
    {
        while (ws != null && ws.State == WebSocketState.Open)
        {
            yield return new WaitForSeconds(3.0f);
        }
    }

    void HandleCtrlDCOpen()
    {
        Debug.Log("[TX] ctrl open");
        SendCtrl(new CtrlHello());
        StartCoroutine(SendPingDelayed(1f));
    }

    void HandleTilesDCOpen()
    {
        Debug.Log("[TX] tiles open");
        StartCoroutine(SendInitialXmlWithFraming());
        TryStartSendLoop();
    }

    void OnCtrlMessage(byte[] data)
    {
        var json = Encoding.UTF8.GetString(data);
        if (verbose) Debug.Log($"[TX][RX] {json}");
        CtrlTypeOnly tag = null; try { tag = JsonUtility.FromJson<CtrlTypeOnly>(json); } catch { }
        if (tag == null || string.IsNullOrEmpty(tag.type)) return;

        if (tag.type == "requestTile")
        {
            var req = JsonUtility.FromJson<RequestTileMsg>(json);
            if (string.IsNullOrEmpty(req.relativePath)) return;

            int fid = ExtractFrameId(req.relativePath);
            if (fid >= 0 && fid < latestRequestedFrameId) return;
            if (fid > latestRequestedFrameId) latestRequestedFrameId = fid;

            if (sendQueue.Count > 2000)
            {
                while (sendQueue.TryDequeue(out _)) ;
                inQueue.Clear();
                Debug.LogWarning("[TX] Queue Overflow! Cleared.");
            }

            if (inQueue.TryAdd(req.relativePath, 1))
            {
                sendQueue.Enqueue(req.relativePath);
                TryStartSendLoop();
            }
        }
    }

    void SendCtrl(object obj)
    {
        if (ctrlDC == null || ctrlDC.ReadyState != RTCDataChannelState.Open) return;
        var json = JsonUtility.ToJson(obj);
        ctrlDC.Send(Encoding.UTF8.GetBytes(json));
    }

    IEnumerator SendPingDelayed(float delay)
    {
        yield return new WaitForSeconds(delay);
        SendCtrl(new CtrlPing { t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    IEnumerator SendInitialXmlWithFraming()
    {
        while (tilesDC == null || tilesDC.ReadyState != RTCDataChannelState.Open) yield return null;
        string sizeStr = tileSize.ToString().Substring(1);
        string name = $"tile_metadata_{sizeStr}.xml";
        string xmlPath = Path.Combine(Application.streamingAssetsPath, name);

        if (!File.Exists(xmlPath))
        {
            Debug.LogError($"[TX] XML Not Found: {xmlPath}");
            yield break;
        }

        yield return StartCoroutine(SendTileFileWithFraming(xmlPath, null));
    }

    void TryStartSendLoop()
    {
        if (sending || sendQueue.IsEmpty) return;
        StartCoroutine(SendLoop());
    }

    IEnumerator SendLoop()
    {
        sending = true;
        while (tilesDC == null || tilesDC.ReadyState != RTCDataChannelState.Open) yield return null;

        while (!sendQueue.IsEmpty)
        {
            while (tilesDC.BufferedAmount > bufferedAmountLimit) yield return null;

            var batchList = new List<string>();
            while (batchList.Count < batchSize && sendQueue.TryDequeue(out var rel))
            {
                int fid = ExtractFrameId(rel);
                if (fid >= 0 && fid < latestRequestedFrameId)
                {
                    inQueue.TryRemove(rel, out _);
                    continue;
                }
                batchList.Add(rel);
            }

            if (batchList.Count > 0)
            {
                byte[] batchPayload = PackBatch(batchList);
                string batchName = $"__BATCH__/batch_frame{latestRequestedFrameId}_{batchCounter++}.batch";

                if (verbose) Debug.Log($"[TX] Sending Batch: {batchList.Count} items (Size: {batchPayload.Length})");
                yield return StartCoroutine(SendTileFileWithFraming(batchName, batchPayload));

                if (batchDelay > 0) yield return new WaitForSeconds(batchDelay);
            }
            else
            {
                yield return null;
            }
        }
        sending = false;
    }

    // ==================================================================================
    // [핵심 로직] 파일이 없어도 0바이트 데이터로 응답하여 수신측 대기 해제
    // ==================================================================================
    byte[] PackBatch(List<string> batchList)
    {
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms, Encoding.UTF8))
        {
            var validFiles = new List<(string relNorm, byte[] data)>();

            foreach (var rel in batchList)
            {
                string relNorm = NormalizeRel(rel);
                string fullPath = Path.Combine(Application.streamingAssetsPath, relNorm.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(fullPath))
                {
                    byte[] fileData = File.ReadAllBytes(fullPath);
                    validFiles.Add((relNorm, fileData));
                }
                else
                {
                    // [중요] 파일이 없으면 빈 바이트 배열(0 byte)을 보내서 수신측이 "파일 없음"으로 처리하게 함
                    validFiles.Add((relNorm, new byte[0]));
                }
            }

            writer.Write(validFiles.Count);
            foreach (var file in validFiles)
            {
                writer.Write(file.relNorm);
                writer.Write(file.data.Length);
                writer.Write(file.data);
                inQueue.TryRemove(file.relNorm, out _);
            }
            return ms.ToArray();
        }
    }

    // [단일 파일 전송 시에도 0바이트 처리 적용]
    IEnumerator SendTileFileWithFraming(string relativePath, byte[] customPayload = null)
    {
        if (string.IsNullOrEmpty(relativePath)) yield break;

        byte[] bytes;
        if (customPayload != null)
        {
            bytes = customPayload;
        }
        else
        {
            string rel = NormalizeRel(relativePath);
            string fullPath = Path.Combine(Application.streamingAssetsPath, rel.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(fullPath))
            {
                bytes = File.ReadAllBytes(fullPath);
            }
            else
            {
                // 파일이 없으면 0바이트 할당 -> 수신측에서 즉시 종료 처리됨
                bytes = new byte[0];
            }
        }

        if (tilesDC == null || tilesDC.ReadyState != RTCDataChannelState.Open) yield break;

        string nameForCtrl = NormalizeRel(relativePath);

        SendCtrl(new FileStart { name = nameForCtrl, bytes = bytes.Length });
        yield return StartCoroutine(SendBytesOverTiles(bytes));
        SendCtrl(new FileEnd { });
    }

    IEnumerator SendBytesOverTiles(byte[] bytes)
    {
        if (tilesDC == null || tilesDC.ReadyState != RTCDataChannelState.Open) yield break;

        int total = bytes.Length;

        // 0바이트면 전송할 내용 없음
        if (total == 0) yield break;

        int chunks = (total + chunkSize - 1) / chunkSize;

        for (int seq = 0; seq < chunks; seq++)
        {
            if (tilesDC.ReadyState != RTCDataChannelState.Open) yield break;

            int offset = seq * chunkSize;
            int size = Math.Min(chunkSize, total - offset);
            var chunk = new byte[size];
            Buffer.BlockCopy(bytes, offset, chunk, 0, size);

            tilesDC.Send(chunk);
            bytesSentTotal += (ulong)size;

            if (tilesDC.BufferedAmount > 64 * 1024) yield return null;
            while (tilesDC.BufferedAmount > bufferedAmountLimit) yield return null;
        }
    }

    // ---- 유틸 ----
    static string NormalizeRel(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        p = p.Replace('\\', '/');
        while (p.Length > 0 && p[0] == '/') p = p[1..];
        return p;
    }

    int ExtractFrameId(string rel)
    {
        if (string.IsNullOrEmpty(rel)) return -1;
        string s = rel.ToLowerInvariant();
        int i = s.IndexOf("frame_");
        if (i >= 0)
        {
            i += "frame_".Length;
            int j = i;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            if (int.TryParse(s.Substring(i, j - i), out int id)) return id;
        }
        return -1;
    }

    // ---- Signaling ----
    async Task ConnectSignaling()
    {
        wsCts = new CancellationTokenSource();
        ws = new ClientWebSocket();
        try { await ws.ConnectAsync(new Uri(signalingUrl), wsCts.Token); _ = Task.Run(ReceiveLoop); } catch { }
    }

    async Task ReceiveLoop()
    {
        var buf = new ArraySegment<byte>(new byte[64 * 1024]);
        while (!wsCts.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, wsCts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf.Array, buf.Offset, r.Count);
                } while (!r.EndOfMessage);
                rxSignal.Enqueue(Encoding.UTF8.GetString(ms.ToArray()));
            }
            catch { break; }
        }
    }

    IEnumerator ProcessIncomingSignals()
    {
        while (true)
        {
            if (rxSignal.TryDequeue(out var json))
            {
                Sig m = null; try { m = JsonUtility.FromJson<Sig>(json); } catch { }
                if (m != null)
                {
                    if (m.type == "offer")
                    {
                        var desc = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = m.sdp };
                        pc.SetRemoteDescription(ref desc);
                        yield return pc.CreateAnswer();
                        yield return pc.SetLocalDescription();
                        SendSignal(new Sig { type = "answer", sdp = pc.LocalDescription.sdp });
                    }
                    else if (m.type == "answer")
                    {
                        var desc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = m.sdp };
                        pc.SetRemoteDescription(ref desc);
                    }
                    else if (m.type == "candidate")
                    {
                        var init = new RTCIceCandidateInit { candidate = m.candidate, sdpMid = m.sdpMid, sdpMLineIndex = m.sdpMLineIndex };
                        pc.AddIceCandidate(new RTCIceCandidate(init));
                    }
                }
            }
            yield return null;
        }
    }

    void SendSignal(Sig s)
    {
        if (ws != null && ws.State == WebSocketState.Open)
            _ = ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonUtility.ToJson(s))), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    void OnDestroy()
    {
        wsCts?.Cancel(); ws?.Dispose(); ctrlDC?.Close(); tilesDC?.Close(); pc?.Close();
    }
}