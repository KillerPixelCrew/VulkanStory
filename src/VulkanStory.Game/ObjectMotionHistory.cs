using System.Runtime.CompilerServices;
using Vintagestory.API.Client;

namespace VulkanStory.Game;

// Retained per-object histories; baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
// Session-owned frame/draw state replaces the injected API static holders.
#nullable disable
    /// <summary>Session-owned animated draw history keyed weakly by bone-array identity; validates consecutive frames, joint counts and camera views.</summary>
    internal sealed class EntityMotionHistory(TemporalFrameState temporal)
    {
        /// <summary>
        /// Whether the motion writers are compiled into the shaders at all. Set by
        /// ShaderRegistry from the same value it stamps TAAMOTION with, so the
        /// per-uniform hooks below cost one static bool read when TAA is off.
        /// </summary>
        public bool Enabled;

        private sealed class History
        {
            public float[] PrevBones = new float[0];
            public float[] CurBones = new float[0];
            public int PrevFloatCount = -1;
            public int CurFloatCount = -1;

            public readonly float[] PrevModelMatrix = new float[16];
            public readonly float[] CurModelMatrix = new float[16];

            public float PrevWindWaveIntensity = 1f;
            public float CurWindWaveIntensity = 1f;
            public float PrevWaterWaveCounter;
            public float CurWaterWaveCounter;

            public EnumTemporalView PrevView;
            public EnumTemporalView CurView;

            /// <summary>The frame CurBones et al. were captured in; -1 = never.</summary>
            public long CapturedFrame = -1;
            /// <summary>The frame PrevBones et al. were captured in; -1 = never.</summary>
            public long PreviousFrame = -1;
        }

        private readonly ConditionalWeakTable<object, History> histories = new ConditionalWeakTable<object, History>();

        private readonly float[] modelMatrixScratch = new float[16];
        private float windWaveIntensityScratch = 1f;
        private float waterWaveCounterScratch;

        /// <summary>
        /// The two per-draw warp uniforms as the draw in progress last set them.
        /// Shared with <see cref="StandardMotionHistory" />: the standard shader's
        /// users override the very same two names (a swimming dropped item sets
        /// waterWaveCounter, an entity sets windWaveIntensity), and both writers
        /// read them through the one recorder in ShaderProgramBase rather than
        /// growing a second hook.
        /// </summary>
        internal float ScratchWindWaveIntensity => windWaveIntensityScratch;

        /// <summary>See <see cref="ScratchWindWaveIntensity" />.</summary>
        internal float ScratchWaterWaveCounter => waterWaveCounterScratch;

        private IShaderProgram sharedUniformProgram;
        private long sharedUniformFrame = -1;
        private EnumTemporalView sharedUniformView;

        /// <summary>
        /// Remembers the model matrix a draw just set, so the upload that follows can
        /// store it as next frame's previous one. Called from ShaderProgramBase for
        /// the uniform named "modelMatrix" only.
        /// </summary>
        public void NoteModelMatrix(float[] matrix)
        {
            if (matrix == null || matrix.Length < 16) return;
            Array.Copy(matrix, modelMatrixScratch, 16);
        }
        public void NoteModelMatrix(ReadOnlySpan<float> matrix)
        {
            if (matrix.Length < 16) return;
            matrix[..16].CopyTo(modelMatrixScratch);
        }

        /// <summary>
        /// Remembers a warp uniform that varies per draw rather than per frame.
        /// EntityShapeRenderer overrides windWaveIntensity per entity and the echo
        /// chamber pins both to zero, so the previous frame's values for these two
        /// have to be stored per entity - the global PrevWarp would replay a warp the
        /// entity never had.
        /// </summary>
        public void NoteWarpUniform(string uniformName, float value)
        {
            if (uniformName == "windWaveIntensity") windWaveIntensityScratch = value;
            else if (uniformName == "waterWaveCounter") waterWaveCounterScratch = value;
        }

        /// <summary>
        /// Called by the lib just before an entity's bone matrices reach the GPU.
        /// Uploads the same entity's previous pose into <paramref name="uploadPrevious" />
        /// and sets the writer's per-draw uniforms, then records this draw's state
        /// as next frame's previous one.
        /// </summary>
        /// <param name="program">The program in use; must be an entityanimated motion writer.</param>
        /// <param name="uploadPrevious">Its "AnimationPrev" uniform block.</param>
        /// <param name="boneMatrices">The array being uploaded into "Animation".</param>
        /// <param name="byteCount">How many bytes of it the draw uses.</param>
        public void OnAnimationUpload(IShaderProgram program, Action<float[], int> uploadPrevious, object boneMatrices, int byteCount)
        {
            if (!Enabled || program == null || uploadPrevious == null) return;
            if (!program.HasUniform("taaHistoryValid")) return;

            float[] bones = boneMatrices as float[];
            if (bones == null || byteCount <= 0) return;

            int floats = byteCount / 4;
            if (floats <= 0 || floats > bones.Length) return;

            TemporalFrameState frame = temporal;
            EnumTemporalView view = frame.ActiveView;
            History history = histories.GetValue(bones, _ => new History());

            // One roll per frame, not per draw: an entity drawn twice in a frame
            // (opaque then after-OIT) must both times compare against the frame
            // before, not against its own first draw.
            if (history.CapturedFrame != frame.FrameIndex)
            {
                float[] swap = history.PrevBones;
                history.PrevBones = history.CurBones;
                history.CurBones = swap;
                history.PrevFloatCount = history.CurFloatCount;
                Array.Copy(history.CurModelMatrix, history.PrevModelMatrix, 16);
                history.PrevWindWaveIntensity = history.CurWindWaveIntensity;
                history.PrevWaterWaveCounter = history.CurWaterWaveCounter;
                history.PrevView = history.CurView;
                history.PreviousFrame = history.CapturedFrame;
                history.CapturedFrame = frame.FrameIndex;
            }

            if (history.CurBones.Length < floats) history.CurBones = new float[floats];
            Array.Copy(bones, history.CurBones, floats);
            history.CurFloatCount = floats;
            Array.Copy(modelMatrixScratch, history.CurModelMatrix, 16);
            history.CurWindWaveIntensity = windWaveIntensityScratch;
            history.CurWaterWaveCounter = waterWaveCounterScratch;
            history.CurView = view;

            // Valid only if the very same entity was drawn last frame, under the same
            // view (a first/third person switch changes both the FOV and the mesh),
            // with the same joint count, and the frame itself did not reset.
            bool valid =
                !frame.Reset &&
                history.PreviousFrame == frame.FrameIndex - 1 &&
                history.PrevFloatCount == floats &&
                history.PrevView == view &&
                frame.WasViewCaptured(view);

            uploadPrevious(valid ? history.PrevBones : history.CurBones, byteCount);

            // Per-frame, per-program half: the previous camera and the previous warp
            // state are the same for every entity in the pass.
            if (!ReferenceEquals(sharedUniformProgram, program) ||
                sharedUniformFrame != frame.FrameIndex ||
                sharedUniformView != view)
            {
                sharedUniformProgram = program;
                sharedUniformFrame = frame.FrameIndex;
                sharedUniformView = view;
                if (program.HasUniform("prevProjectionMatrix"))
                {
                    program.UniformMatrix("prevProjectionMatrix", frame.GetPrevProjection(view));
                }
                if (program.HasUniform("prevViewMatrix"))
                {
                    program.UniformMatrix("prevViewMatrix", frame.PrevCameraMatrixOrigin);
                }
                frame.ApplyMotionUniforms(program);
            }

            // Per-draw half.
            if (program.HasUniform("prevModelMatrix"))
            {
                program.UniformMatrix("prevModelMatrix", valid ? history.PrevModelMatrix : history.CurModelMatrix);
            }
            program.Uniform("taaHistoryValid", valid ? 1 : 0);
            if (program.HasUniform("taaReactive")) program.Uniform("taaReactive", valid ? 0f : 1f);
            if (program.HasUniform("prevWindWaveIntensity"))
            {
                program.Uniform("prevWindWaveIntensity", valid ? history.PrevWindWaveIntensity : history.CurWindWaveIntensity);
            }
            if (program.HasUniform("prevWaterWaveCounter"))
            {
                program.Uniform("prevWaterWaveCounter", valid ? history.PrevWaterWaveCounter : history.CurWaterWaveCounter);
            }
        }
    }


    /// <summary>Tracks rigid draw transforms by stable object identity and rejects previous data after shape, view or frame discontinuity.</summary>
    internal sealed class StandardMotionHistory(TemporalFrameState temporal, EntityMotionHistory drawState)
    {
        private sealed class History
        {
            public readonly float[] PrevModelMatrix = new float[16];
            public readonly float[] CurModelMatrix = new float[16];

            /// <summary>The mesh drawn last frame; a different one means a different shape.</summary>
            public object PrevShape;
            public object CurShape;

            public float PrevWindWaveIntensity = 1f;
            public float CurWindWaveIntensity = 1f;
            public float PrevWaterWaveCounter;
            public float CurWaterWaveCounter;

            public EnumTemporalView PrevView;
            public EnumTemporalView CurView;

            public long CapturedFrame = -1;
            public long PreviousFrame = -1;
        }

        private readonly ConditionalWeakTable<object, History> histories = new ConditionalWeakTable<object, History>();

        private IShaderProgram sharedUniformProgram;
        private long sharedUniformFrame = -1;
        private EnumTemporalView sharedUniformView;

        /// <summary>
        /// Feeds one standard-shader draw's previous transform to the writer.
        /// </summary>
        /// <param name="program">The standard-shader program in use, already active.</param>
        /// <param name="identity">A stable object that means "this drawn thing".</param>
        /// <param name="shape">The mesh about to be drawn; history is void when it changed.</param>
        /// <param name="modelMatrix">The model matrix this draw set, 16 floats.</param>
        /// <returns>Whether the writer got a usable previous transform.</returns>
        public bool Apply(IShaderProgram program, object identity, object shape, float[] modelMatrix)
        {
            if (!drawState.Enabled || program == null || identity == null) return false;
            if (modelMatrix == null || modelMatrix.Length < 16) return false;
            if (!program.HasUniform("taaHistoryValid")) return false;

            TemporalFrameState frame = temporal;
            EnumTemporalView view = frame.ActiveView;
            History history = histories.GetValue(identity, _ => new History());

            // One roll per frame, not per draw: something drawn twice in a frame must
            // both times compare against the frame before, not against its own first draw.
            if (history.CapturedFrame != frame.FrameIndex)
            {
                Array.Copy(history.CurModelMatrix, history.PrevModelMatrix, 16);
                history.PrevShape = history.CurShape;
                history.PrevWindWaveIntensity = history.CurWindWaveIntensity;
                history.PrevWaterWaveCounter = history.CurWaterWaveCounter;
                history.PrevView = history.CurView;
                history.PreviousFrame = history.CapturedFrame;
                history.CapturedFrame = frame.FrameIndex;
            }

            Array.Copy(modelMatrix, history.CurModelMatrix, 16);
            history.CurShape = shape;
            history.CurWindWaveIntensity = drawState.ScratchWindWaveIntensity;
            history.CurWaterWaveCounter = drawState.ScratchWaterWaveCounter;
            history.CurView = view;

            bool valid =
                !frame.Reset &&
                history.PreviousFrame == frame.FrameIndex - 1 &&
                ReferenceEquals(history.PrevShape, shape) &&
                history.PrevView == view &&
                frame.WasViewCaptured(view);

            // Per-frame, per-program half: the previous camera and the previous global
            // warp state are the same for every draw the pass makes.
            if (!ReferenceEquals(sharedUniformProgram, program) ||
                sharedUniformFrame != frame.FrameIndex ||
                sharedUniformView != view)
            {
                sharedUniformProgram = program;
                sharedUniformFrame = frame.FrameIndex;
                sharedUniformView = view;
                if (program.HasUniform("prevProjectionMatrix"))
                {
                    program.UniformMatrix("prevProjectionMatrix", frame.GetPrevProjection(view));
                }
                if (program.HasUniform("prevViewMatrix"))
                {
                    program.UniformMatrix("prevViewMatrix", frame.PrevCameraMatrixOrigin);
                }
                frame.ApplyMotionUniforms(program);
            }

            if (program.HasUniform("prevModelMatrix"))
            {
                program.UniformMatrix("prevModelMatrix", valid ? history.PrevModelMatrix : history.CurModelMatrix);
            }
            program.Uniform("taaHistoryValid", valid ? 1 : 0);
            if (program.HasUniform("taaReactive")) program.Uniform("taaReactive", valid ? 0f : 1f);
            if (program.HasUniform("prevWindWaveIntensity"))
            {
                program.Uniform("prevWindWaveIntensity", valid ? history.PrevWindWaveIntensity : history.CurWindWaveIntensity);
            }
            if (program.HasUniform("prevWaterWaveCounter"))
            {
                program.Uniform("prevWaterWaveCounter", valid ? history.PrevWaterWaveCounter : history.CurWaterWaveCounter);
            }

            return valid;
        }
    }
