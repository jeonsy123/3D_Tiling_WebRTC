// DracoPointMeshLoader.cs
// Draco 바이트 → Unity Mesh 변환 + centroid 기준 원점 정렬 + 포인트 토폴로지 세팅
using UnityEngine;
using System.Threading.Tasks;
using Draco;

public static class DracoPointMeshLoader
{
    public static async Task<Mesh> LoadPointsAsync(byte[] dracoBytes, bool centerToCentroid = true)
    {
        var loader = new DracoMeshLoader();
        var mesh = await loader.ConvertDracoMeshToUnity(dracoBytes);
        if (mesh == null || mesh.vertexCount == 0) return null;

        if (centerToCentroid)
        {
            var verts = mesh.vertices;
            Vector3 c = Vector3.zero;
            for (int i = 0; i < verts.Length; i++) c += verts[i];
            c /= Mathf.Max(1, verts.Length);
            for (int i = 0; i < verts.Length; i++) verts[i] -= c;
            mesh.vertices = verts;
        }

        mesh.RecalculateBounds();

        // Point topology (HTTP PLY와 동일하게 포인트로 렌더)
        int vcount = Mathf.Max(1, mesh.vertexCount);
        var idx = new int[vcount];
        for (int i = 0; i < vcount; i++) idx[i] = i;
        mesh.SetIndices(idx, MeshTopology.Points, 0);

        return mesh;
    }
}
