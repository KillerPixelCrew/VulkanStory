using System.Runtime.CompilerServices;
using Vintagestory.API.Client;

namespace VulkanStory.Game;

// Retained per-object histories; baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
// Session-owned frame/draw state replaces the injected API static holders.
#nullable disable
    /// <summary>Session-owned animated draw history keyed weakly by bone-array identity; validates consecutive frames, joint counts and camera views.</summary>
    internal sealed class EntityMotionHistory(TemporalFrameState temporal, Func<int, (float Wind, float Water)> readWarp)
    {
        /// <summary>
        /// Whether the motion writers are compiled into the shaders at all. Set by
        /// ShaderRegistry from the same value it stamps TAAMOTION with, so the
        /// per-uniform hooks below cost one static bool read when TAA is off.
        /// </summary>
        public bool Enabled;
        internal long ValidAnimationUploads { get; private set; }
        internal long ModelCaptureSerial { get; private set; }

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
            public bool CurAmbiguous, PrevAmbiguous;

        }

        private readonly ConditionalWeakTable<object, Dictionary<(int Program, EnumTemporalView View), History>> histories = new();
        private sealed class DrawInputs
        {
            internal readonly float[] Model = new float[16];
            internal float UploadWind, UploadWater;
            internal long ModelFrame = -1, ModelSerial, Revision, UploadFrame = -1, UploadRevision;
            internal EnumTemporalView ModelView, UploadView;
            internal int UploadBuffer, UploadPreviousBuffer;
        }
        private readonly Dictionary<int, DrawInputs> inputs = new();
        private DrawInputs Inputs(int program)
        {
            if (!inputs.TryGetValue(program, out var state)) inputs.Add(program, state = new DrawInputs());
            return state;
        }
        internal (float Wind, float Water) CurrentWarp(int program) => readWarp(program);
        internal bool HasCurrentModel(int program) => inputs.TryGetValue(program, out var state) &&
            state.ModelFrame == temporal.FrameIndex && state.ModelView == temporal.ActiveView;
        // Borrowed capture for an immediate draw; the next accepted model write replaces its contents.
        internal bool TryGetCurrentModel(int program, long capturedAfter, out float[] model)
        {
            model = null;
            if (!inputs.TryGetValue(program, out var state) || state.ModelFrame != temporal.FrameIndex ||
                state.ModelView != temporal.ActiveView || state.ModelSerial <= capturedAfter) return false;
            model = state.Model;
            return true;
        }
        internal bool HasCurrentUpload(int program, int buffer, int previousBuffer) => HasCurrentModel(program) &&
            inputs[program].UploadFrame == temporal.FrameIndex && inputs[program].UploadView == temporal.ActiveView &&
            inputs[program].UploadRevision == inputs[program].Revision && inputs[program].UploadBuffer == buffer &&
            inputs[program].UploadPreviousBuffer == previousBuffer &&
            readWarp(program) == (inputs[program].UploadWind, inputs[program].UploadWater);
        internal void InvalidateUpload(int program)
        { if (inputs.TryGetValue(program, out var state)) state.UploadFrame = -1; }
        internal void InvalidateModel(int program)
        { if (inputs.TryGetValue(program, out var state)) { state.ModelFrame = -1; state.UploadFrame = -1; } }
        internal void InvalidateBuffer(int buffer)
        {
            foreach (var state in inputs.Values)
                if (state.UploadBuffer == buffer || state.UploadPreviousBuffer == buffer) state.UploadFrame = -1;
        }
        internal void ReleaseProgram(int program)
        {
            inputs.Remove(program);
            foreach (var entry in histories)
                foreach (var key in entry.Value.Keys.Where(key => key.Program == program).ToArray())
                { entry.Value.Remove(key); }
            if (sharedUniformProgram?.ProgramId == program) sharedUniformProgram = null;
        }
        internal void Clear()
        {
            inputs.Clear(); histories.Clear(); sharedUniformProgram = null; sharedUniformFrame = -1;
        }

        private IShaderProgram sharedUniformProgram;
        private long sharedUniformFrame = -1;
        private EnumTemporalView sharedUniformView;

        /// <summary>
        /// Remembers the model matrix a draw just set, so the upload that follows can
        /// store it as next frame's previous one. Called from ShaderProgramBase for
        /// the uniform named "modelMatrix" only.
        /// </summary>
        public void NoteModelMatrix(int program, ReadOnlySpan<float> matrix)
        {
            if (matrix.Length < 16) return;
            DrawInputs state = Inputs(program);
            if (!matrix[..16].SequenceEqual(state.Model)) state.Revision++;
            matrix[..16].CopyTo(state.Model);
            state.ModelSerial = ++ModelCaptureSerial;
            state.ModelFrame = temporal.FrameIndex;
            state.ModelView = temporal.ActiveView;
        }

        /// <summary>
        /// Called by the lib just before an entity's bone matrices reach the GPU.
        /// Uploads the same entity's previous pose into <paramref name="uploadPrevious" />
        /// and sets the writer's per-draw uniforms, then records this draw's state
        /// as next frame's previous one.
        /// </summary>
        /// <param name="program">The program in use, with a certified animated motion interface.</param>
        /// <param name="uploadPrevious">Its "AnimationPrev" uniform block.</param>
        /// <param name="boneMatrices">The array being uploaded into "Animation".</param>
        /// <param name="byteCount">How many bytes of it the draw uses.</param>
        public void OnAnimationUpload(IShaderProgram program, int bufferHandle, int previousBufferHandle,
            Action<float[], int> uploadPrevious, object boneMatrices, int byteCount)
        {
            if (!Enabled || program == null || uploadPrevious == null) return;
            if (!program.HasUniform("taaHistoryValid")) return;
            if (!HasCurrentModel(program.ProgramId)) return;
            DrawInputs state = inputs[program.ProgramId];
            var warp = readWarp(program.ProgramId);
            if (!float.IsFinite(warp.Wind) || !float.IsFinite(warp.Water)) return;

            float[] bones = boneMatrices as float[];
            if (bones == null || byteCount <= 0 || byteCount % 64 != 0) return;

            int floats = byteCount / 4;
            if (floats <= 0 || floats > bones.Length) return;

            TemporalFrameState frame = temporal;
            EnumTemporalView view = frame.ActiveView;
            var poses = histories.GetValue(bones, _ => new());
            var key = (program.ProgramId, view);
            if (!poses.TryGetValue(key, out var history)) poses.Add(key, history = new History());
            bool repeated = history.CapturedFrame == frame.FrameIndex;

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
                history.PrevAmbiguous = history.CurAmbiguous;
                history.CurAmbiguous = false;
            }

            // One pose array cannot identify several independently transformed objects.
            // Retain the pose, but decline its writer when a same-frame upload aliases transforms.
            if (repeated && (history.CurFloatCount != floats ||
                !state.Model.AsSpan().SequenceEqual(history.CurModelMatrix) ||
                history.CurWindWaveIntensity != warp.Wind || history.CurWaterWaveCounter != warp.Water ||
                !bones.AsSpan(0, floats).SequenceEqual(history.CurBones.AsSpan(0, floats)))) history.CurAmbiguous = true;

            if (history.CurBones.Length < floats) history.CurBones = new float[floats];
            Array.Copy(bones, history.CurBones, floats);
            history.CurFloatCount = floats;
            Array.Copy(state.Model, history.CurModelMatrix, 16);
            history.CurWindWaveIntensity = warp.Wind;
            history.CurWaterWaveCounter = warp.Water;
            history.CurView = view;

            // Valid only if the very same entity was drawn last frame, under the same
            // view (a first/third person switch changes both the FOV and the mesh),
            // with the same joint count, and the frame itself did not reset.
            bool valid =
                !frame.Reset &&
                !history.CurAmbiguous && !history.PrevAmbiguous &&
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
            if (!history.CurAmbiguous)
            {
                if (valid) ValidAnimationUploads++;
                state.UploadFrame = frame.FrameIndex;
                state.UploadView = view;
                state.UploadRevision = state.Revision;
                state.UploadBuffer = bufferHandle;
                state.UploadPreviousBuffer = previousBufferHandle;
                state.UploadWind = warp.Wind;
                state.UploadWater = warp.Water;
            }
        }
    }


    /// <summary>Tracks rigid draw transforms by stable object identity and rejects previous data after shape, view or frame discontinuity.</summary>
    internal sealed class StandardMotionHistory(TemporalFrameState temporal, EntityMotionHistory drawState,
        Action<string> rejectAmbiguous)
    {
        internal long ValidHistoryWrites { get; private set; }

        private readonly record struct GeometryPart(MeshRef Mesh, long Revision);

        private sealed class History
        {
            public readonly float[] PrevModelMatrix = new float[16];
            public readonly float[] CurModelMatrix = new float[16];

            /// <summary>The mesh wrapper and exact part geometry drawn in each frame.</summary>
            public object PrevShape;
            public object CurShape;
            public GeometryPart[] PrevGeometry = [];
            public GeometryPart[] CurGeometry = [];

            public float PrevWindWaveIntensity = 1f;
            public float CurWindWaveIntensity = 1f;
            public float PrevWaterWaveCounter;
            public float CurWaterWaveCounter;

            public EnumTemporalView PrevView;
            public EnumTemporalView CurView;

            public long CapturedFrame = -1;
            public long PreviousFrame = -1;
            public bool CurAmbiguous, PrevAmbiguous, FrameValidWritten;
        }

        private readonly ConditionalWeakTable<object, Dictionary<(int Program, EnumTemporalView View), History>> histories = new();

        private IShaderProgram sharedUniformProgram;
        private long sharedUniformFrame = -1;
        private EnumTemporalView sharedUniformView;

        internal void ReleaseProgram(int program)
        {
            foreach (var entry in histories)
                foreach (var key in entry.Value.Keys.Where(key => key.Program == program).ToArray()) entry.Value.Remove(key);
            if (sharedUniformProgram?.ProgramId == program) sharedUniformProgram = null;
        }

        internal void Clear()
        { histories.Clear(); sharedUniformProgram = null; sharedUniformFrame = -1; }

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
            if (!program.HasUniform("taaHistoryValid")) return false;
            TemporalFrameState frame = temporal;
            EnumTemporalView view = frame.ActiveView;
            var programs = histories.GetValue(identity, _ => new());
            var key = (program.ProgramId, view);
            if (!programs.TryGetValue(key, out var history)) programs.Add(key, history = new History());
            bool repeated = history.CapturedFrame == frame.FrameIndex;

            // One roll per frame, not per draw: something drawn twice in a frame must
            // both times compare against the frame before, not against its own first draw.
            if (history.CapturedFrame != frame.FrameIndex)
            {
                Array.Copy(history.CurModelMatrix, history.PrevModelMatrix, 16);
                history.PrevShape = history.CurShape;
                var geometrySwap = history.PrevGeometry;
                history.PrevGeometry = history.CurGeometry;
                history.CurGeometry = geometrySwap;
                history.PrevWindWaveIntensity = history.CurWindWaveIntensity;
                history.PrevWaterWaveCounter = history.CurWaterWaveCounter;
                history.PrevView = history.CurView;
                history.PreviousFrame = history.CapturedFrame;
                history.CapturedFrame = frame.FrameIndex;
                history.PrevAmbiguous = history.CurAmbiguous;
                history.CurAmbiguous = false;
                history.FrameValidWritten = false;
            }

            bool modelValid = modelMatrix != null && modelMatrix.Length >= 16;
            if (modelValid)
                for (int i = 0; i < 16; i++)
                    if (!float.IsFinite(modelMatrix[i])) { modelValid = false; break; }
            MeshRef singleMesh = shape as MeshRef;
            MeshRef[] meshParts = (shape as MultiTextureMeshRef)?.meshrefs;
            int partCount = singleMesh != null ? 1 : meshParts?.Length ?? 0;
            bool geometryValid = partCount > 0 && (shape is not MultiTextureMeshRef multi || !multi.Disposed);
            bool geometryMatchesCurrent = ReferenceEquals(history.CurShape, shape) && history.CurGeometry.Length == partCount;
            bool geometryMatchesPrevious = ReferenceEquals(history.PrevShape, shape) && history.PrevGeometry.Length == partCount;
            if (geometryValid)
                for (int i = 0; i < partCount; i++)
                {
                    MeshRef part = singleMesh ?? meshParts[i];
                    long revision = part == null || part.Disposed ? -1 : GameGraphicsAdapter.MeshGeometryRevision(part);
                    if (revision < 0) { geometryValid = false; break; }
                    if (geometryMatchesCurrent && (!ReferenceEquals(history.CurGeometry[i].Mesh, part) ||
                        history.CurGeometry[i].Revision != revision)) geometryMatchesCurrent = false;
                    if (geometryMatchesPrevious && (!ReferenceEquals(history.PrevGeometry[i].Mesh, part) ||
                        history.PrevGeometry[i].Revision != revision)) geometryMatchesPrevious = false;
                }
            var warp = drawState.CurrentWarp(program.ProgramId);
            if (!modelValid || !geometryValid || !float.IsFinite(warp.Wind) || !float.IsFinite(warp.Water))
            {
                bool newlyAmbiguous = !history.CurAmbiguous;
                history.CurAmbiguous = true;
                if (newlyAmbiguous && history.FrameValidWritten)
                    rejectAmbiguous?.Invoke("rigid object identity received invalid same-frame draw inputs");
                program.Uniform("taaHistoryValid", 0);
                if (program.HasUniform("taaReactive")) program.Uniform("taaReactive", 1f);
                return false;
            }

            // A stable object identity cannot describe independently transformed parts.
            // If an earlier valid draw already consumed this history, reject the frame's certification too.
            if (repeated && !history.CurAmbiguous &&
                (!modelMatrix.AsSpan(0, 16).SequenceEqual(history.CurModelMatrix) ||
                !geometryMatchesCurrent ||
                history.CurWindWaveIntensity != warp.Wind || history.CurWaterWaveCounter != warp.Water))
            {
                history.CurAmbiguous = true;
                if (history.FrameValidWritten) rejectAmbiguous?.Invoke("rigid object identity aliases multiple same-frame draws");
            }

            Array.Copy(modelMatrix, history.CurModelMatrix, 16);
            history.CurShape = shape;
            if (history.CurGeometry.Length != partCount) history.CurGeometry = new GeometryPart[partCount];
            for (int i = 0; i < partCount; i++)
            {
                MeshRef part = singleMesh ?? meshParts[i];
                history.CurGeometry[i] = new GeometryPart(part, GameGraphicsAdapter.MeshGeometryRevision(part));
            }
            history.CurWindWaveIntensity = warp.Wind;
            history.CurWaterWaveCounter = warp.Water;
            history.CurView = view;

            bool valid =
                !frame.Reset &&
                !history.CurAmbiguous && !history.PrevAmbiguous &&
                history.PreviousFrame == frame.FrameIndex - 1 &&
                geometryMatchesPrevious &&
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

            if (valid)
            {
                history.FrameValidWritten = true;
                ValidHistoryWrites++;
            }
            return valid;
        }
    }
