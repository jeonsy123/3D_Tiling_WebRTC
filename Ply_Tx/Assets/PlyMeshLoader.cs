using UnityEngine;
using System.Collections.Generic;
using System.IO;

public static class PlyMeshLoader
{
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

    public static Mesh LoadPlyFromBytes(byte[] data)
    {
        List<Vector3> points = new List<Vector3>();
        List<Color32> colors = new List<Color32>();

        using (MemoryStream ms = new MemoryStream(data))
        using (BinaryReader br = new BinaryReader(ms))
        {
            int vertexCount = 0;
            bool headerEnded = false;

            while (!headerEnded)
            {
                string line = ReadLine(br);
                if (line.StartsWith("element vertex"))
                    vertexCount = int.Parse(line.Split(' ')[2]);

                if (line.Trim() == "end_header")
                    headerEnded = true;
            }

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

        Vector3 center = Vector3.zero;
        foreach (var p in points) center += p;
        center /= points.Count;

        for (int i = 0; i < points.Count; i++)
            points[i] -= center;

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

    private static string ReadLine(BinaryReader br)
    {
        List<byte> bytes = new List<byte>();
        byte b;
        while (br.BaseStream.Position < br.BaseStream.Length && (b = br.ReadByte()) != '\n')
            bytes.Add(b);
        return System.Text.Encoding.ASCII.GetString(bytes.ToArray()).Trim();
    }
}
