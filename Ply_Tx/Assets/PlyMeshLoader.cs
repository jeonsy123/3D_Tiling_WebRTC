using UnityEngine;
using System.Collections.Generic;
using System.IO;

public static class PlyMeshLoader
{
    // ✅ 1. 기존 방식: 로컬 파일 경로로부터 로드
    public static Mesh LoadPlyAsMesh(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            Debug.LogError("PLY 파일을 찾을 수 없습니다: " + fullPath);
            return null;
        }

        byte[] data = File.ReadAllBytes(fullPath);
        return LoadPlyFromBytes(data);
    }

    // ✅ 2. 바이너리 PLY 파일로부터 Mesh 생성
    public static Mesh LoadPlyFromBytes(byte[] data)
    {
        List<Vector3> points = new List<Vector3>();
        List<Color32> colors = new List<Color32>();

        using (MemoryStream ms = new MemoryStream(data))
        using (BinaryReader br = new BinaryReader(ms))
        {
            int vertexCount = 0;
            bool headerEnded = false;

            // 1. 헤더 파싱
            while (!headerEnded)
            {
                string line = ReadLine(br);
                if (line.StartsWith("element vertex"))
                    vertexCount = int.Parse(line.Split(' ')[2]);

                if (line.Trim() == "end_header")
                    headerEnded = true;
            }

            // 2. 본문 파싱 (binary_little_endian)
            for (int i = 0; i < vertexCount; i++)
            {
                float x = br.ReadSingle();
                float y = br.ReadSingle();
                float z = br.ReadSingle();

                byte r = br.ReadByte();
                byte g = br.ReadByte();
                byte b = br.ReadByte();

                points.Add(new Vector3(x, y, z));
                colors.Add(new Color32(r, g, b, 255));
            }
        }

        // 중심 정렬
        Vector3 center = Vector3.zero;
        foreach (var p in points) center += p;
        center /= points.Count;

        for (int i = 0; i < points.Count; i++)
            points[i] -= center;

        // Mesh 생성
        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(points);
        mesh.SetColors(colors);

        int[] indices = new int[points.Count];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;

        mesh.SetIndices(indices, MeshTopology.Points, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    // ASCII 라인 읽기
    private static string ReadLine(BinaryReader br)
    {
        List<byte> bytes = new List<byte>();
        byte b;
        while (br.BaseStream.Position < br.BaseStream.Length && (b = br.ReadByte()) != '\n')
            bytes.Add(b);
        return System.Text.Encoding.ASCII.GetString(bytes.ToArray()).Trim();
    }
}
