using System.Numerics;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.API.MathTools;
using VulkanStory.Render.Vulkan.Vr;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    internal int OpenXrEye { get; private set; } = -1;
    internal (int Width, int Height) RenderExtent => device?.OpenXr is { Width: > 0, Height: > 0 } xr
        ? (xr.Width, xr.Height) : Window.PixelSize;
    private ClientMain? eyeClient;
    private readonly double[] centreCamera = new double[16], centreOrigin = new double[16], centreProjection = new double[16];
    private double centreEyeX, centreEyeY, centreEyeZ;
    private bool referenceReady;
    private int referenceRevision;
    private Vector3 referencePosition;
    private Quaternion referenceRotation;
    private static readonly AccessTools.FieldRef<ScreenManager, GuiScreen> ActiveScreen = AccessTools.FieldRefAccess<ScreenManager, GuiScreen>("CurrentScreen");

    internal void RenderStereo(ScreenManager manager, float delta, Action<ScreenManager, float> render)
    {
        eyeClient = null;
        object screen = ActiveScreen(manager);
        try
        {
            for (int eye = 0; eye < 2; eye++)
            {
                OpenXrEye = eye;
                Graphics.Stated.UiImageFramebuffer = 0;
                Device.RedirectDefaultFramebuffer(0);
                Graphics.SceneNoHudCaptured = Graphics.UpscaledThisFrame = Graphics.UpscaledCompositeReady = false;
                Graphics.TaaResolvedThisFrame = Graphics.GodRaysInScene = false;
                render(manager, eye == 0 ? delta : 0);
                Graphics.ComposeUiTarget();
                if (!ReferenceEquals(screen, ActiveScreen(manager)))
                {
                    Device.OpenXr!.SuppressFrame();
                    break; // Never submit a world in one eye and a new screen in the other.
                }
                Device.CopyOpenXrEye(eye);
                if (eye == 0 && eyeClient == null)
                {
                    Device.OpenXr!.UseHeadLockedMenu();
                    break; // A flat menu updates once and the compositor handles both eyes.
                }
            }
        }
        finally { RestoreEyeCamera(); OpenXrEye = -1; }
        // TODO(VR-01): replace the per-eye draw replay with multiview after terrain,
        // entities, post effects and mod passes all expose eye-indexed inputs.
    }

    internal void ApplyEyeCamera(ClientMain client)
    {
        var xr = Device.OpenXr!;
        var camera = client.MainCamera;
        if (OpenXrEye == 0 || !ReferenceEquals(eyeClient, client))
        {
            eyeClient = client;
            camera.CameraMatrix.CopyTo(centreCamera, 0);
            camera.CameraMatrixOrigin.CopyTo(centreOrigin, 0);
            Mat4d.Perspective(centreProjection, camera.Fov,
                (double)platform.WindowSize.Width / platform.WindowSize.Height, camera.ZNear, camera.ZFar);
            (centreEyeX, centreEyeY, centreEyeZ) = (camera.CameraEyePos.X, camera.CameraEyePos.Y, camera.CameraEyePos.Z);
        }
        OpenXrEyeView eye = xr.Eye(OpenXrEye);
        if (referenceRevision != xr.ReferenceRevision) { referenceReady = false; referenceRevision = xr.ReferenceRevision; }
        if (!referenceReady)
        {
            OpenXrEyeView left = xr.Eye(0), right = xr.Eye(1);
            referencePosition = (left.Position + right.Position) * .5f;
            Quaternion headOrientation = Quaternion.Normalize(Quaternion.Slerp(left.Orientation, right.Orientation, .5f));
            Vector3 forward = Vector3.Transform(-Vector3.UnitZ, headOrientation);
            float yaw = MathF.Atan2(-forward.X, -forward.Z);
            referenceRotation = Quaternion.Inverse(Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
            referenceReady = true;
        }
        // Game mouse yaw remains the locomotion/body heading. Physical head pitch
        // and roll come from XR, avoiding the ordinary camera's head-bob rotation.
        var baseView = new Matrix4x4((float)centreOrigin[0], (float)centreOrigin[1], (float)centreOrigin[2], 0,
            (float)centreOrigin[4], (float)centreOrigin[5], (float)centreOrigin[6], 0,
            (float)centreOrigin[8], (float)centreOrigin[9], (float)centreOrigin[10], 0, 0, 0, 0, 1);
        Matrix4x4.Invert(baseView, out var inverse);
        Vector3 bodyForward = Vector3.TransformNormal(-Vector3.UnitZ, inverse);
        bodyForward.Y = 0;
        bodyForward = bodyForward.LengthSquared() > .0001f ? Vector3.Normalize(bodyForward) : -Vector3.UnitZ;
        Quaternion body = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(-bodyForward.X, -bodyForward.Z));
        Vector3 offset = Vector3.Transform(Vector3.Transform(eye.Position - referencePosition, referenceRotation), body);
        Quaternion orientation = Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Concatenate(eye.Orientation, referenceRotation), body));
        double x = centreEyeX + offset.X, y = centreEyeY + offset.Y, z = centreEyeZ + offset.Z;
        camera.CameraEyePos.Set(x, y, z);
        FillView(camera.CameraMatrix, orientation, x, y, z);
        var origin = client.EntityPlayer.CameraPos;
        FillView(camera.CameraMatrixOrigin, orientation, x - origin.X, y - origin.Y, z - origin.Z);
        for (int index = 0; index < 16; index++) camera.CameraMatrixOriginf[index] = (float)camera.CameraMatrixOrigin[index];
        client.Set3DProjection(camera.ZFar, camera.Fov);
        // TODO(VR-01): sample poses again near submission, with per-frame owned UBO
        // storage and synchronization; never overwrite matrices already read by the GPU.
    }

    private static void FillView(double[] output, Quaternion orientation, double x, double y, double z)
    {
        Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(orientation));
        // System.Numerics row-vector matrices and the game's column-major arrays
        // have the same linear storage. Keep world translation in double precision.
        output[0] = rotation.M11; output[1] = rotation.M12; output[2] = rotation.M13; output[3] = 0;
        output[4] = rotation.M21; output[5] = rotation.M22; output[6] = rotation.M23; output[7] = 0;
        output[8] = rotation.M31; output[9] = rotation.M32; output[10] = rotation.M33; output[11] = 0;
        output[12] = -(x * output[0] + y * output[4] + z * output[8]);
        output[13] = -(x * output[1] + y * output[5] + z * output[9]);
        output[14] = -(x * output[2] + y * output[6] + z * output[10]);
        output[15] = 1;
    }

    internal void FillEyeProjection(double[] output, double near, double far)
    {
        // The game adapter uses OpenGL [-1,1] depth. The Vulkan shader path already
        // remaps it; copying a native Vulkan [0,1] tutorial matrix here would remap twice.
        var eye = Device.OpenXr!.Eye(OpenXrEye);
        double left = Math.Tan(eye.AngleLeft), right = Math.Tan(eye.AngleRight);
        double bottom = Math.Tan(eye.AngleDown), top = Math.Tan(eye.AngleUp);
        Array.Clear(output);
        output[0] = 2 / (right - left); output[5] = 2 / (top - bottom);
        output[8] = (right + left) / (right - left); output[9] = (top + bottom) / (top - bottom);
        output[10] = -(far + near) / (far - near); output[11] = -1;
        output[14] = -2 * far * near / (far - near);
    }

    private void RestoreEyeCamera()
    {
        if (eyeClient is not { } client) return;
        var camera = client.MainCamera;
        centreCamera.CopyTo(camera.CameraMatrix, 0);
        centreOrigin.CopyTo(camera.CameraMatrixOrigin, 0);
        centreProjection.CopyTo(OpenXrConsumerPatches.Perspective(client), 0);
        if (ReferenceEquals(Temporal.CurrentClient, client)) centreProjection.CopyTo(client.PMatrix.Top, 0);
        for (int index = 0; index < 16; index++) camera.CameraMatrixOriginf[index] = (float)centreOrigin[index];
        camera.CameraEyePos.Set(centreEyeX, centreEyeY, centreEyeZ);
        eyeClient = null;
    }
}
