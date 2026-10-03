using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace VulkanStory.Game;

// Retained 40-float instance layout and per-device/buffer history; baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
#nullable disable
    internal sealed class InstanceMotionHistory(TemporalFrameState temporal, EntityMotionHistory drawState)
    {
        /// <summary>Floats per instance: light rgba, transform, previous transform, TAA metadata.</summary>
        public const int InstanceFloats = 4 + 16 + 16 + 4;

        /// <summary>Where the light rgba starts within an instance, in floats.</summary>
        public const int LightOffset = 0;

        /// <summary>Where the current transform starts within an instance, in floats.</summary>
        public const int TransformOffset = 4;

        /// <summary>Where the previous transform starts within an instance, in floats.</summary>
        public const int PrevTransformOffset = 20;

        /// <summary>Where the TAA metadata vec4 starts within an instance, in floats.</summary>
        public const int MetaOffset = 36;

        private sealed class Entry
        {
            public float[] Prev = new float[16];
            public float[] Cur = new float[16];
            public EnumTemporalView PrevView;
            public EnumTemporalView CurView;
            public long CapturedFrame = -1;
            public long PreviousFrame = -1;
        }

        // Keyed on the instance buffer first, so a renderer that fills several
        // buffers for one device (the creative rotor's five sub-meshes, the
        // pulverizer's axle and pounders) keeps one history per sub-mesh; and on
        // the device object second, so it dies with the block entity behaviour.
        private readonly ConditionalWeakTable<float[], ConditionalWeakTable<object, Entry>> buffers =
            new ConditionalWeakTable<float[], ConditionalWeakTable<object, Entry>>();

        private object currentDevice;

        /// <summary>
        /// The instance-buffer layout instanced.vsh expects: rgbaLightIn at
        /// location 4, transform at 5..8, prevTransform at 9..12 and the TAA
        /// metadata at 13. Allocated whether or not TAA is on, because the shaders
        /// are recompiled when TAA is toggled but the instance buffers are not.
        /// </summary>
        /// <param name="instanceCapacity">How many instances the buffer must hold.</param>
        public CustomMeshDataPartFloat CreateInstanceFloats(int instanceCapacity)
        {
            int floats = InstanceFloats * instanceCapacity;
            CustomMeshDataPartFloat part = new CustomMeshDataPartFloat(floats)
            {
                Instanced = true,
                InterleaveOffsets = new int[] { 0, 16, 32, 48, 64, 80, 96, 112, 128, 144 },
                InterleaveSizes = new int[] { 4, 4, 4, 4, 4, 4, 4, 4, 4, 4 },
                InterleaveStride = InstanceFloats * 4,
                StaticDraw = false
            };
            part.SetAllocationSize(floats);
            return part;
        }

        /// <summary>
        /// Names the device whose instances are written next. Called once per
        /// device per frame by the renderer's buffer fill, before the transforms
        /// for that device reach any buffer.
        /// </summary>
        public void NoteDevice(object device)
        {
            currentDevice = device;
        }

        internal object ExchangeDevice(object device)
        {
            object previous = currentDevice;
            currentDevice = device;
            return previous;
        }

        /// <summary>
        /// Writes one instance: the light colour and this frame's transform as
        /// vanilla did, plus the same device's transform from the previous frame
        /// and the metadata that tells the shader whether to believe it.
        /// </summary>
        /// <param name="values">The instance buffer being filled.</param>
        /// <param name="index">The instance slot in that buffer.</param>
        /// <param name="lightRgba">The instance's light colour.</param>
        /// <param name="transform">This frame's 16-float transform.</param>
        public void WriteInstance(float[] values, int index, Vec4f lightRgba, float[] transform)
        {
            if (values == null || transform == null || index < 0) return;

            int j = index * InstanceFloats;
            if (j + InstanceFloats > values.Length) return;

            values[j + LightOffset] = lightRgba.R;
            values[j + LightOffset + 1] = lightRgba.G;
            values[j + LightOffset + 2] = lightRgba.B;
            values[j + LightOffset + 3] = lightRgba.A;

            for (int i = 0; i < 16; i++)
            {
                values[j + TransformOffset + i] = transform[i];
            }

            bool valid = false;
            object device = currentDevice;

            if (drawState.Enabled && device != null)
            {
                TemporalFrameState frame = temporal;
                EnumTemporalView view = frame.ActiveView;
                ConditionalWeakTable<object, Entry> entries =
                    buffers.GetValue(values, _ => new ConditionalWeakTable<object, Entry>());
                Entry entry = entries.GetValue(device, _ => new Entry());

                // One roll per frame, not per written instance: a device that
                // writes twice into the same buffer in one frame must both times
                // compare against the frame before, not against its own first write.
                if (entry.CapturedFrame != frame.FrameIndex)
                {
                    float[] swap = entry.Prev;
                    entry.Prev = entry.Cur;
                    entry.Cur = swap;
                    entry.PrevView = entry.CurView;
                    entry.PreviousFrame = entry.CapturedFrame;
                    entry.CapturedFrame = frame.FrameIndex;
                }

                for (int i = 0; i < 16; i++)
                {
                    entry.Cur[i] = transform[i];
                }
                entry.CurView = view;

                // The transform is camera-relative, so the previous one only means
                // anything together with the previous camera: the same device, drawn
                // into the same buffer, in the frame immediately before, under the
                // same view, in a frame that did not reset.
                valid =
                    !frame.Reset &&
                    entry.PreviousFrame == frame.FrameIndex - 1 &&
                    entry.PrevView == view &&
                    frame.WasViewCaptured(view);

                float[] previous = valid ? entry.Prev : entry.Cur;
                for (int i = 0; i < 16; i++)
                {
                    values[j + PrevTransformOffset + i] = previous[i];
                }
            }
            else
            {
                for (int i = 0; i < 16; i++)
                {
                    values[j + PrevTransformOffset + i] = transform[i];
                }
            }

            // x selects the branch in the vertex shader, y is the reactive value the
            // fragment writer stamps: a new or reordered instance has no history, so
            // the resolve is told to lean on this frame.
            values[j + MetaOffset] = valid ? 1f : 0f;
            values[j + MetaOffset + 1] = valid ? 0f : 1f;
            values[j + MetaOffset + 2] = 0f;
            values[j + MetaOffset + 3] = 0f;
        }

        /// <summary>
        /// Sets the per-pass half of the writer's uniforms: the previous camera and
        /// the shared warp/jitter/render-size block. Called once per frame by the
        /// renderer that owns the instanced program, after it has set this frame's
        /// projection and model-view matrices.
        /// </summary>
        public void ApplyPassUniforms(IShaderProgram program)
        {
            if (!drawState.Enabled || program == null) return;
            if (!program.HasUniform("prevProjectionMatrix")) return;

            TemporalFrameState frame = temporal;
            EnumTemporalView view = frame.ActiveView;

            program.UniformMatrix("prevProjectionMatrix", frame.GetPrevProjection(view));
            if (program.HasUniform("prevModelViewMatrix"))
            {
                program.UniformMatrix("prevModelViewMatrix", frame.PrevCameraMatrixOrigin);
            }
            frame.ApplyMotionUniforms(program);
        }
    }
