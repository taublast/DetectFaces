using DetectFaces.Services;
using DrawnUi.Camera;
using SkiaSharp;
using System.Diagnostics;
 

namespace CameraTests.UI
{
    public partial class AppCamera : SkiaCamera
    {
        /// <summary>
        /// Captures the newest preview frame for ML and feeds it into a coalescing detection pipeline.
        /// Only one preview detection is allowed to run at a time. If a frame arrives while detection
        /// is already in flight, this method keeps only the most recent pending request and drops older
        /// intermediate frames so overlay latency stays low.
        /// </summary>
        /// <param name="frame">Temporary raw-frame context. Use <see cref="RawCameraFrame.TryGetRgba"/> for AI input.</param>
        protected override void OnRawFrameAvailable(RawCameraFrame frame)
        {
            base.OnRawFrameAvailable(frame);

            if (!EnablePreviewDetection || Detector == null)
                return;

            PendingDetectionRequest? requestToSubmit = null;
            IFaceLandmarkDetector? detector = null;

            try
            {
                lock (_detectionSync)
                {
                    if (_stopDetectionWorker || Detector == null)
                        return;

                    int targetWidth;
                    int targetHeight;
                    int detectionRotation;
                    bool reusedCachedFrame = false;
                    int writeBufferIndex = _activeDetectionBufferIndex == 0 ? 1 : 0;
                    var resizeStopwatch = Stopwatch.StartNew();

                    if (ReuseFirstMlFrameForPreviewDetection && _hasCachedMlFrame)
                    {
                        targetWidth = _cachedMlWidth;
                        targetHeight = _cachedMlHeight;
                        detectionRotation = _cachedMlRotation;
                        reusedCachedFrame = true;
                    }
                    else
                    {
                        //app optimization logic
                        if (!PrepareReusableBuffers(frame, writeBufferIndex, out targetWidth, out targetHeight))
                            return;

                        //calling GPU SkiaCamera GPU helpers
                        if (!frame.TryGetRgba(targetWidth, targetHeight, _mlFrameBuffers[writeBufferIndex]))
                            return;

                        detectionRotation = 0;

                        if (ReuseFirstMlFrameForPreviewDetection)
                        {
                            _cachedMlWidth = targetWidth;
                            _cachedMlHeight = targetHeight;
                            _cachedMlRotation = 0;
                            _hasCachedMlFrame = true;
                        }
                    }

                    resizeStopwatch.Stop();

                    var request = new PendingDetectionRequest(
                        writeBufferIndex,
                        targetWidth,
                        targetHeight,
                        detectionRotation,
                        resizeStopwatch.Elapsed.TotalMilliseconds,
                        reusedCachedFrame);

                    if (_activeDetectionBufferIndex >= 0)
                    {
                        _queuedDetectionRequest = request;
                        return;
                    }

                    _activeDetectionBufferIndex = request.BufferIndex;
                    detector = Detector;
                    requestToSubmit = request;
                }

                SubmitPreviewDetection(detector, requestToSubmit);
            }
            catch
            {
                throw;
            }
        }

        #region Detection Configuration

        /// <summary>
        /// Performance control:
        ///
        /// Example:
        ///
        /// raw preview is 1280x720, MlMaxDimension = 192
        /// scaled ML frame becomes about 192x108
        /// 
        /// If raw preview is portrait 720x1280
        /// scaled ML frame becomes about 108x192
        ///
        /// </summary>
        /// <summary>
        /// Base maximum dimension for frames sent to the detector when no tracked face state is available.
        /// Higher values preserve more detail but increase detector input size and processing cost.
        /// </summary>
        private const int DefaultMlMaxDimension = 128;

        /// <summary>
        /// Reduced maximum detector input size used while one face is already tracked.
        /// Lower values cut reacquisition cost, but overly small values can reduce landmark stability.
        /// </summary>
        private const int DefaultTrackedSingleFaceMlMaxDimension = 112;

        /// <summary>
        /// Reduced maximum detector input size used while multiple faces are already tracked.
        /// This stays slightly larger than single-face mode to preserve more detail across several faces.
        /// </summary>
        private const int DefaultTrackedMultiFaceMlMaxDimension = 128;

        /// <summary>
        /// Default minimum confidence threshold for the face-detection stage.
        /// </summary>
        private const float DefaultMinFaceDetectionConfidence = 0.5f;

        /// <summary>
        /// Default minimum confidence threshold for the face-presence stage.
        /// </summary>
        private const float DefaultMinFacePresenceConfidence = 0.5f;

        /// <summary>
        /// Default minimum confidence threshold for landmark tracking.
        /// </summary>
        private const float DefaultMinTrackingConfidence = 0.5f;

        /// <summary>
        /// Time constant for overlay interpolation toward the latest detected landmarks.
        /// Higher values make masks smoother but laggier; lower values make them more responsive but twitchier.
        /// Values around 10-16 ms remove most visible landmark buzz while keeping the mask responsive.
        /// </summary>
        private const double DefaultOverlaySmoothingMs = 8;

        /// <summary>
        /// Normalized per-landmark deadzone applied before overlay interpolation.
        /// Tiny detector noise inside this threshold is ignored so a still face does not visibly tremble.
        /// </summary>
        private const float DefaultOverlayDeadzone = 0.007f;

        private readonly byte[][] _mlFrameBuffers = [Array.Empty<byte>(), Array.Empty<byte>()];
        private bool _hasCachedMlFrame;
        private int _cachedMlWidth;
        private int _cachedMlHeight;
        private int _cachedMlRotation;
        private readonly object _detectionSync = new();
        private bool _stopDetectionWorker;
        private int _activeDetectionBufferIndex = -1;
        private PendingDetectionRequest? _queuedDetectionRequest;

        private IFaceLandmarkDetector? _detector;

        /// <summary>
        /// Gets or sets the face-landmark detector used for still-image and live preview processing.
        /// Setting this property updates event subscriptions and pushes the current camera settings into
        /// the new detector instance.
        /// </summary>
        public IFaceLandmarkDetector? Detector
        {
            get => _detector;
            set
            {
                if (ReferenceEquals(_detector, value))
                    return;

                if (_detector != null)
                {
                    _detector.PreviewDetectionCompleted -= OnDetectorPreviewDetectionCompleted;
                    _detector.PreviewDetectionFailed -= OnDetectorPreviewDetectionFailed;
                }

                _detector = value;

                if (_detector != null)
                {
                    _detector.PreviewDetectionCompleted += OnDetectorPreviewDetectionCompleted;
                    _detector.PreviewDetectionFailed += OnDetectorPreviewDetectionFailed;
                }

                ApplyDetectorSettings();
            }
        }

        /// <summary>
        /// Gets or sets which detection overlay style should be rendered for faces.
        /// </summary>
        public DetectionType DrawMode { get; set; } = DetectionType.Landmark;

        /// <summary>
        /// Gets or sets whether live preview frames should be sent through the detector.
        /// </summary>
        public bool EnablePreviewDetection { get; set; } = true;

        /// <summary>
        /// Gets or sets whether preview detection should keep reusing the first prepared ML frame instead
        /// of resizing each incoming preview frame again.
        /// </summary>
        public bool ReuseFirstMlFrameForPreviewDetection { get; set; } = false;

        /// <summary>
        /// Gets or sets the maximum number of faces the detector should attempt to track.
        /// </summary>
        public int MaxNumFaces
        {
            get => _maxNumFaces;
            set
            {
                _maxNumFaces = Math.Max(1, value);
                ApplyDetectorSettings();
            }
        }

        /// <summary>
        /// Gets or sets the default maximum detector input dimension used when no faces are currently
        /// being tracked.
        /// </summary>
        public int MlMaxDimension { get; set; } = DefaultMlMaxDimension;

        /// <summary>
        /// Gets or sets the reduced detector input dimension used while one face is already tracked.
        /// </summary>
        public int TrackedSingleFaceMlMaxDimension { get; set; } = DefaultTrackedSingleFaceMlMaxDimension;

        /// <summary>
        /// Gets or sets the reduced detector input dimension used while multiple faces are already tracked.
        /// </summary>
        public int TrackedMultiFaceMlMaxDimension { get; set; } = DefaultTrackedMultiFaceMlMaxDimension;

        /// <summary>
        /// Gets or sets the minimum score required for the initial face-detection stage.
        /// Higher values reduce weak detections, lower values admit more tentative detections.
        /// </summary>
        public float MinFaceDetectionConfidence { get; set; } = DefaultMinFaceDetectionConfidence;

        /// <summary>
        /// Gets or sets the minimum score required for the face-presence stage after detection.
        /// Higher values demand more certainty that a tracked region still contains a face.
        /// </summary>
        public float MinFacePresenceConfidence { get; set; } = DefaultMinFacePresenceConfidence;

        /// <summary>
        /// Gets or sets the minimum score required for the tracker to keep following an existing face.
        /// Higher values make tracking stricter, lower values make reacquisition easier but noisier.
        /// </summary>
        public float MinTrackingConfidence { get; set; } = DefaultMinTrackingConfidence;

        /// <summary>
        /// Gets or sets the time constant used when interpolating overlay landmarks toward the latest
        /// detection.
        /// </summary>
        public double OverlaySmoothingMs { get; set; } = DefaultOverlaySmoothingMs;

        /// <summary>
        /// Gets or sets the normalized deadzone applied before tiny landmark movements are allowed to
        /// update the rendered overlay.
        /// </summary>
        public float OverlayDeadzone { get; set; } = DefaultOverlayDeadzone;

        /// <summary>
        /// Enables time-based interpolation between detector updates.
        /// Disable this temporarily to see the raw detector pose with only deadzone-based still-face stabilization.
        /// </summary>
        public bool EnableOverlayInterpolation { get; set; } = false;

        /// <summary>
        /// Raised when a preview detection result has been produced and published as the newest face state.
        /// </summary>
        public event EventHandler<FaceLandmarkResult>? PreviewDetectionUpdated;

        /// <summary>
        /// Raised with timing and sizing metrics for each completed preview-detection request.
        /// </summary>
        public event EventHandler<PreviewDetectionMetrics>? PreviewDetectionMeasured;

        /// <summary>
        /// Raised when a preview-detection request fails.
        /// </summary>
        public event EventHandler<Exception>? PreviewDetectionFailed;

        private int _maxNumFaces = 1;

        /// <summary>
        /// Pushes the current camera-side detection settings into the active detector instance.
        /// </summary>
        private void ApplyDetectorSettings()
        {
            if (_detector != null)
            {
                _detector.MaxFaces = _maxNumFaces;
                _detector.MinFaceDetectionConfidence = ClampConfidence(MinFaceDetectionConfidence);
                _detector.MinFacePresenceConfidence = ClampConfidence(MinFacePresenceConfidence);
                _detector.MinTrackingConfidence = ClampConfidence(MinTrackingConfidence);
            }
        }

        /// <summary>
        /// Normalizes user-entered confidence values into MediaPipe's supported <c>0..1</c> range.
        /// </summary>
        private static float ClampConfidence(float value)
        {
            return Math.Clamp(value, 0f, 1f);
        }

        #endregion

        #region Detection Pipeline

        /// <summary>
        /// Submits a prepared preview-detection request to the detector using the buffer selected in
        /// <see cref="OnRawFrameAvailable(RawCameraFrame)"/>. On submission failure, the active slot is
        /// released so a newer queued frame can still continue through the pipeline.
        /// </summary>
        /// <param name="detector">The detector instance that should process the request.</param>
        /// <param name="request">The prepared request describing the ML frame and timing metadata.</param>
        private void SubmitPreviewDetection(IFaceLandmarkDetector? detector, PendingDetectionRequest? request)
        {
            if (detector == null || request == null)
                return;

            try
            {
                detector.EnqueuePreviewDetection(
                    _mlFrameBuffers[request.BufferIndex],
                    new PreviewDetectionRequest(
                        request.Width,
                        request.Height,
                        request.Rotation,
                        request.ResizeMilliseconds,
                        request.ReusedCachedFrame,
                        Stopwatch.GetTimestamp()));
            }
            catch (Exception ex)
            {
                FinishPreviewDetection();
                MainThread.BeginInvokeOnMainThread(() => { PreviewDetectionFailed?.Invoke(this, ex); });
            }
        }

        /// <summary>
        /// Handles successful preview detection completion, publishes the latest detection snapshot and
        /// immediately advances the coalescing pipeline by calling <see cref="FinishPreviewDetection"/>.
        /// That follow-up call does not resubmit the completed request; it frees the active slot and, if
        /// a newer frame was captured while detection was running, starts that pending request next.
        /// </summary>
        /// <param name="sender">The detector that raised the completion event.</param>
        /// <param name="e">The completed detection result and request metadata.</param>
        private void OnDetectorPreviewDetectionCompleted(object? sender, PreviewDetectionCompletedEventArgs e)
        {
            if (!ReferenceEquals(sender, Detector))
                return;

            var completedAtTicks = Stopwatch.GetTimestamp();

            lock (_detectionSync)
            {
                _previousDetection = _latestDetection;
                _latestDetection = new DetectionSnapshot(e.Result, e.Request.Rotation, completedAtTicks);
            }

            var detectionMilliseconds =
                Stopwatch.GetElapsedTime(e.Request.EnqueuedAtTicks, completedAtTicks).TotalMilliseconds;

            //while we use maui view we need ui-thread, can remove after all is drawn
            MainThread.BeginInvokeOnMainThread(() =>
            {
                PreviewDetectionMeasured?.Invoke(this, new PreviewDetectionMetrics(
                    e.Request.ResizeMilliseconds,
                    detectionMilliseconds,
                    e.Request.ReusedCachedFrame,
                    e.Request.Width,
                    e.Request.Height,
                    e.Request.Rotation));
                PreviewDetectionUpdated?.Invoke(this, e.Result);
            });

            FinishPreviewDetection();
        }

        /// <summary>
        /// Handles preview detection failures. The current in-flight request is considered finished even
        /// on error, so the active slot is released and the newest queued request, if any, can continue.
        /// </summary>
        /// <param name="sender">The detector that raised the failure event.</param>
        /// <param name="e">The failed request metadata and the thrown exception.</param>
        private void OnDetectorPreviewDetectionFailed(object? sender, PreviewDetectionFailedEventArgs e)
        {
            if (!ReferenceEquals(sender, Detector))
                return;

            FinishPreviewDetection();

            MainThread.BeginInvokeOnMainThread(() => { PreviewDetectionFailed?.Invoke(this, e.Exception); });
        }

        /// <summary>
        /// Completes the current preview-detection slot and drains the single queued request, if present.
        /// This implements a latest-frame-wins queue of depth one: while one request is running, newer
        /// frames overwrite <c>_queuedDetectionRequest</c>; when the active request finishes, only that
        /// latest queued request is dispatched.
        /// </summary>
        private void FinishPreviewDetection()
        {
            PendingDetectionRequest? nextRequest = null;
            IFaceLandmarkDetector? detector = null;

            lock (_detectionSync)
            {
                _activeDetectionBufferIndex = -1;

                if (!_stopDetectionWorker && _queuedDetectionRequest != null && Detector != null)
                {
                    nextRequest = _queuedDetectionRequest;
                    _queuedDetectionRequest = null;
                    _activeDetectionBufferIndex = nextRequest.BufferIndex;
                    detector = Detector;
                }
            }

            SubmitPreviewDetection(detector, nextRequest);
        }

        /// <summary>
        /// Stops accepting new queued preview-detection work and clears any pending request that has not
        /// yet been submitted. An already running detector callback may still complete afterward.
        /// </summary>
        private void StopDetectionWorker()
        {
            lock (_detectionSync)
            {
                if (_stopDetectionWorker)
                    return;

                _stopDetectionWorker = true;
                _queuedDetectionRequest = null;
            }
        }

        /// <summary>
        /// Chooses the ML working size for the incoming frame and ensures the selected staging buffer is
        /// large enough to hold the converted RGBA pixels.
        /// </summary>
        /// <param name="frame">The source raw-frame context received from the camera.</param>
        /// <param name="bufferIndex">The index of the reusable ML buffer that should receive the frame.</param>
        /// <param name="targetWidth">Receives the scaled width chosen for ML processing.</param>
        /// <param name="targetHeight">Receives the scaled height chosen for ML processing.</param>
        /// <returns><see langword="true"/> when a valid ML frame size was prepared; otherwise <see langword="false"/>.</returns>
        private bool PrepareReusableBuffers(RawCameraFrame frame, int bufferIndex, out int targetWidth,
            out int targetHeight)
        {
            targetWidth = 0;
            targetHeight = 0;

            int sourceWidth = frame.SourceWidth;
            int sourceHeight = frame.SourceHeight;

            if (sourceWidth <= 0 || sourceHeight <= 0)
                return false;

            int maxDimension = ResolveCurrentMlMaxDimension();
            float scale = Math.Max(sourceWidth, sourceHeight) / (float)maxDimension;
            if (scale < 1f)
            {
                scale = 1f;
            }

            targetWidth = Math.Max(32, (int)Math.Round(sourceWidth / scale));
            targetHeight = Math.Max(32, (int)Math.Round(sourceHeight / scale));

            EnsureMlBufferSize(bufferIndex, targetWidth * targetHeight * 4);
            return true;
        }

        /// <summary>
        /// Resolves the detector input size to use for the next preview frame based on whether faces are
        /// already being tracked and how many are currently visible.
        /// </summary>
        /// <returns>The maximum dimension that should be used for the next ML frame.</returns>
        private int ResolveCurrentMlMaxDimension()
        {
            DetectionSnapshot? snapshot;
            lock (_detectionSync)
            {
                snapshot = _latestDetection;
            }

            int configuredMax = Math.Max(64, MlMaxDimension);
            if (snapshot?.Result?.Faces == null || snapshot.Result.Faces.Count == 0)
                return configuredMax;

            if (snapshot.Result.Faces.Count == 1)
                return Math.Min(configuredMax, Math.Max(64, TrackedSingleFaceMlMaxDimension));

            return Math.Min(configuredMax, Math.Max(64, TrackedMultiFaceMlMaxDimension));
        }

        /// <summary>
        /// Resizes the reusable ML staging buffer when its current capacity does not match the required
        /// byte count for the next converted frame.
        /// </summary>
        /// <param name="bufferIndex">The ML staging buffer to verify.</param>
        /// <param name="requiredBytes">The exact number of bytes needed for the converted frame.</param>
        private void EnsureMlBufferSize(int bufferIndex, int requiredBytes)
        {
            if (_mlFrameBuffers[bufferIndex].Length == requiredBytes)
                return;

            _mlFrameBuffers[bufferIndex] = new byte[requiredBytes];
        }

        #endregion

        #region Nested Types

        /// <summary>
        /// Immutable detector output captured at a specific completion time and orientation.
        /// </summary>
        public record DetectionSnapshot
        {
            /// <summary>
            /// Initializes an immutable raw detection snapshot captured from the detector callback.
            /// </summary>
            /// <param name="result">The detected face result payload.</param>
            /// <param name="rotation">The detector rotation associated with the result.</param>
            /// <param name="completedAtTicks">The completion timestamp in <see cref="Stopwatch"/> ticks.</param>
            public DetectionSnapshot(FaceLandmarkResult result, int rotation, long completedAtTicks)
            {
                Result = result;
                Rotation = rotation;
                CompletedAtTicks = completedAtTicks;
            }

            /// <summary>
            /// Gets the raw face-landmark result payload returned by the detector.
            /// </summary>
            public FaceLandmarkResult Result { get; }

            /// <summary>
            /// Gets the detector rotation associated with <see cref="Result"/>.
            /// </summary>
            public int Rotation { get; }

            /// <summary>
            /// Gets the completion timestamp of the detector callback in <see cref="Stopwatch"/> ticks.
            /// </summary>
            public long CompletedAtTicks { get; }
        }

        private sealed class RenderedDetectionState
        {
            /// <summary>
            /// Initializes mutable rendered detection state that can be filtered or interpolated between
            /// detector callbacks.
            /// </summary>
            /// <param name="faces">The mutable face list used by the overlay renderer.</param>
            /// <param name="rotation">The detector rotation associated with the state.</param>
            /// <param name="completedAtTicks">The timestamp of the source detection state.</param>
            public RenderedDetectionState(List<DetectedFace> faces, int rotation, long completedAtTicks)
            {
                Faces = faces;
                Rotation = rotation;
                CompletedAtTicks = completedAtTicks;
            }

            /// <summary>
            /// Gets the mutable face collection currently used by the overlay renderer.
            /// </summary>
            public List<DetectedFace> Faces { get; }

            /// <summary>
            /// Gets or sets the detector rotation associated with the rendered state.
            /// </summary>
            public int Rotation { get; set; }

            /// <summary>
            /// Gets or sets the timestamp of the source detection state in <see cref="Stopwatch"/> ticks.
            /// </summary>
            public long CompletedAtTicks { get; set; }
        }

        /// <summary>
        /// Describes one prepared preview-detection request stored in the coalescing queue.
        /// </summary>
        /// <param name="BufferIndex">The staging buffer index holding the converted ML frame.</param>
        /// <param name="Width">The prepared frame width in pixels.</param>
        /// <param name="Height">The prepared frame height in pixels.</param>
        /// <param name="Rotation">The detector rotation associated with the frame.</param>
        /// <param name="ResizeMilliseconds">The time spent resizing or preparing the ML frame.</param>
        /// <param name="ReusedCachedFrame">Whether the request reused the cached first ML frame.</param>
        private sealed record PendingDetectionRequest(
            int BufferIndex,
            int Width,
            int Height,
            int Rotation,
            double ResizeMilliseconds,
            bool ReusedCachedFrame);

        /// <summary>
        /// One Euro Filter: adaptive low-pass filter that smooths strongly when the signal
        /// is stationary (killing jitter) but adds minimal lag when it moves quickly.
        /// See: https://cristal.univ-lille.fr/~casiez/1euro/
        /// </summary>
        private sealed class OneEuroFilter
        {
            private readonly float _minCutoff;
            private readonly float _beta;
            private readonly float _dCutoff;
            private float _xPrev;
            private float _dxPrev;
            private long _lastTicks;

            /// <summary>
            /// Initializes a One Euro filter seeded with the first observed value.
            /// </summary>
            /// <param name="minCutoff">The minimum cutoff frequency in hertz.</param>
            /// <param name="beta">The speed coefficient that increases cutoff during fast movement.</param>
            /// <param name="dCutoff">The derivative cutoff frequency in hertz.</param>
            /// <param name="initialValue">The initial signal value.</param>
            /// <param name="initialTicks">The timestamp of the initial sample.</param>
            public OneEuroFilter(float minCutoff, float beta, float dCutoff, float initialValue, long initialTicks)
            {
                _minCutoff = minCutoff;
                _beta = beta;
                _dCutoff = dCutoff;
                _xPrev = initialValue;
                _dxPrev = 0;
                _lastTicks = initialTicks;
            }

            /// <summary>
            /// Filters a new scalar sample and returns the smoothed output.
            /// </summary>
            /// <param name="x">The new sample value.</param>
            /// <param name="ticks">The sample timestamp in <see cref="Stopwatch"/> ticks.</param>
            /// <returns>The filtered signal value.</returns>
            public float Filter(float x, long ticks)
            {
                var dtSeconds = Stopwatch.GetElapsedTime(_lastTicks, ticks).TotalSeconds;
                if (dtSeconds <= 1e-6)
                    return _xPrev;

                _lastTicks = ticks;
                var dt = (float)dtSeconds;

                // Filter the derivative to estimate speed
                var dx = (x - _xPrev) / dt;
                var alphaD = ComputeAlpha(dt, _dCutoff);
                _dxPrev = alphaD * dx + (1f - alphaD) * _dxPrev;

                // Adaptive cutoff: higher speed → higher cutoff → less smoothing
                var cutoff = _minCutoff + _beta * MathF.Abs(_dxPrev);

                // Filter the signal
                var alpha = ComputeAlpha(dt, cutoff);
                _xPrev = alpha * x + (1f - alpha) * _xPrev;

                return _xPrev;
            }

            /// <summary>
            /// Converts a cutoff frequency and time delta into the smoothing coefficient used by the
            /// One Euro filter's low-pass step.
            /// </summary>
            /// <param name="dt">The elapsed time in seconds since the previous sample.</param>
            /// <param name="cutoff">The active cutoff frequency in hertz.</param>
            /// <returns>The low-pass coefficient for the given sample interval.</returns>
            private static float ComputeAlpha(float dt, float cutoff)
            {
                var tau = 1f / (2f * MathF.PI * cutoff);
                return 1f / (1f + tau / dt);
            }
        }

        /// <summary>
        /// Reports timing, source, and prepared-frame information for a completed preview-detection pass.
        /// </summary>
        /// <param name="ResizeMilliseconds">The time spent preparing the detector input frame.</param>
        /// <param name="DetectionMilliseconds">The detector turnaround time from enqueue to completion.</param>
        /// <param name="ReusedCachedFrame">Whether the detector reused the cached first ML frame.</param>
        /// <param name="Width">The prepared detector input width in pixels.</param>
        /// <param name="Height">The prepared detector input height in pixels.</param>
        /// <param name="Rotation">The detector rotation associated with the prepared frame.</param>
        public record PreviewDetectionMetrics(
            double ResizeMilliseconds,
            double DetectionMilliseconds,
            bool ReusedCachedFrame,
            int Width,
            int Height,
            int Rotation);

        #endregion
    }
}
