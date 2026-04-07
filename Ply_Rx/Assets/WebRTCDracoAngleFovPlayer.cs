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
using System.Globalization;
using Unity.Collections;
using UnityEngine.Rendering;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Debug = UnityEngine.Debug;

public enum TileSize { _64, _128, _256, _512, _1024 }
public enum OutsideFovMode { Skip, RequestLow }

public class WebRTCPlyFovPlayer : MonoBehaviour
{
    public class ParsedMeshData : IDisposable
    {
        public NativeArray<VertexData> vertices;
        public NativeArray<int> indices;
        public int vCount;

        public void Dispose()
        {
            if (vertices.IsCreated) vertices.Dispose();
            if (indices.IsCreated) indices.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VertexData
    {
        public Vector3 pos;
        public Color32 color;
    }

    [Header("Data Logger")]
    public DataLogger dataLogger;

    [Header("Signaling")]
    public string signalingUrl = "ws://127.0.0.1:3001?room=demo";

    [Header("XML / Files")]
    public TileSize tileSize = TileSize._128;

    [Header("FOV Policy")]
    public OutsideFovMode outsideFovMode = OutsideFovMode.Skip;

    [Header("Playback Settings")]
    public float targetDataFPS = 2.0f;
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

    private readonly ConcurrentDictionary<string, byte[]> receivedFiles = new ConcurrentDictionary<string, byte[]>();
    private readonly ConcurrentDictionary<string, Mesh> meshCache = new ConcurrentDictionary<string, Mesh>();

    private readonly SemaphoreSlim parseSemaphore = new SemaphoreSlim(4, 4);

    private Dictionary<string, float> requestStartTimes = new Dictionary<string, float>();
    private List<float> highTileLatencies = new List<float>();

    private class TileMeta { public int id; public Vector3 pos; public string highRel; public string lowRel; }
    private class FrameMeta { public int id; public List<TileMeta> tiles = new(); }
    private readonly List<FrameMeta> frames = new();

    private bool xmlReady = false;
    private List<GameObject> objectPool = new List<GameObject>();
    private int poolIndex = 0;

    private bool isTrackingStarted = false;
    private int renderingFrameCount = 0;
    private int dataFramesProcessed = 0;
    private float elapsedTime = 0f;
    private ulong totalBytesTracked = 0;

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
        if (mainCamera == null) mainCamera = Camera.main;
        if (objectParent == null) { var go = new GameObject("TileGroup"); objectParent = go.transform; }

        StartCoroutine(StartupTimeout(30f));

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
        try { wsCts?.Cancel(); ws?.Dispose(); pc?.Close(); parseSemaphore?.Dispose(); } catch { }
        foreach (var go in objectPool) if (go != null) Destroy(go);
        foreach (var m in meshCache.Values) if (m != null) Destroy(m);
    }

    IEnumerator StartupTimeout(float timeout)
    {
        float timer = 0f;
        while (timer < timeout)
        {
            if (isTrackingStarted) yield break;
            timer += Time.unscaledDeltaTime;
            yield return null;
        }
        if (!isTrackingStarted) { Debug.LogError("Timeout"); FinishExperimentAndQuit(); }
    }

    IEnumerator Bootstrap()
    {
        var t = ConnectSignaling();
        while (!t.IsCompleted) yield return null;
        Debug.Log("---RX_READY_FOR_TX---");
        StartCoroutine(ProcessSignals());

        float timeout = 5.0f;
        while (ctrlDC == null || ctrlDC.ReadyState != RTCDataChannelState.Open)
        {
            timeout -= Time.deltaTime;
            if (timeout <= 0) break;
            yield return null;
        }

        if (ctrlDC != null && ctrlDC.ReadyState == RTCDataChannelState.Open)
        {
            string xmlName = GetCurrentXmlName();
            Debug.Log($"Requesting XML: {xmlName}");
            xmlReady = false;
            RequestTile(xmlName, 10);
        }
        StartCoroutine(PlaybackLoop());
    }

    IEnumerator PlaybackLoop()
    {
        while (!xmlReady) yield return null;

        int currentFrameIndex = 0;
        float initialPitch = mainCamera.transform.localEulerAngles.x;
        int totalFramesInXml = frames.Count;

        if (totalFramesInXml == 0) { FinishExperimentAndQuit(); yield break; }

        if (!isTrackingStarted)
        {
            isTrackingStarted = true;
            Debug.Log($"Start Tracking. TargetFPS: {targetDataFPS}");
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
            mainCamera.transform.localEulerAngles = new Vector3(targetPitch, mainCamera.transform.localEulerAngles.y, 0);

            yield return StartCoroutine(ProcessFrameFOV(frames[currentFrameIndex]));

            if (isTrackingStarted) dataFramesProcessed++;
            currentFrameIndex = (currentFrameIndex + 1) % totalFramesInXml;

            if (currentFrameIndex == 0)
            {
                receivedFiles.Clear();
                foreach (var m in meshCache.Values) if (m != null) Destroy(m);
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
            string url = null;
            bool isHigh = false;

            if (inFOV)
            {
                url = GetPathForCurrentTileSize(tile.highRel);
                isHigh = true;
            }
            else if (outsideFovMode == OutsideFovMode.RequestLow)
            {
                url = GetPathForCurrentTileSize(tile.lowRel);
                isHigh = false;
            }

            if (url != null)
            {
                if (isHigh && !requestStartTimes.ContainsKey(url))
                    requestStartTimes[url] = Time.realtimeSinceStartup;

                runningTasks.Add(GetTileMeshAsync(url, inFOV ? 1 : 0));
                taskUrls.Add(url);
                taskIsHigh.Add(isHigh);
            }
        }

        poolIndex = 0;
        int processedInThisFrame = 0;

        while (runningTasks.Count > 0)
        {
            Task<Task<Mesh>> whenAny = Task.WhenAny(runningTasks);
            while (!whenAny.IsCompleted) yield return null;

            Task<Mesh> completedTask = whenAny.Result;
            int idx = runningTasks.IndexOf(completedTask);
            runningTasks.RemoveAt(idx);

            string loadedUrl = taskUrls[idx];
            bool isHigh = taskIsHigh[idx];
            taskUrls.RemoveAt(idx);
            taskIsHigh.RemoveAt(idx);

            Mesh mesh = completedTask.Result;

            if (mesh != null)
            {
                if (isHigh && requestStartTimes.ContainsKey(loadedUrl))
                {
                    highTileLatencies.Add(Time.realtimeSinceStartup - requestStartTimes[loadedUrl]);
                    requestStartTimes.Remove(loadedUrl);
                }

                GameObject go = GetPooledObject();
                go.name = Path.GetFileName(loadedUrl);
                go.transform.localPosition = Vector3.zero;

                var mf = go.GetComponent<MeshFilter>();
                if (mf.sharedMesh != null && mf.sharedMesh != mesh) Destroy(mf.sharedMesh);
                mf.sharedMesh = mesh;

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

        for (int i = poolIndex; i < objectPool.Count; i++)
        {
            if (objectPool[i].activeSelf) objectPool[i].SetActive(false);
        }
    }

    async Task<Mesh> GetTileMeshAsync(string relativePath, int priority)
    {
        if (meshCache.TryGetValue(relativePath, out Mesh cached)) return cached;

        if (!receivedFiles.TryGetValue(relativePath, out byte[] bytes))
        {
            RequestTile(relativePath, priority);
            float start = Time.unscaledTime;
            while (!receivedFiles.TryGetValue(relativePath, out bytes))
            {
                if (Time.unscaledTime - start > 5.0f) return null;
                await Task.Delay(5);
            }
        }
        if (bytes == null || bytes.Length == 0) return null;

        await parseSemaphore.WaitAsync();
        ParsedMeshData parsedData = null;
        try
        {
            parsedData = await Task.Run(() => ParsePlyNative(bytes));
        }
        finally
        {
            parseSemaphore.Release();
        }

        if (parsedData == null) return null;

        Mesh mesh = new Mesh();
        mesh.SetVertexBufferParams(parsedData.vCount,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4));
        mesh.SetIndexBufferParams(parsedData.vCount, IndexFormat.UInt32);

        mesh.SetVertexBufferData(parsedData.vertices, 0, 0, parsedData.vCount);
        mesh.SetIndexBufferData(parsedData.indices, 0, 0, parsedData.vCount);

        mesh.subMeshCount = 1;
        mesh.SetSubMesh(0, new SubMeshDescriptor(0, parsedData.vCount, MeshTopology.Points));
        mesh.RecalculateBounds();
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);

        parsedData.Dispose();

        meshCache.TryAdd(relativePath, mesh);
        return mesh;
    }

    ParsedMeshData ParsePlyNative(byte[] data)
    {
        int headerEnd = 0;
        int vCount = 0;

        try
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new StreamReader(ms, Encoding.ASCII))
            {
                string line;
                int linesRead = 0;
                while ((line = reader.ReadLine()) != null && linesRead < 100)
                {
                    linesRead++;
                    if (line.StartsWith("element vertex"))
                    {
                        string[] parts = line.Trim().Split(' ');
                        if (parts.Length >= 3) int.TryParse(parts[2], out vCount);
                    }
                    if (line.Trim() == "end_header") break;
                }
            }
        }
        catch { return null; }

        for (int i = 0; i < 5000 && i < data.Length - 10; i++)
        {
            if (data[i] == 'e' && data[i + 9] == 'r')
            {
                string s = Encoding.ASCII.GetString(data, i, 10);
                if (s == "end_header")
                {
                    int k = i + 10;
                    while (k < data.Length && (data[k] == '\r' || data[k] == '\n')) k++;
                    headerEnd = k;
                    break;
                }
            }
        }

        if (vCount == 0 || headerEnd == 0) return null;

        var vertices = new NativeArray<VertexData>(vCount, Allocator.Persistent);
        var indices = new NativeArray<int>(vCount, Allocator.Persistent);

        int stride = 15;
        int maxCount = (data.Length - headerEnd) / stride;
        if (vCount > maxCount) vCount = maxCount;

        for (int i = 0; i < vCount; i++)
        {
            int baseIdx = headerEnd + (i * stride);

            float x = BitConverter.ToSingle(data, baseIdx);
            float y = BitConverter.ToSingle(data, baseIdx + 4);
            float z = BitConverter.ToSingle(data, baseIdx + 8);
            byte r = data[baseIdx + 12];
            byte g = data[baseIdx + 13];
            byte b = data[baseIdx + 14];

            vertices[i] = new VertexData { pos = new Vector3(x, y, z), color = new Color32(r, g, b, 255) };
            indices[i] = i;
        }

        return new ParsedMeshData { vertices = vertices, indices = indices, vCount = vCount };
    }

    void HandleCompletedFile(string nameRel, byte[] bytes)
    {
        if (nameRel.EndsWith(".xml")) { ParseXml(bytes); return; }
        if (nameRel.StartsWith("__BATCH__/"))
        {
            if (isTrackingStarted) totalBytesTracked += (ulong)bytes.Length;
            UnpackBatch(bytes);
        }
        else
        {
            if (isTrackingStarted) totalBytesTracked += (ulong)bytes.Length;
            receivedFiles.TryAdd(nameRel, bytes);
        }
    }

    void UnpackBatch(byte[] data)
    {
        try
        {
            using (var ms = new MemoryStream(data)) using (var br = new BinaryReader(ms))
            {
                int c = br.ReadInt32();
                for (int i = 0; i < c; i++)
                {
                    string p = br.ReadString(); int l = br.ReadInt32(); byte[] d = br.ReadBytes(l);
                    receivedFiles.TryAdd(p, d);
                }
            }
        }
        catch { }
    }

    void ParseXml(byte[] bytes)
    {
        frames.Clear(); receivedFiles.Clear();
        var doc = new XmlDocument();
        doc.LoadXml(Encoding.UTF8.GetString(bytes));
        foreach (XmlNode f in doc.SelectNodes("//Frame"))
        {
            var fm = new FrameMeta { id = int.Parse(f.Attributes["id"].Value) };
            int tid = 0;
            foreach (XmlNode t in f.SelectNodes("Tile"))
            {
                fm.tiles.Add(new TileMeta
                {
                    id = tid++,
                    pos = new Vector3(float.Parse(t.Attributes["x"].Value), float.Parse(t.Attributes["y"].Value), float.Parse(t.Attributes["z"].Value)),
                    highRel = ForcePath(t.SelectSingleNode("High")?.Attributes["file"]?.Value),
                    lowRel = ForcePath(t.SelectSingleNode("Low")?.Attributes["file"]?.Value)
                });
            }
            frames.Add(fm);
        }
        xmlReady = true;
    }

    string ForcePath(string p)
    {
        if (p == null) return null;
        p = p.Replace('\\', '/').Replace(".drc", ".ply");
        string sz = tileSize.ToString().Substring(1);
        if (!p.Contains(sz)) p = p.Replace("128", sz).Replace("64", sz).Replace("256", sz).Replace("512", sz).Replace("1024", sz);
        return p;
    }
    string GetPathForCurrentTileSize(string p) => ForcePath(p);
    string GetCurrentXmlName() => $"tile_metadata_{tileSize.ToString().Substring(1)}.xml";

    float GetFovMargin(TileSize size)
    {
        switch (size)
        {
            case TileSize._1024: return 25.0f;
            case TileSize._512: return 18.0f;
            case TileSize._256: return 10.0f;
            case TileSize._128: return 5.0f;
            case TileSize._64: return 2.0f;
            default: return 5.0f;
        }
    }

    bool IsInFOV(TileMeta t)
    {
        if (!mainCamera) return false;
        Vector3 d = (t.pos - mainCamera.transform.position).normalized;
        float h = Vector3.Angle(Vector3.ProjectOnPlane(mainCamera.transform.forward, Vector3.up), Vector3.ProjectOnPlane(d, Vector3.up));
        float v = Vector3.Angle(Vector3.ProjectOnPlane(mainCamera.transform.forward, mainCamera.transform.right), Vector3.ProjectOnPlane(d, mainCamera.transform.right));

        float margin = GetFovMargin(tileSize);
        return h < (horizontalCutDeg / 2f + margin) && v < (verticalCutDeg + margin);
    }

    GameObject GetPooledObject()
    {
        if (poolIndex < objectPool.Count) return objectPool[poolIndex++];
        var go = new GameObject("PooledTile"); go.transform.parent = objectParent;
        go.AddComponent<MeshFilter>(); go.AddComponent<MeshRenderer>(); objectPool.Add(go);
        poolIndex++; return go;
    }

    async Task ConnectSignaling() { wsCts = new CancellationTokenSource(); ws = new ClientWebSocket(); await ws.ConnectAsync(new Uri(signalingUrl), wsCts.Token); _ = Task.Run(ReceiveLoop); }
    async Task ReceiveLoop() { var b = new byte[1024 * 64]; while (!wsCts.IsCancellationRequested && ws.State == WebSocketState.Open) { var r = await ws.ReceiveAsync(b, wsCts.Token); rxSignal.Enqueue(Encoding.UTF8.GetString(b, 0, r.Count)); } }
    IEnumerator ProcessSignals() { while (true) { if (rxSignal.TryDequeue(out var s)) { var m = JsonUtility.FromJson<Sig>(s); if (m != null && m.type == "offer") { var d = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = m.sdp }; pc.SetRemoteDescription(ref d); yield return pc.CreateAnswer(); yield return pc.SetLocalDescription(); SendSignal(new Sig { type = "answer", sdp = pc.LocalDescription.sdp }); } else if (m != null && m.type == "candidate") pc.AddIceCandidate(new RTCIceCandidate(new RTCIceCandidateInit { candidate = m.candidate, sdpMid = m.sdpMid, sdpMLineIndex = m.sdpMLineIndex })); } yield return null; } }
    void SendSignal(Sig s) { if (ws.State == WebSocketState.Open) ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonUtility.ToJson(s))), WebSocketMessageType.Text, true, CancellationToken.None); }
    void OnCtrlMessage(byte[] d) { var s = Encoding.UTF8.GetString(d); if (s.Contains("file_start")) { var f = JsonUtility.FromJson<FileStart>(s); activeFileName = f.name.Replace('\\', '/'); activeFileStream?.Dispose(); activeFileStream = new MemoryStream(Mathf.Max(f.bytes, 4)); } else if (s.Contains("file_end")) { HandleCompletedFile(activeFileName, activeFileStream.ToArray()); activeFileStream.Dispose(); activeFileStream = null; } }
    void OnTilesMessage(byte[] d) { activeFileStream?.Write(d, 0, d.Length); }
    void RequestTile(string p, int pr) { if (ctrlDC?.ReadyState == RTCDataChannelState.Open) ctrlDC.Send(Encoding.UTF8.GetBytes(JsonUtility.ToJson(new RequestTileMsg { relativePath = p, priority = pr }))); }

    void FinishExperimentAndQuit()
    {
        double mbps = (elapsedTime > 0) ? (totalBytesTracked * 8.0) / (elapsedTime * 1e6) : 0;
        float dfps = (elapsedTime > 0) ? dataFramesProcessed / elapsedTime : 0;
        float rfps = (elapsedTime > 0) ? renderingFrameCount / elapsedTime : 0;
        float lat = (highTileLatencies.Count > 0) ? 0 : 0;
        if (highTileLatencies.Count > 0) { float sum = 0; foreach (float f in highTileLatencies) sum += f; lat = (sum / highTileLatencies.Count) * 1000f; }

        if (dataLogger) dataLogger.SaveDirectResult(targetFrameCount, tileSize.ToString(), targetDataFPS, totalBytesTracked, elapsedTime, mbps, dfps, rfps, lat);

        Debug.Log($"Result: Mbps={mbps:F2}, FPS={dfps:F2}, Lat={lat:F2}ms");

        var manager = FindObjectOfType<ExperimentManager>();
        if (manager != null)
        {
            manager.OnExperimentFinished();
        }
        else
        {
            Debug.LogWarning("ExperimentManager를 찾을 수 없습니다! 강제 종료합니다.");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}