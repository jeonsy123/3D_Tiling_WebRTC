using UnityEngine;
using Unity.WebRTC;
using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Draco;
using System.Linq;
using System.Globalization;
using System.Diagnostics;
using Debug = UnityEngine.Debug;

public enum TileSize { _64, _128, _256, _512, _1024 }
public enum OutsideFovMode { Skip, RequestLow }

public class WebRTCDracoFovPlayer : MonoBehaviour
{
    [Header("Data Logger")]
    public DataLogger dataLogger;

    [Header("Signaling (Localhost)")]
    public string signalingUrl = "ws://127.0.0.1:3001?room=demo";

    [Header("XML / Files")]
    public TileSize tileSize = TileSize._128;

    [Header("FOV Policy")]
    public OutsideFovMode outsideFovMode = OutsideFovMode.Skip;

    [Header("Playback Settings")]
    public float targetDataFPS = 2.0f; // 목표 FPS
    public int targetFrameCount = 300;

    [Header("Performance Tuning")]
    public int maxMeshUploadsPerFrame = 50;

    [Header("Scene Settings")]
    public Transform objectParent;
    public Material pointMaterial;
    public Material lowPointMaterial;

    [Header("Camera & FOV")]
    public Camera mainCamera;
    public float horizontalCutDeg = 110f;
    public float verticalCutDeg = 96f;

    [Header("Camera Movement")]
    public float pitchAmplitude = 30f;


    private RTCPeerConnection pc;
    private RTCDataChannel ctrlDC, tilesDC;
    private ClientWebSocket ws;
    private readonly ConcurrentQueue<string> rxSignal = new();
    private CancellationTokenSource wsCts;

    //수신 데이터 관리
    private readonly ConcurrentDictionary<string, byte[]> receivedFiles = new ConcurrentDictionary<string, byte[]>();
    private readonly ConcurrentDictionary<string, Mesh> meshCache = new ConcurrentDictionary<string, Mesh>();

    //지연시간 측정
    private Dictionary<string, float> requestStartTimes = new Dictionary<string, float>();
    private List<float> highTileLatencies = new List<float>();

    // 메타데이터
    private class TileMeta { public int id; public Vector3 pos; public string highRel; public string lowRel; }
    private class FrameMeta { public int id; public List<TileMeta> tiles = new(); }
    private readonly List<FrameMeta> frames = new();

    //상태 관리
    private bool xmlReady = false;
    private DracoMeshLoader dracoLoader;
    private readonly SemaphoreSlim dracoSemaphore = new SemaphoreSlim(4, 4);

    private List<GameObject> objectPool = new List<GameObject>();
    private int poolIndex = 0;
    private bool isTrackingStarted = false;
    private int renderingFrameCount = 0;
    private int dataFramesProcessed = 0;
    private float elapsedTime = 0f;
    private ulong totalBytesTracked = 0;

    //파일 조립용
    private MemoryStream activeFileStream;
    private string activeFileName;

    [Serializable] private class Sig { public string type, sdp, candidate, sdpMid; public int sdpMLineIndex; }
    [Serializable] private class CtrlType { public string type; }
    [Serializable] private class FileStart { public string type; public string name; public int bytes; }
    [Serializable] private class RequestTileMsg { public string type = "requestTile"; public string relativePath; public int priority = 0; }

    void Start()
    {
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;

        if (dataLogger != null) dataLogger.StartLogging();

        StartCoroutine(StartupTimeout(30f));
        if (mainCamera == null) mainCamera = Camera.main;
        if (objectParent == null) { var go = new GameObject("TileGroup"); objectParent = go.transform; }

        dracoLoader = new DracoMeshLoader();

        var cfg = new RTCConfiguration { iceServers = new RTCIceServer[] { } };
        pc = new RTCPeerConnection(ref cfg);

        pc.OnDataChannel = ch => {
            if (ch.Label == "ctrl") { ctrlDC = ch; ctrlDC.OnMessage = OnCtrlMessage; }
            else if (ch.Label == "tiles") { tilesDC = ch; tilesDC.OnMessage = OnTilesMessage; }
        };

        pc.OnIceCandidate = cand => {
            if (!string.IsNullOrEmpty(cand.Candidate))
                SendSignal(new Sig { type = "candidate", candidate = cand.Candidate, sdpMid = cand.SdpMid, sdpMLineIndex = cand.SdpMLineIndex ?? 0 });
        };

        StartCoroutine(Bootstrap());
    }

    void Update()
    {
        if (isTrackingStarted)
        {
            renderingFrameCount++;
            elapsedTime += Time.unscaledDeltaTime;
        }
    }

    void OnDestroy()
    {
        try { wsCts?.Cancel(); ws?.Dispose(); } catch { }
        try { pc?.Close(); } catch { }
        try { dracoSemaphore?.Dispose(); } catch { }

        foreach (var go in objectPool)
        {
            if (go != null)
            {
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
                Destroy(go);
            }
        }
        objectPool.Clear();

        foreach (var meshPair in meshCache) if (meshPair.Value != null) Destroy(meshPair.Value);
        meshCache.Clear();
    }

    IEnumerator StartupTimeout(float timeout)
    {
        float masterTimer = 0f;
        while (masterTimer < timeout)
        {
            if (isTrackingStarted) yield break;
            masterTimer += Time.unscaledDeltaTime;
            yield return null;
        }
        if (!isTrackingStarted)
        {
            Debug.LogError($"Timeout. Quitting.");
            FinishExperimentAndQuit();
        }
    }

    IEnumerator Bootstrap()
    {
        var t = ConnectSignaling();
        while (!t.IsCompleted) yield return null;
        Debug.Log("---RX_READY_FOR_TX---");
        StartCoroutine(ProcessSignals());
        StartCoroutine(PlaybackLoop());
    }

    IEnumerator PlaybackLoop()
    {
        while (!xmlReady) yield return null;

        int currentFrameIndex = 0;
        float initialPitch = mainCamera.transform.localEulerAngles.x;
        int totalFramesInXml = frames.Count;

        if (totalFramesInXml == 0)
        {
            Debug.LogError("XML Empty");
            yield return new WaitForSeconds(5f);
            FinishExperimentAndQuit();
            yield break;
        }

        if (!isTrackingStarted)
        {
            isTrackingStarted = true;
            Debug.Log($"Start Tracking. TileSize: {tileSize}, TargetFPS: {targetDataFPS}");
            requestStartTimes.Clear();
            highTileLatencies.Clear();
            System.GC.Collect();
        }

        float targetInterval = 1.0f / targetDataFPS;
        double nextFrameTime = Time.realtimeSinceStartup;

        while (dataFramesProcessed < targetFrameCount)
        {
            nextFrameTime += targetInterval;

            float progress = (float)currentFrameIndex / totalFramesInXml;
            float targetPitch = initialPitch + Mathf.Sin(progress * Mathf.PI * 2) * pitchAmplitude;
            Vector3 euler = mainCamera.transform.localEulerAngles;
            euler.x = targetPitch;
            mainCamera.transform.localEulerAngles = euler;

            yield return StartCoroutine(ProcessFrameFOV(frames[currentFrameIndex]));

            if (isTrackingStarted) dataFramesProcessed++;
            currentFrameIndex = (currentFrameIndex + 1) % totalFramesInXml;

            if (currentFrameIndex == 0)
            {
                receivedFiles.Clear();
                foreach (var meshPair in meshCache) if (meshPair.Value != null) Destroy(meshPair.Value);
                meshCache.Clear();
                requestStartTimes.Clear();
                Resources.UnloadUnusedAssets();
                GC.Collect();
            }

            double now = Time.realtimeSinceStartup;
            double waitTime = nextFrameTime - now;

            if (waitTime > 0) yield return new WaitForSeconds((float)waitTime);
            else yield return null;
        }

        isTrackingStarted = false;
        FinishExperimentAndQuit();
    }


    IEnumerator ProcessFrameFOV(FrameMeta frame)
    {
        List<Task<Mesh>> runningTasks = new List<Task<Mesh>>();
        List<string> taskUrls = new List<string>();
        List<bool> taskIsHigh = new List<bool>();

        foreach (var tile in frame.tiles)
        {
            bool inFOV = IsInFOV(tile);
            string urlToRequest = null;
            bool isHigh = false;

            if (inFOV)
            {
                urlToRequest = GetPathForCurrentTileSize(tile.highRel);
                isHigh = true;
            }
            else if (outsideFovMode == OutsideFovMode.RequestLow)
            {
                urlToRequest = GetPathForCurrentTileSize(tile.lowRel);
                isHigh = false;
            }

            if (urlToRequest != null)
            {
                if (requestStartTimes.ContainsKey(urlToRequest) || meshCache.ContainsKey(urlToRequest))
                    continue;

                if (isHigh) requestStartTimes[urlToRequest] = Time.realtimeSinceStartup;

                runningTasks.Add(GetTileMesh(urlToRequest, isHigh));
                taskUrls.Add(urlToRequest);
                taskIsHigh.Add(isHigh);
            }
        }

        poolIndex = 0;
        int processedInThisFrame = 0;

        while (runningTasks.Count > 0)
        {
            Task<Task<Mesh>> whenAnyTask = Task.WhenAny(runningTasks);
            while (!whenAnyTask.IsCompleted) yield return null;

            Task<Mesh> completedTask = whenAnyTask.Result;
            int index = runningTasks.IndexOf(completedTask);
            runningTasks.RemoveAt(index);

            string loadedUrl = taskUrls[index];
            bool isHigh = taskIsHigh[index];
            taskUrls.RemoveAt(index);
            taskIsHigh.RemoveAt(index);

            Mesh mesh = completedTask.Result;

            if (mesh != null)
            {
                if (isHigh && requestStartTimes.ContainsKey(loadedUrl))
                {
                    float latency = Time.realtimeSinceStartup - requestStartTimes[loadedUrl];
                    requestStartTimes.Remove(loadedUrl);
                    highTileLatencies.Add(latency);
                }

                GameObject go = GetPooledObject();
                go.name = Path.GetFileName(loadedUrl);
                go.transform.localPosition = Vector3.zero;

                var mf = go.GetComponent<MeshFilter>();
                if (mf.sharedMesh != null && mf.sharedMesh != mesh) Destroy(mf.sharedMesh);
                mf.sharedMesh = mesh;

                mesh.UploadMeshData(false);

                var mr = go.GetComponent<MeshRenderer>();
                mr.material = isHigh ? pointMaterial : lowPointMaterial;
                go.SetActive(true);

                processedInThisFrame++;
            }

            if (processedInThisFrame >= maxMeshUploadsPerFrame)
            {
                processedInThisFrame = 0;
                yield return null;
            }
        }

        int disableCount = 0;
        for (int i = poolIndex; i < objectPool.Count; i++)
        {
            if (objectPool[i] != null && objectPool[i].activeSelf)
            {
                objectPool[i].SetActive(false);
                disableCount++;
                if (disableCount >= 50) { disableCount = 0; yield return null; }
            }
        }
    }

    async Task<Mesh> GetTileMesh(string relativePath, bool isHigh)
    {
        if (meshCache.TryGetValue(relativePath, out Mesh mesh)) return mesh;

        if (!receivedFiles.TryGetValue(relativePath, out byte[] bytes))
        {
            RequestTile(relativePath, isHigh ? 10 : 0);
            float startTime = Time.unscaledTime;

            float timeout = isHigh ? 5.0f : 2.0f;

            while (!receivedFiles.TryGetValue(relativePath, out bytes))
            {
                if (Time.unscaledTime - startTime > timeout) return null;
                await Task.Delay(5);
            }
        }

        if (bytes == null || bytes.Length == 0) return null;

        mesh = await LoadDracoMeshFromBytes(bytes);

        if (mesh != null) meshCache.TryAdd(relativePath, mesh);
        return mesh;
    }

    async Task<Mesh> LoadDracoMeshFromBytes(byte[] bytes)
    {
        if (dracoLoader == null) return null;

        await dracoSemaphore.WaitAsync();
        Mesh mesh = null;
        try
        {
            mesh = await dracoLoader.ConvertDracoMeshToUnity(bytes);
        }
        catch (Exception e) { Debug.LogError($"Draco Err: {e.Message}"); }
        finally { dracoSemaphore.Release(); }

        if (mesh == null) return null;

        if (mesh.colors == null || mesh.colors.Length == 0)
        {
            Color[] colors = new Color[mesh.vertexCount];
            for (int i = 0; i < mesh.vertexCount; i++) colors[i] = Color.white;
            mesh.colors = colors;
        }

        int[] idx = new int[mesh.vertexCount];
        for (int i = 0; i < mesh.vertexCount; i++) idx[i] = i;
        mesh.SetIndices(idx, MeshTopology.Points, 0);

        return mesh;
    }

    void RequestTile(string relativePath, int priority = 0)
    {
        if (ctrlDC == null || ctrlDC.ReadyState != RTCDataChannelState.Open) return;
        var msg = new RequestTileMsg { relativePath = relativePath, priority = priority };
        ctrlDC.Send(Encoding.UTF8.GetBytes(JsonUtility.ToJson(msg)));
    }


    void OnCtrlMessage(byte[] data)
    {
        string json = Encoding.UTF8.GetString(data);
        CtrlType tag = null; try { tag = JsonUtility.FromJson<CtrlType>(json); } catch { }
        if (tag == null || string.IsNullOrEmpty(tag.type)) return;

        if (tag.type == "file_start")
        {
            var s = JsonUtility.FromJson<FileStart>(json);
            activeFileName = s.name.Replace('\\', '/');
            activeFileStream?.Dispose();
            activeFileStream = new MemoryStream(Mathf.Max(s.bytes, 4));
        }
        else if (tag.type == "file_end")
        {
            if (activeFileStream != null && activeFileName != null)
            {
                HandleCompletedFile(activeFileName, activeFileStream.ToArray());
                activeFileStream?.Dispose(); activeFileStream = null; activeFileName = null;
            }
        }
    }

    void OnTilesMessage(byte[] data)
    {
        if (activeFileStream != null) activeFileStream.Write(data, 0, data.Length);
    }

    void HandleCompletedFile(string nameRel, byte[] bytes)
    {
        if (nameRel.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            ParseXml(bytes); return;
        }

        if (nameRel.StartsWith("__BATCH__/"))
        {
            if (isTrackingStarted) totalBytesTracked += (ulong)bytes.Length;
            UnpackAndProcessBatch(nameRel, bytes);
        }
        else
        {
            if (isTrackingStarted) totalBytesTracked += (ulong)bytes.Length;
            receivedFiles.TryAdd(nameRel, bytes);
        }
    }

    private void UnpackAndProcessBatch(string batchName, byte[] batchPayload)
    {
        try
        {
            using (var ms = new MemoryStream(batchPayload))
            using (var reader = new BinaryReader(ms, Encoding.UTF8))
            {
                int fileCount = reader.ReadInt32();
                for (int i = 0; i < fileCount; i++)
                {
                    string relPath = reader.ReadString();
                    int dataLength = reader.ReadInt32();
                    byte[] fileData = reader.ReadBytes(dataLength);
                    if (fileData.Length == dataLength) receivedFiles.TryAdd(relPath, fileData);
                }
            }
        }
        catch { }
    }

    async Task ConnectSignaling()
    {
        wsCts = new CancellationTokenSource();
        ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(signalingUrl), wsCts.Token);
            _ = Task.Run(ReceiveLoop);
        }
        catch (Exception e) { Debug.LogError($"Signaling Error: {e.Message}"); }
    }

    async Task ReceiveLoop()
    {
        var buf = new ArraySegment<byte>(new byte[64 * 1024]);
        while (!wsCts.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                using var ms = new MemoryStream(); WebSocketReceiveResult r;
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

    IEnumerator ProcessSignals()
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
                        var op1 = pc.SetRemoteDescription(ref desc); yield return op1;
                        var ans = pc.CreateAnswer(); yield return ans;
                        var op2 = pc.SetLocalDescription(); yield return op2;
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
        {
            var data = Encoding.UTF8.GetBytes(JsonUtility.ToJson(s));
            _ = ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    void ParseXml(byte[] xmlBytes)
    {
        if (xmlReady) return;
        frames.Clear(); receivedFiles.Clear();
        var doc = new XmlDocument();
        try
        {
            string xmlString = Encoding.UTF8.GetString(xmlBytes);
            doc.LoadXml(xmlString);
        }
        catch (Exception e) { Debug.LogError($"XML Error: {e.Message}"); return; }
        var root = doc.DocumentElement;
        if (root == null) return;
        foreach (XmlNode f in root.SelectNodes("Frame"))
        {
            var fm = new FrameMeta { id = Int(f, "id") };
            int tileIdCounter = 0;
            foreach (XmlNode t in f.SelectNodes("Tile"))
            {
                fm.tiles.Add(new TileMeta
                {
                    id = tileIdCounter++,
                    pos = new Vector3(F(t, "x"), F(t, "y"), F(t, "z")),
                    highRel = t.SelectSingleNode("High")?.Attributes?["file"]?.Value.Replace('\\', '/'),
                    lowRel = t.SelectSingleNode("Low")?.Attributes?["file"]?.Value.Replace('\\', '/')
                });
            }
            frames.Add(fm);
        }
        xmlReady = true;
    }

    private static float F(XmlNode n, string attr) => float.TryParse(n.Attributes?[attr]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    private static int Int(XmlNode n, string attr) => int.TryParse(n.Attributes?[attr]?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private string GetTileSizeString() => tileSize.ToString().Substring(1);
    private string GetPathForCurrentTileSize(string pathTemplate) => string.IsNullOrEmpty(pathTemplate) ? null : pathTemplate.Replace("{SIZE}", GetTileSizeString());

    float GetFovMargin(TileSize size)
    {
        switch (size)
        {
            case TileSize._1024: return 25.0f;
            case TileSize._512: return 18.0f;
            case TileSize._256: return 10.0f;
            case TileSize._128: return 5.0f;
            case TileSize._64: return 2.0f;
            default: return 0f;
        }
    }

    bool IsInFOV(TileMeta tile)
    {
        if (mainCamera == null) return false;
        Vector3 dirToTile = (tile.pos - mainCamera.transform.position).normalized;
        float horizontalAngle = Vector3.Angle(Vector3.ProjectOnPlane(mainCamera.transform.forward, Vector3.up), Vector3.ProjectOnPlane(dirToTile, Vector3.up));
        float verticalAngle = Vector3.Angle(Vector3.ProjectOnPlane(mainCamera.transform.forward, mainCamera.transform.right), Vector3.ProjectOnPlane(dirToTile, mainCamera.transform.right));
        float margin = GetFovMargin(tileSize);
        return horizontalAngle < (horizontalCutDeg / 2f + margin) && verticalAngle < (verticalCutDeg + margin);
    }

    GameObject GetPooledObject()
    {
        GameObject go;
        if (poolIndex < objectPool.Count) go = objectPool[poolIndex];
        else
        {
            go = new GameObject("PooledTile");
            go.transform.parent = objectParent.transform;
            go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.material = pointMaterial;
            objectPool.Add(go);
        }
        poolIndex++;
        return go;
    }

    void FinishExperimentAndQuit()
    {
        double mbpsOnWire = (elapsedTime > 0) ? (totalBytesTracked * 8.0) / (elapsedTime * 1_000_000.0) : 0;
        float finalDataFps = (elapsedTime > 0) ? ((float)dataFramesProcessed / elapsedTime) : 0;
        float finalRenderFps = (elapsedTime > 0) ? ((float)renderingFrameCount / elapsedTime) : 0;
        float avgLatencyMs = 0f;
        if (highTileLatencies.Count > 0) { float sum = 0; foreach (var t in highTileLatencies) sum += t; avgLatencyMs = (sum / highTileLatencies.Count) * 1000f; }

        Debug.Log($"--- DRC/WebRTC Result ---");
        Debug.Log($"[Settings] TileSize={tileSize}, TargetFPS={targetDataFPS}");
        Debug.Log($"[Network] Mbps: {mbpsOnWire:F2}");
        Debug.Log($"[Content] Play FPS: {finalDataFps:F2}");
        Debug.Log($"[Latency] Avg HQ Latency: {avgLatencyMs:F2} ms");

        if (dataLogger != null)
        {
            dataLogger.SaveDirectResult(targetFrameCount, tileSize.ToString(), targetDataFPS, totalBytesTracked, elapsedTime, mbpsOnWire, finalDataFps, finalRenderFps, avgLatencyMs);
        }

        meshCache.Clear();
        receivedFiles.Clear();
        highTileLatencies.Clear();
        frames.Clear();
        Resources.UnloadUnusedAssets();
        System.GC.Collect();

        var manager = FindObjectOfType<ExperimentManager>();
        if (manager != null)
        {
            manager.OnExperimentFinished();
        }
        else
        {
            QuitApplication();
        }
    }

    void QuitApplication()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}