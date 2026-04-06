using System.Threading.Tasks;
using UnityEngine;
using Draco.Sample.Decode; // 샘플 네임스페이스

namespace Draco
{
    /// <summary>
    /// 샘플의 DracoDecoder를 WebRTCDracoFovPlayer가 기대하는
    /// DracoMeshLoader API( ConvertDracoMeshToUnity )로 래핑.
    /// </summary>
    public class DracoMeshLoader
    {
        public async Task<Mesh> ConvertDracoMeshToUnity(byte[] dracoBytes)
        {
            if (dracoBytes == null || dracoBytes.Length == 0)
                return null;

            // 샘플 API: 바이트 배열을 Mesh로 디코딩
            var mesh = await DracoDecoder.DecodeMesh(dracoBytes);
            return mesh ?? new Mesh();
        }
    }
}
