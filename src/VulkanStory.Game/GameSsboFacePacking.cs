using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Retained face-packing loop; returned storage is borrowed on the current thread.
/// <summary>Packs original mesh attributes into the retained shader-storage face layout without changing its byte ABI.</summary>
internal static class GameSsboFacePacking
{
    [ThreadStatic] private static FaceData[] facedataBuffer = null!;
    /// <summary>Converts original mesh vertices/indices into the retained FaceData storage layout.</summary>
    /// <param name="data">Original mesh attributes and indices.</param>
    /// <returns>Packed face records consumed by the SSBO terrain path.</returns>
    internal static FaceData[] Pack(MeshData data)
    {
        int verticesCount = data.VerticesCount;
        if (facedataBuffer == null || facedataBuffer.Length < verticesCount / 4)
        {
            facedataBuffer = new FaceData[verticesCount / 4];
        }
        float[] xyz = data.xyz;
        float[] uv = data.Uv;
        int[] flags = data.Flags;
        int[]? array = ((data.CustomInts != null && data.CustomInts.Count > 0) ? data.CustomInts.Values : null);
        int num = ((data.CustomInts == null || data.CustomInts.Count <= 0) ? 1 : (data.CustomInts.InterleaveStride / 4));
        FaceData[] array2 = facedataBuffer;
        for (int i = 0; i < verticesCount; i += 4)
        {
            float num2 = uv[i * 2];
            float num3 = uv[i * 2 + 1];
            float num4 = uv[i * 2 + 3];
            float num5 = uv[i * 2 + 4];
            float num6 = uv[i * 2 + 5];
            if (num2 < -1.5E-05f || num2 > 1.000015f || num3 < -1.5E-05f || num3 > 1.000015f)
            {
                num2 = 0f;
                num3 = 0f;
            }
            if (num5 < -1.5E-05f || num5 > 1.000015f || num6 < -1.5E-05f || num6 > 1.000015f)
            {
                num5 = 0f;
                num6 = 0f;
            }
            bool rotateUV;
            if (rotateUV = num3 == num4)
            {
                float num7 = uv[i * 2 + 2];
                if (num5 != num7)
                {
                    rotateUV = false;
                }
            }
            array2[i / 4] = new FaceData(xyz, i * 3, num2, num3, num5 - num2, num6 - num3, flags, i, (array != null) ? array[i * num] : 0, rotateUV);
        }
        return array2;
    }
}
