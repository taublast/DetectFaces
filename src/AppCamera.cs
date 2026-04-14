using DrawnUi.Camera;
using SkiaSharp;
using System.Diagnostics;
using TestFaces.Services;
 

namespace CameraTests.UI
{
    public partial class AppCamera : SkiaCamera
    {
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
        private const int DefaultTrackedSingleFaceMlMaxDimension = 96;

        /// <summary>
        /// Reduced maximum detector input size used while multiple faces are already tracked.
        /// This stays slightly larger than single-face mode to preserve more detail across several faces.
        /// </summary>
        private const int DefaultTrackedMultiFaceMlMaxDimension = 112;

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
        private const double DefaultOverlaySmoothingMs = 16;

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

        /// <summary>
        /// Initializes the sample camera with audio, preview processing, and real-time frame processing
        /// enabled so face detection can run directly against incoming preview frames.
        /// </summary>
        public AppCamera()
        {
            //set defaults for this camera, we set base to be able to do video recording with sound
            NeedPermissionsSet = NeedPermissions.Camera | NeedPermissions.Gallery | NeedPermissions.Microphone;

            //GPS metadata
            //this.InjectGpsLocation = true;

            //audio 
            this.EnableAudioMonitoring = false;
            Facing = CameraPosition.Selfie;

            ProcessFrame = OnFrameProcessing;
            ProcessPreview = OnFrameProcessing;
            this.UseRealtimeVideoProcessing = true;

#if DEBUG
            VideoDiagnosticsOn = true;
#endif
        }

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

        private int _maxNumFaces = 2;

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

        /// <summary>
        /// Loads or clears the bitmap used for mask rendering so preview and recording overlays use the
        /// same already-decoded asset.
        /// </summary>
        /// <param name="config">The mask configuration to activate, or <see langword="null"/> to disable masks.</param>
        /// <returns>A task that completes once the mask asset has been loaded or cleared.</returns>
        public async Task SetMaskConfigurationAsync(MaskConfiguration? config)
        {
            ActiveMaskConfig = config;

            if (config == null || string.IsNullOrWhiteSpace(config.Filename))
            {
                var kill = MaskBitmap;
                MaskBitmap = null;
                DisposeObject(kill);
                return;
            }

            using var stream = await FileSystem.OpenAppPackageFileAsync(config.Filename);
            using var managed = new MemoryStream();
            await stream.CopyToAsync(managed);
            managed.Position = 0;

            var kill2 = MaskBitmap;
            MaskBitmap = SKBitmap.Decode(managed);
            DisposeObject(kill2);
        }




        /// <summary>
        /// Releases detector subscriptions and stops the preview-detection pipeline before base disposal.
        /// </summary>
        public override void OnDisposing()
        {
            Detector = null;
            StopDetectionWorker();
            base.OnDisposing();
        }

        /// <summary>
        /// Backing bindable property for <see cref="UseGain"/>.
        /// </summary>
        public static readonly BindableProperty UseGainProperty = BindableProperty.Create(
            nameof(UseGain),
            typeof(bool),
            typeof(AppCamera),
            false);

        /// <summary>
        /// Gets or sets whether microphone PCM samples should be amplified before they continue through
        /// the sample pipeline.
        /// </summary>
        public bool UseGain
        {
            get => (bool)GetValue(UseGainProperty);
            set => SetValue(UseGainProperty, value);
        }

        /// <summary>
        /// Gain multiplier applied to raw PCM when UseGain is true.
        /// </summary>
        public float GainFactor { get; set; } = 3.0f;



        /// <summary>
        /// Raised whenever a captured audio sample becomes available to the sample app.
        /// </summary>
        public event Action<AudioSample>? OnAudioSample; 

        /// <summary>
        /// Applies optional gain to captured PCM audio, forwards the sample to listeners, and then lets
        /// the base camera pipeline continue processing the same sample.
        /// </summary>
        /// <param name="sample">The audio sample received from the capture pipeline.</param>
        /// <returns>The sample that should continue through the base pipeline.</returns>
        protected override AudioSample OnAudioSampleAvailable(AudioSample sample)
        {
            if (UseGain && sample.Data != null && sample.Data.Length > 1)
            {
                AmplifyPcm16(sample.Data, GainFactor);
            }

            OnAudioSample?.Invoke(sample);

            return base.OnAudioSampleAvailable(sample);
        }

        /// <summary>
        /// Amplifies PCM16 audio data in-place. Zero allocations.
        /// </summary>
        private static void AmplifyPcm16(byte[] data, float gain)
        {
            for (int i = 0; i < data.Length - 1; i += 2)
            {
                int sample = (short)(data[i] | (data[i + 1] << 8));
                sample = (int)(sample * gain);

                // Clamp to 16-bit range
                if (sample > 32767) sample = 32767;
                else if (sample < -32768) sample = -32768;

                data[i] = (byte)(sample & 0xFF);
                data[i + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }

        /// <summary>
        /// Disposes paints, bitmaps, and filter state owned by this sample camera before the control tree
        /// is torn down.
        /// </summary>
        public override void OnWillDisposeWithChildren()
        {
            base.OnWillDisposeWithChildren();

            _paintRec?.Dispose();
            _paintRec = null;
            _paintPreview?.Dispose();
            _paintPreview = null;
            _detectionStrokePaint?.Dispose();
            _detectionStrokePaint = null;
            _detectionFillPaint?.Dispose();
            _detectionFillPaint = null;
            _maskPaint?.Dispose();
            _maskPaint = null;
            MaskBitmap?.Dispose();
            MaskBitmap = null;
            _filtersX = null;
            _filtersY = null;
        }

        /// <summary>
        /// Captures the newest preview frame for ML and feeds it into a coalescing detection pipeline.
        /// Only one preview detection is allowed to run at a time. If a frame arrives while detection
        /// is already in flight, this method keeps only the most recent pending request and drops older
        /// intermediate frames so overlay latency stays low.
        /// </summary>
        /// <param name="rawImage">The latest camera frame provided by <see cref="SkiaCamera"/>.</param>
        /// <param name="rotation">The rotation associated with <paramref name="rawImage"/>.</param>
        protected override void OnRawFrameAcquired(SKImage rawImage, int rotation)
        {
            base.OnRawFrameAcquired(rawImage, rotation);

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
                        if (!TryPrepareMlFrame(rawImage, writeBufferIndex, out targetWidth, out targetHeight))
                            return;

                        if (!TryGetMLFrame(rawImage, targetWidth, targetHeight, _mlFrameBuffers[writeBufferIndex]))
                            return;

                        detectionRotation = rotation;

                        if (ReuseFirstMlFrameForPreviewDetection)
                        {
                            _cachedMlWidth = targetWidth;
                            _cachedMlHeight = targetHeight;
                            _cachedMlRotation = rotation;
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

        /// <summary>
        /// Submits a prepared preview-detection request to the detector using the buffer selected in
        /// <see cref="OnRawFrameAcquired(SKImage, int)"/>. On submission failure, the active slot is
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
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    PreviewDetectionFailed?.Invoke(this, ex);
                });
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

            var detectionMilliseconds = Stopwatch.GetElapsedTime(e.Request.EnqueuedAtTicks, completedAtTicks).TotalMilliseconds;

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

            MainThread.BeginInvokeOnMainThread(() =>
            {
                PreviewDetectionFailed?.Invoke(this, e.Exception);
            });
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
        /// <param name="rawImage">The source image received from the camera, if available.</param>
        /// <param name="bufferIndex">The index of the reusable ML buffer that should receive the frame.</param>
        /// <param name="targetWidth">Receives the scaled width chosen for ML processing.</param>
        /// <param name="targetHeight">Receives the scaled height chosen for ML processing.</param>
        /// <returns><see langword="true"/> when a valid ML frame size was prepared; otherwise <see langword="false"/>.</returns>
        private bool TryPrepareMlFrame(SKImage? rawImage, int bufferIndex, out int targetWidth, out int targetHeight)
        {
            targetWidth = 0;
            targetHeight = 0;

            int sourceWidth = rawImage?.Width ?? 0;
            int sourceHeight = rawImage?.Height ?? 0;

            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                var format = CurrentVideoFormat;
                if (format != null)
                {
                    sourceWidth = format.Width;
                    sourceHeight = format.Height;
                }
            }

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

        /// <summary>
        /// Draws the sample's recording-state diagnostics directly into the current preview or recording
        /// frame.
        /// </summary>
        /// <param name="frame">The drawable camera frame that should receive the overlay content.</param>
        public void DrawOverlay(DrawableFrame frame)
        {
            SKPaint paint;
            var canvas = frame.Canvas;
            var width = frame.Width;
            var height = frame.Height;
            var scale = frame.Scale;

            if (frame.IsPreview)
            {
                if (_paintPreview == null)
                {
                    _paintPreview = new SKPaint
                    {
                        IsAntialias = true,
                    };
                }
                paint = _paintPreview;
            }
            else
            {
                if (_paintRec == null)
                {
                    _paintRec = new SKPaint
                    {
                        IsAntialias = true,
                    };
                }
                paint = _paintRec;
            }

            paint.TextSize = 48 * scale;
            paint.Color = IsPreRecording ? SKColors.White : SKColors.Red;
            paint.Style = SKPaintStyle.Fill;

            if (IsRecording || IsPreRecording)
            {
                // text at top left
                var text = IsPreRecording ? "PRE-RECORDED" : "LIVE";
                canvas.DrawText(text, 50 * scale, 100 * scale, paint);
                canvas.DrawText($"{frame.Time:mm\\:ss}", 50 * scale, 160 * scale, paint);

                // Draw a simple border around the frame
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 4 * scale;
                canvas.DrawRect(10 * scale, 10 * scale, width - 20 * scale, height - 20 * scale, paint);
            }
            else
            {
                paint.Color = SKColors.White;
                var text = $"PREVIEW {this.CaptureMode}";
                canvas.DrawText(text, 50 * scale, 100 * scale, paint);
            }

            //if (UseRealtimeVideoProcessing && EnableAudioRecording)
            //{
            //    _audioVisualizer?.Render(canvas, width, height, scale);
            //}
        }


        #region DRAWN LAYOUT

        private SKPaint? _paintPreview;
        private SKPaint? _paintRec;
        private SKPaint? _detectionStrokePaint;
        private SKPaint? _detectionFillPaint;
        private SKPaint? _maskPaint;
        private SKBitmap? MaskBitmap;
        private MaskConfiguration? ActiveMaskConfig;
        private DetectionSnapshot? _latestDetection;
        private DetectionSnapshot? _previousDetection;
        private RenderedDetectionState? _renderedDetection;
        private DetectionSnapshot? _lastConsumedTarget;
        private OneEuroFilter[]? _filtersX;
        private OneEuroFilter[]? _filtersY;
        private long _lastRenderedDetectionUpdateTicks;

        /// <summary>
        /// When true, landmark positions are extrapolated forward from the latest detection
        /// using the velocity between the two most recent detections. This compensates for
        /// MediaPipe's async pipeline latency (~65–85 ms) and makes overlays track moving
        /// faces much more closely. Set to false to observe raw (uncompensated) positions.
        /// </summary>
        public bool EnablePrediction { get; set; } = false;

        /// <summary>
        /// Enables One Euro Filter for landmark stabilization.
        /// Adaptively smooths landmarks: strong filtering when still, minimal lag when moving.
        /// When enabled, replaces the deadzone + interpolation smoothing path.
        /// </summary>
        public bool EnableOneEuroFilter { get; set; } = true;

        /// <summary>
        /// Minimum cutoff frequency for the One Euro Filter (Hz).
        /// Lower values = more smoothing when stationary. Range: 0.5–3.0.
        /// </summary>
        public float FilterMinCutoff { get; set; } = 1.0f;

        /// <summary>
        /// Speed coefficient for the One Euro Filter.
        /// Higher values = less lag during fast movement. Range: 0.0–1.0.
        /// </summary>
        public float FilterBeta { get; set; } = 15.0f;

        /// <summary>
        /// Derivative cutoff frequency for the One Euro Filter (Hz).
        /// </summary>
        public float FilterDCutoff { get; set; } = 1.0f;

        private DeviceOrientation _orientation;
        private float _previewScale;
        private float _renderedScale;
        private SKRect _rectFramePreview;
        private SKRect _rectFrameRecording;
        private float _overlayScale = 1;
        private float _overlayScaleChanged = -1;
        private VideoFormat? _adaptedToFormat;
        private DeviceOrientation _rectOrientation = DeviceOrientation.Unknown;
        private DeviceOrientation _rectOrientationLocked = DeviceOrientation.Unknown;
        /// <summary>
        /// Gets or sets whether overlay orientation should remain fixed instead of adapting to live device
        /// orientation changes while rendering.
        /// </summary>
        public bool LockOrientation { get; set; }

        /// <summary>
        /// Maps the current video format to a baseline divider used to keep DrawnUI overlay scaling
        /// visually consistent across different capture resolutions.
        /// </summary>
        /// <param name="smallSize">The smaller dimension of the current capture format.</param>
        /// <returns>A divider used when converting capture size into overlay scale.</returns>
        float GetOverlayBaseDivider(float smallSize)
        {
            var setting = smallSize;
            return setting switch
            {
                0 => 1920f,   // Default 1080p max dimension
                720 => 1280f,   // 1280x720 max
                1080 => 1920f,  // 1920x1080 max
                1440 => 2560f,  // 2560x1440 max
                2160 => 3840f,  // 3840x2160 max
                4320 => 7680f,  // 7680x4320 max
                _ => 1920f
            };
        }

        /// <summary>
        /// Recomputes the overlay scaling factor after a video format change so DrawnUI overlay layouts
        /// remain proportionate on preview and recorded frames.
        /// </summary>
        void AdjustOverlayScale()
        {
            var format = this.CurrentVideoFormat;
            if (format == null)
            {
                _overlayScaleChanged = -1;
                return;
            }
            var (formatWidth, formatHeight) = this.GetRotationCorrectedDimensions(format.Width, format.Height);
            var baseDivider = GetOverlayBaseDivider(Math.Min(format.Width, format.Height));
            _overlayScaleChanged = Math.Max(formatWidth, formatHeight) / baseDivider;
        }

        /// <summary>
        /// Renders DrawnUI overlay layouts, face overlays, and lightweight recording diagnostics for each
        /// preview or recording frame that passes through the camera pipeline.
        /// </summary>
        /// <param name="frame">The frame currently being processed for preview or recording output.</param>
        void OnFrameProcessing(DrawableFrame frame)
        {
            bool DrawOverlay(SkiaLayout layout, bool skipRendering)
            {

                if (this.CurrentVideoFormat != _adaptedToFormat)
                {
                    _adaptedToFormat = this.CurrentVideoFormat;
                    AdjustOverlayScale();
                }

                if (_overlayScale != _overlayScaleChanged)
                {
                    _overlayScale = _overlayScaleChanged;
                    _rectFramePreview = SKRect.Empty;
                    _rectFrameRecording = SKRect.Empty;
                }

                if (frame.IsPreview && frame.Scale != _previewScale)
                {
                    _rectFramePreview = SKRect.Empty;
                    _previewScale = frame.Scale;
                }

                var k = _overlayScale;
                var overlayScale = 3 * frame.Scale * k;

                if (_rectOrientation != _orientation && !LockOrientation)
                {
                    _rectFramePreview = SKRect.Empty;
                    _rectFrameRecording = SKRect.Empty;
                    _rectOrientation = _orientation;
                }

                var orientation = _rectOrientation;
                if (!LockOrientation)
                {
                    _rectOrientationLocked = DeviceOrientation.Unknown;
                }
                else
                {
                    if (_rectOrientationLocked == DeviceOrientation.Unknown)
                    {
                        //LOCK
                        _rectOrientationLocked = _rectOrientation;
                    }
                    orientation = _rectOrientationLocked;
                }

                var frameRect = new SKRect(0, 0, frame.Width, frame.Height); ;
                var rectLimits = frameRect;

                if (frame.IsPreview && _rectFramePreview == SKRect.Empty)
                {
                    _rectFramePreview = frameRect;
                    if (!layout.NeedMeasure)
                    {
                        layout.Invalidate();
                    }

                }
                else
                    if (!frame.IsPreview && _rectFrameRecording == SKRect.Empty)
                    {
                        _rectFrameRecording = frameRect;
                        if (!layout.NeedMeasure)
                        {
                            layout.Invalidate();
                        }
                    }

                if (orientation == DeviceOrientation.LandscapeLeft || orientation == DeviceOrientation.LandscapeRight)
                {
                    layout.AnchorX = 0;
                    layout.AnchorY = 0;

                    rectLimits = new SKRect(
                        rectLimits.Top,
                        rectLimits.Left,
                        rectLimits.Top + rectLimits.Height,
                        rectLimits.Left + rectLimits.Width
                    );
                }
                else
                {
                    layout.TranslationX = 0;
                    layout.TranslationY = 0;
                    layout.Rotation = 0;
                }

                //tune up a bit
                //overlayScale *= 0.9f;

                bool wasMeasured = false;

                if (layout.NeedMeasure)
                {
                    if (orientation == DeviceOrientation.LandscapeLeft || orientation == DeviceOrientation.LandscapeRight)
                    {
                        if (orientation == DeviceOrientation.LandscapeLeft)
                        {
                            layout.TranslationX = frameRect.Width / overlayScale - rectLimits.Left / overlayScale;
                            layout.TranslationY = rectLimits.Left / overlayScale; //rotated side offset
                            layout.Rotation = 90;
                        }
                        else // LandscapeRight
                        {
                            layout.TranslationX = -rectLimits.Left / overlayScale;
                            layout.TranslationY = frameRect.Height / overlayScale - rectLimits.Left / overlayScale;
                            layout.Rotation = -90;
                        }

                        var measured = layout.Measure(frameRect.Height, frameRect.Width, overlayScale);
                    }
                    else
                    {
                        var measured = layout.Measure(frameRect.Width, frameRect.Height, overlayScale);
                    }
                    layout.Arrange(
                        new SKRect(0, 0, layout.MeasuredSize.Pixels.Width, layout.MeasuredSize.Pixels.Height),
                        layout.MeasuredSize.Pixels.Width, layout.MeasuredSize.Pixels.Height, overlayScale);

                    wasMeasured = true;
                }

                var ctx = new SkiaDrawingContext()
                {
                    Canvas = frame.Canvas,
                    Width = frame.Width,
                    Height = frame.Height,
                    Superview = this.Superview  //to enable animations and use disposal manager
                };

                if (!skipRendering)
                {
                    layout.Render(new DrawingContext(ctx, rectLimits, overlayScale));
                    _renderedScale = overlayScale;
                }

                return wasMeasured;
            }

            // Simple text overlay for testing
            if (_paintRec == null)
            {
                _paintRec = new SKPaint
                {
                    IsAntialias = true,
                };
            }
            if (_paintPreview == null)
            {
                _paintPreview = new SKPaint
                {
                    IsAntialias = true,
                    Color = SKColors.Fuchsia
                };
            }

            var paint = frame.IsPreview ? _paintPreview : _paintRec;
            paint.TextSize = 32 * frame.Scale;
            paint.Style = SKPaintStyle.Fill;

            // text at top left
            var text = string.Empty;
            var text2 = string.Empty;

            if (this.IsPreRecording)
            {
                text = "PRE";
                text2 = $"{frame.Time:mm\\:ss}";
                paint.Color = SKColors.White;
            }
            else
            if (this.IsRecording)
            {
                text = "LIVE";
                text2 = $"{frame.Time:mm\\:ss}";
                paint.Color = SKColors.Red;
            }
            else
            {
                paint.Color = SKColors.Transparent;
                //text = $"{this.CurrentVideoFormat.Width}x{this.CurrentVideoFormat.Height} ({frame.Width}x{frame.Height}) x{_renderedScale:0.00}";
                //text = $"{frame.Width}x{frame.Height}";
            }

            //if (_labelRec != null)
            //{
            //    _labelRec.Text = CameraControl.IsPreRecording ? "PRE" : "REC";
            //}

            if (OverlayPreview != null && frame.IsPreview) //PREVIEW small frame
            {
                DrawOverlay(OverlayPreview, false);
            }
            else
            if (OverlayRecording != null && !frame.IsPreview) //RAW frame being recorded
            {
                DrawOverlay(OverlayRecording, false);
            }

            DrawDetectionOverlay(frame);

            //if (frame.IsPreview)
            {
                // draw frame indicator
                if (paint.Color != SKColors.Transparent)
                {
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 2 * frame.Scale;
                    frame.Canvas.DrawRect(10 * frame.Scale, 10 * frame.Scale, frame.Width - 20 * frame.Scale, frame.Height - 20 * frame.Scale, paint);
                }

                if (!string.IsNullOrEmpty(text))
                {
                    paint.TextSize = 48 * frame.Scale;
                    paint.Color = IsPreRecording ? SKColors.White : SKColors.Red;
                    paint.Style = SKPaintStyle.Fill;

                    if (IsRecording || IsPreRecording)
                    {
                        // text at top left
                        frame.Canvas.DrawText(text, 50 * frame.Scale, 100 * frame.Scale, paint);
                        frame.Canvas.DrawText(text2, 50 * frame.Scale, 160 * frame.Scale, paint);
                    }
                    else
                    {
                        paint.Color = SKColors.White;
                        frame.Canvas.DrawText(text, 50 * frame.Scale, 100 * frame.Scale, paint);
                    }
                }
            }


        }

        /// <summary>
        /// Draws face detection results as an overlay on the given frame. The appearance and smoothing of the overlay
        /// are determined by the current detection mode and filter settings.   
        /// </summary>
        /// <param name="frame"></param>
        private void DrawDetectionOverlay(DrawableFrame frame)
        {
            var rendered = GetRenderedDetectionState();
            if (rendered == null || rendered.Faces.Count == 0)
                return;

            EnsureDetectionPaints(frame.Scale);

            foreach (var face in rendered.Faces)
            {
                if (DrawMode == DetectionType.Rectangle)
                {
                    DrawFaceRectangle(frame, face, rendered.Rotation);
                }
                else if (DrawMode == DetectionType.Mask && MaskBitmap != null)
                {
                    DrawFaceMask(frame, face, rendered.Rotation);
                }
                else
                {
                    DrawFaceLandmarks(frame, face, rendered.Rotation);
                }
            }
        }

        /// <summary>
        /// Produces the rendered detection state used by the overlay renderer, applying prediction,
        /// deadzone logic, interpolation, and optional One Euro filtering as required by the current mode.
        /// </summary>
        /// <returns>The current rendered detection state, or <see langword="null"/> when there is nothing to draw.</returns>
        private RenderedDetectionState? GetRenderedDetectionState()
        {
            lock (_detectionSync)
            {
                var target = _latestDetection;
                if (target?.Result == null || target.Result.Faces.Count == 0)
                {
                    _renderedDetection = null;
                    _lastRenderedDetectionUpdateTicks = 0;
                    _lastConsumedTarget = null;
                    _filtersX = null;
                    _filtersY = null;
                    return null;
                }

                var nowTicks = Stopwatch.GetTimestamp();

                // Fast path: prediction is off, the detection hasn't changed, and smoothing
                // has already converged — return the cached result without any allocation.
                if (!EnablePrediction
                    && ReferenceEquals(target, _lastConsumedTarget)
                    && _renderedDetection != null)
                {
                    return _renderedDetection;
                }

                DetectionSnapshot? predictionPrevious = null;
                float predictionAlpha = 0f;
                var usePrediction = EnablePrediction
                    && TryComputePredictionAlpha(_previousDetection, target, nowTicks, out predictionPrevious, out predictionAlpha);

                // Extrapolate ahead of the latest detection using inter-detection velocity.
                // This compensates for pipeline latency so the overlay tracks the live face
                // rather than where it was when the frame was captured.
                // One Euro Filter path — smooth movement for masks (hats, face overlays).
                // Dots and rectangles use the zero-lag per-landmark deadzone path below.
                if (EnableOneEuroFilter && DrawMode == DetectionType.Mask)
                {
                    if (_renderedDetection == null || _filtersX == null || !CanSmoothRenderedDetection(_renderedDetection, target))
                    {
                        var predicted = usePrediction
                            ? ExtrapolateDetectionSnapshot(predictionPrevious!, target, predictionAlpha, nowTicks)
                            : target;
                        _renderedDetection = CreateRenderedDetectionState(predicted);
                        InitializeLandmarkFilters(_renderedDetection, nowTicks);
                        _lastRenderedDetectionUpdateTicks = nowTicks;
                        _lastConsumedTarget = target;
                        return _renderedDetection;
                    }

                    if (!EnablePrediction && ReferenceEquals(target, _lastConsumedTarget))
                        return _renderedDetection;

                    if (usePrediction)
                    {
                        ApplyPredictedOneEuroFilters(predictionPrevious!, target, predictionAlpha, nowTicks, _renderedDetection);
                    }
                    else
                    {
                        ApplyOneEuroFilters(target, _renderedDetection, nowTicks);
                    }

                    _lastRenderedDetectionUpdateTicks = nowTicks;
                    _lastConsumedTarget = target;
                    return _renderedDetection;
                }

                // Per-landmark deadzone path (zero-lag, for dots/rectangles)
                // Reset filter state so switching back to Mask starts fresh
                _filtersX = null;
                _filtersY = null;

                if (_renderedDetection == null || !CanSmoothRenderedDetection(_renderedDetection, target))
                {
                    var predicted = usePrediction
                        ? ExtrapolateDetectionSnapshot(predictionPrevious!, target, predictionAlpha, nowTicks)
                        : target;
                    _renderedDetection = CreateRenderedDetectionState(predicted);
                    _lastRenderedDetectionUpdateTicks = nowTicks;
                    _lastConsumedTarget = target;
                    return _renderedDetection;
                }

                if (!EnableOverlayInterpolation)
                {
                    if (usePrediction)
                    {
                        CopyPredictedWithPerLandmarkDeadzone(
                            predictionPrevious!,
                            target,
                            predictionAlpha,
                            nowTicks,
                            _renderedDetection,
                            OverlayDeadzone);
                    }
                    else
                    {
                        CopyWithPerLandmarkDeadzone(target, _renderedDetection, OverlayDeadzone);
                    }

                    _lastRenderedDetectionUpdateTicks = nowTicks;
                    _lastConsumedTarget = target;
                    return _renderedDetection;
                }

                var smoothingFactor = ComputeOverlaySmoothingFactor(nowTicks);
                if (smoothingFactor >= 1f)
                {
                    if (usePrediction)
                    {
                        CopyPredictedDetectionToRenderedState(predictionPrevious!, target, predictionAlpha, nowTicks, _renderedDetection);
                    }
                    else
                    {
                        CopyDetectionSnapshotToRenderedState(target, _renderedDetection);
                    }

                    _lastConsumedTarget = target; // mark converged so fast path can skip next frames
                }
                else if (smoothingFactor > 0f)
                {
                    if (usePrediction)
                    {
                        InterpolateRenderedStateTowardsPredictedSnapshot(
                            _renderedDetection,
                            predictionPrevious!,
                            target,
                            predictionAlpha,
                            nowTicks,
                            smoothingFactor,
                            OverlayDeadzone);
                    }
                    else
                    {
                        InterpolateRenderedStateTowardsSnapshot(_renderedDetection, target, smoothingFactor, OverlayDeadzone);
                    }
                }

                _lastRenderedDetectionUpdateTicks = nowTicks;
                return _renderedDetection;
            }
        }

        /// <summary>
        /// Computes the extrapolation factor for prediction without allocating a temporary predicted
        /// detection graph. The returned alpha can then be applied directly while updating the rendered
        /// landmark state in place.
        /// </summary>
        /// <param name="previous">The previous completed detection snapshot.</param>
        /// <param name="target">The latest completed detection snapshot.</param>
        /// <param name="nowTicks">The current render timestamp in <see cref="Stopwatch"/> ticks.</param>
        /// <param name="compatiblePrevious">Receives the compatible previous snapshot when prediction is possible.</param>
        /// <param name="alpha">Receives the extrapolation factor when prediction is possible.</param>
        /// <returns><see langword="true"/> when prediction should be applied; otherwise <see langword="false"/>.</returns>
        private static bool TryComputePredictionAlpha(
            DetectionSnapshot? previous,
            DetectionSnapshot target,
            long nowTicks,
            out DetectionSnapshot? compatiblePrevious,
            out float alpha)
        {
            compatiblePrevious = null;
            alpha = 0f;

            if (previous == null || !CanSmoothDetections(previous, target))
                return false;

            var sampleDtMs = Stopwatch.GetElapsedTime(previous.CompletedAtTicks, target.CompletedAtTicks).TotalMilliseconds;
            if (sampleDtMs <= 0)
                return false;

            var elapsedMs = Stopwatch.GetElapsedTime(target.CompletedAtTicks, nowTicks).TotalMilliseconds;
            if (elapsedMs <= 0)
                return false;

            var predictMs = Math.Min(elapsedMs, sampleDtMs * 1.5);
            alpha = (float)(predictMs / sampleDtMs);
            if (alpha < 0.01f)
            {
                alpha = 0f;
                return false;
            }

            compatiblePrevious = previous;
            return true;
        }

        /// <summary>
        /// Extrapolates landmark positions from <paramref name="target"/> using the velocity
        /// computed between <paramref name="previous"/> and <paramref name="target"/>.
        /// The extrapolation distance equals the time elapsed since <paramref name="target"/>
        /// was delivered, capped at two detection intervals to limit overshoot.
        /// </summary>
        private static DetectionSnapshot ComputePredictedDetection(
            DetectionSnapshot? previous,
            DetectionSnapshot target,
            long nowTicks)
        {
            if (previous == null || !CanSmoothDetections(previous, target))
                return target;

            var sampleDtMs = Stopwatch.GetElapsedTime(previous.CompletedAtTicks, target.CompletedAtTicks).TotalMilliseconds;
            if (sampleDtMs <= 0)
                return target;

            var elapsedMs = Stopwatch.GetElapsedTime(target.CompletedAtTicks, nowTicks).TotalMilliseconds;
            if (elapsedMs <= 0)
                return target;

            // Cap at 2× detection interval to avoid extreme overshoot on direction changes.
            var predictMs = Math.Min(elapsedMs, sampleDtMs * 1.5); //todo tune down to 1.0
            var alpha = (float)(predictMs / sampleDtMs);
            if (alpha < 0.01f)
                return target;

            return ExtrapolateDetectionSnapshot(previous, target, alpha, nowTicks);
        }

        /// <summary>
        /// Extrapolates beyond <paramref name="target"/> by <paramref name="alpha"/> multiples
        /// of the (previous → target) delta: predicted = target + alpha * (target − previous).
        /// </summary>
        private static DetectionSnapshot ExtrapolateDetectionSnapshot(
            DetectionSnapshot previous,
            DetectionSnapshot target,
            float alpha,
            long completedAtTicks)
        {
            var faces = new List<DetectedFace>(target.Result.Faces.Count);
            for (int f = 0; f < target.Result.Faces.Count; f++)
            {
                var prevFace = previous.Result.Faces[f];
                var targetFace = target.Result.Faces[f];
                var points = new List<NormalizedPoint>(targetFace.Landmarks.Count);

                for (int i = 0; i < targetFace.Landmarks.Count; i++)
                {
                    var p = prevFace.Landmarks[i];
                    var t = targetFace.Landmarks[i];
                    points.Add(new NormalizedPoint(
                        t.X + alpha * (t.X - p.X),
                        t.Y + alpha * (t.Y - p.Y)));
                }

                faces.Add(new DetectedFace { Landmarks = points });
            }

            return new DetectionSnapshot(
                new FaceLandmarkResult
                {
                    Faces = faces,
                    ImageWidth = target.Result.ImageWidth,
                    ImageHeight = target.Result.ImageHeight,
                    ConversionMilliseconds = target.Result.ConversionMilliseconds,
                    InferenceMilliseconds = target.Result.InferenceMilliseconds,
                    UsedGpuDelegate = target.Result.UsedGpuDelegate,
                },
                target.Rotation,
                completedAtTicks);
        }

        /// <summary>
        /// Copies predicted landmark positions directly into the rendered state without allocating an
        /// intermediate predicted detection snapshot.
        /// </summary>
        /// <param name="previous">The previous completed detection snapshot.</param>
        /// <param name="target">The latest completed detection snapshot.</param>
        /// <param name="alpha">The extrapolation factor returned by <see cref="TryComputePredictionAlpha"/>.</param>
        /// <param name="completedAtTicks">The render-time timestamp assigned to the predicted state.</param>
        /// <param name="destination">The rendered state to overwrite.</param>
        private static void CopyPredictedDetectionToRenderedState(
            DetectionSnapshot previous,
            DetectionSnapshot target,
            float alpha,
            long completedAtTicks,
            RenderedDetectionState destination)
        {
            destination.Rotation = target.Rotation;
            destination.CompletedAtTicks = completedAtTicks;

            for (int faceIndex = 0; faceIndex < destination.Faces.Count; faceIndex++)
            {
                var destinationLandmarks = destination.Faces[faceIndex].Landmarks;
                var previousLandmarks = previous.Result.Faces[faceIndex].Landmarks;
                var targetLandmarks = target.Result.Faces[faceIndex].Landmarks;

                for (int landmarkIndex = 0; landmarkIndex < destinationLandmarks.Count; landmarkIndex++)
                {
                    var prev = previousLandmarks[landmarkIndex];
                    var next = targetLandmarks[landmarkIndex];
                    destinationLandmarks[landmarkIndex] = new NormalizedPoint(
                        PredictValue(prev.X, next.X, alpha),
                        PredictValue(prev.Y, next.Y, alpha));
                }
            }
        }

        /// <summary>
        /// Converts elapsed time since the last rendered update into an exponential interpolation factor
        /// for smooth overlay transitions.
        /// </summary>
        /// <param name="nowTicks">The current timestamp in <see cref="Stopwatch"/> ticks.</param>
        /// <returns>A normalized interpolation factor in the range from 0 to 1.</returns>
        private float ComputeOverlaySmoothingFactor(long nowTicks)
        {
            if (OverlaySmoothingMs <= 0)
                return 1f;

            if (_lastRenderedDetectionUpdateTicks == 0)
                return 1f;

            var elapsedMs = Stopwatch.GetElapsedTime(_lastRenderedDetectionUpdateTicks, nowTicks).TotalMilliseconds;
            if (elapsedMs <= 0)
                return 0f;

            return (float)(1d - Math.Exp(-elapsedMs / OverlaySmoothingMs));
        }

        /// <summary>
        /// Checks whether two raw detection snapshots are structurally compatible for smoothing or
        /// prediction.
        /// </summary>
        /// <param name="current">The earlier detection snapshot.</param>
        /// <param name="target">The later detection snapshot.</param>
        /// <returns><see langword="true"/> when both snapshots can be blended landmark-by-landmark.</returns>
        private static bool CanSmoothDetections(DetectionSnapshot current, DetectionSnapshot target)
        {
            if (current.Rotation != target.Rotation)
                return false;

            if (current.Result.Faces.Count != target.Result.Faces.Count)
                return false;

            for (int faceIndex = 0; faceIndex < current.Result.Faces.Count; faceIndex++)
            {
                if (current.Result.Faces[faceIndex].Landmarks.Count != target.Result.Faces[faceIndex].Landmarks.Count)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Checks whether the current rendered state and a new detection snapshot have compatible shape
        /// so one can be updated toward the other in place.
        /// </summary>
        /// <param name="current">The already-rendered detection state.</param>
        /// <param name="target">The incoming detection snapshot.</param>
        /// <returns><see langword="true"/> when both states describe the same landmark topology.</returns>
        private static bool CanSmoothRenderedDetection(RenderedDetectionState current, DetectionSnapshot target)
        {
            if (current.Rotation != target.Rotation)
                return false;

            if (current.Faces.Count != target.Result.Faces.Count)
                return false;

            for (int faceIndex = 0; faceIndex < current.Faces.Count; faceIndex++)
            {
                if (current.Faces[faceIndex].Landmarks.Count != target.Result.Faces[faceIndex].Landmarks.Count)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether every landmark in the target snapshot remains inside the configured deadzone
        /// relative to the currently rendered state.
        /// </summary>
        /// <param name="current">The currently rendered landmark state.</param>
        /// <param name="target">The candidate detection snapshot.</param>
        /// <param name="deadzone">The per-axis normalized deadzone threshold.</param>
        /// <returns><see langword="true"/> when all landmarks remain inside the deadzone.</returns>
        private static bool IsRenderedDetectionWithinDeadzone(RenderedDetectionState current, DetectionSnapshot target, float deadzone)
        {
            if (deadzone <= 0f)
                return false;

            if (!CanSmoothRenderedDetection(current, target))
                return false;

            for (int faceIndex = 0; faceIndex < current.Faces.Count; faceIndex++)
            {
                var currentFace = current.Faces[faceIndex];
                var targetFace = target.Result.Faces[faceIndex];

                for (int landmarkIndex = 0; landmarkIndex < currentFace.Landmarks.Count; landmarkIndex++)
                {
                    var currentPoint = currentFace.Landmarks[landmarkIndex];
                    var targetPoint = targetFace.Landmarks[landmarkIndex];

                    if (Math.Abs(targetPoint.X - currentPoint.X) > deadzone
                        || Math.Abs(targetPoint.Y - currentPoint.Y) > deadzone)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Clones a raw detection snapshot into mutable overlay state that can then be interpolated or
        /// filtered across subsequent frames.
        /// </summary>
        /// <param name="snapshot">The snapshot to clone.</param>
        /// <returns>A mutable rendered state copy of <paramref name="snapshot"/>.</returns>
        private static RenderedDetectionState CreateRenderedDetectionState(DetectionSnapshot snapshot)
        {
            var faces = new List<DetectedFace>(snapshot.Result.Faces.Count);
            foreach (var sourceFace in snapshot.Result.Faces)
            {
                var points = new List<NormalizedPoint>(sourceFace.Landmarks.Count);
                foreach (var point in sourceFace.Landmarks)
                {
                    points.Add(point);
                }

                faces.Add(new DetectedFace { Landmarks = points });
            }

            return new RenderedDetectionState(faces, snapshot.Rotation, snapshot.CompletedAtTicks);
        }

        /// <summary>
        /// Copies a raw detection snapshot into an existing rendered state without reallocating landmark
        /// collections.
        /// </summary>
        /// <param name="source">The source detection snapshot.</param>
        /// <param name="destination">The rendered state to overwrite.</param>
        private static void CopyDetectionSnapshotToRenderedState(DetectionSnapshot source, RenderedDetectionState destination)
        {
            destination.Rotation = source.Rotation;
            destination.CompletedAtTicks = source.CompletedAtTicks;

            for (int faceIndex = 0; faceIndex < destination.Faces.Count; faceIndex++)
            {
                var destinationLandmarks = destination.Faces[faceIndex].Landmarks;
                var sourceLandmarks = source.Result.Faces[faceIndex].Landmarks;
                for (int landmarkIndex = 0; landmarkIndex < destinationLandmarks.Count; landmarkIndex++)
                {
                    destinationLandmarks[landmarkIndex] = sourceLandmarks[landmarkIndex];
                }
            }
        }

        /// <summary>
        /// Moves each rendered landmark toward the target snapshot using exponential interpolation while
        /// honoring the configured deadzone.
        /// </summary>
        /// <param name="current">The rendered state to mutate.</param>
        /// <param name="target">The target snapshot to move toward.</param>
        /// <param name="amount">The interpolation amount in the range from 0 to 1.</param>
        /// <param name="deadzone">The per-axis normalized deadzone threshold.</param>
        private static void InterpolateRenderedStateTowardsSnapshot(RenderedDetectionState current, DetectionSnapshot target, float amount, float deadzone)
        {
            current.Rotation = target.Rotation;
            current.CompletedAtTicks = target.CompletedAtTicks;

            for (int faceIndex = 0; faceIndex < target.Result.Faces.Count; faceIndex++)
            {
                var currentFace = current.Faces[faceIndex];
                var targetFace = target.Result.Faces[faceIndex];

                for (int landmarkIndex = 0; landmarkIndex < targetFace.Landmarks.Count; landmarkIndex++)
                {
                    var from = currentFace.Landmarks[landmarkIndex];
                    var to = targetFace.Landmarks[landmarkIndex];

                    float targetX = ApplyDeadzone(from.X, to.X, deadzone);
                    float targetY = ApplyDeadzone(from.Y, to.Y, deadzone);

                    currentFace.Landmarks[landmarkIndex] = new NormalizedPoint(
                        Lerp(from.X, targetX, amount),
                        Lerp(from.Y, targetY, amount));
                }
            }
        }

        /// <summary>
        /// Moves each rendered landmark toward a predicted landmark position computed from the previous
        /// and current detector snapshots, avoiding allocation of a temporary predicted snapshot.
        /// </summary>
        /// <param name="current">The rendered state to mutate.</param>
        /// <param name="previous">The previous completed detection snapshot.</param>
        /// <param name="target">The latest completed detection snapshot.</param>
        /// <param name="alpha">The extrapolation factor returned by <see cref="TryComputePredictionAlpha"/>.</param>
        /// <param name="completedAtTicks">The render-time timestamp assigned to the predicted state.</param>
        /// <param name="amount">The interpolation amount in the range from 0 to 1.</param>
        /// <param name="deadzone">The per-axis normalized deadzone threshold.</param>
        private static void InterpolateRenderedStateTowardsPredictedSnapshot(
            RenderedDetectionState current,
            DetectionSnapshot previous,
            DetectionSnapshot target,
            float alpha,
            long completedAtTicks,
            float amount,
            float deadzone)
        {
            current.Rotation = target.Rotation;
            current.CompletedAtTicks = completedAtTicks;

            for (int faceIndex = 0; faceIndex < target.Result.Faces.Count; faceIndex++)
            {
                var currentFace = current.Faces[faceIndex];
                var previousFace = previous.Result.Faces[faceIndex];
                var targetFace = target.Result.Faces[faceIndex];

                for (int landmarkIndex = 0; landmarkIndex < targetFace.Landmarks.Count; landmarkIndex++)
                {
                    var from = currentFace.Landmarks[landmarkIndex];
                    var prev = previousFace.Landmarks[landmarkIndex];
                    var next = targetFace.Landmarks[landmarkIndex];

                    float predictedX = PredictValue(prev.X, next.X, alpha);
                    float predictedY = PredictValue(prev.Y, next.Y, alpha);
                    float targetX = ApplyDeadzone(from.X, predictedX, deadzone);
                    float targetY = ApplyDeadzone(from.Y, predictedY, deadzone);

                    currentFace.Landmarks[landmarkIndex] = new NormalizedPoint(
                        Lerp(from.X, targetX, amount),
                        Lerp(from.Y, targetY, amount));
                }
            }
        }

        /// <summary>
        /// Linearly interpolates between two scalar values.
        /// </summary>
        /// <param name="from">The starting value.</param>
        /// <param name="to">The target value.</param>
        /// <param name="amount">The interpolation amount in the range from 0 to 1.</param>
        /// <returns>The interpolated value.</returns>
        private static float Lerp(float from, float to, float amount)
        {
            return from + ((to - from) * amount);
        }

        /// <summary>
        /// Extrapolates one scalar coordinate from the previous detector value toward the target value.
        /// </summary>
        /// <param name="previous">The previous detector value.</param>
        /// <param name="target">The latest detector value.</param>
        /// <param name="alpha">The extrapolation factor.</param>
        /// <returns>The predicted coordinate value.</returns>
        private static float PredictValue(float previous, float target, float alpha)
        {
            return target + alpha * (target - previous);
        }

        /// <summary>
        /// Applies a deadzone to a scalar value so tiny movements are ignored and the current value is
        /// preserved.
        /// </summary>
        /// <param name="current">The current rendered value.</param>
        /// <param name="target">The candidate target value.</param>
        /// <param name="deadzone">The threshold under which the current value should be kept.</param>
        /// <returns>The kept or updated value after deadzone evaluation.</returns>
        private static float ApplyDeadzone(float current, float target, float deadzone)
        {
            return Math.Abs(target - current) <= deadzone ? current : target;
        }

        /// <summary>
        /// Copies a detection snapshot into the rendered state using independent deadzone checks per
        /// landmark axis so still faces remain visually stable without interpolation lag.
        /// </summary>
        /// <param name="source">The source detection snapshot.</param>
        /// <param name="destination">The rendered state to update in place.</param>
        /// <param name="deadzone">The per-axis normalized deadzone threshold.</param>
        private static void CopyWithPerLandmarkDeadzone(DetectionSnapshot source, RenderedDetectionState destination, float deadzone)
        {
            destination.Rotation = source.Rotation;
            destination.CompletedAtTicks = source.CompletedAtTicks;

            for (int f = 0; f < destination.Faces.Count; f++)
            {
                var dstLandmarks = destination.Faces[f].Landmarks;
                var srcLandmarks = source.Result.Faces[f].Landmarks;

                for (int i = 0; i < dstLandmarks.Count; i++)
                {
                    var dst = dstLandmarks[i];
                    var src = srcLandmarks[i];

                    float x = Math.Abs(src.X - dst.X) > deadzone ? src.X : dst.X;
                    float y = Math.Abs(src.Y - dst.Y) > deadzone ? src.Y : dst.Y;

                    dstLandmarks[i] = new NormalizedPoint(x, y);
                }
            }
        }

        /// <summary>
        /// Copies predicted landmark positions into the rendered state using per-axis deadzone checks so
        /// the zero-lag preview path avoids both jitter and temporary prediction allocations.
        /// </summary>
        /// <param name="previous">The previous completed detection snapshot.</param>
        /// <param name="target">The latest completed detection snapshot.</param>
        /// <param name="alpha">The extrapolation factor returned by <see cref="TryComputePredictionAlpha"/>.</param>
        /// <param name="completedAtTicks">The render-time timestamp assigned to the predicted state.</param>
        /// <param name="destination">The rendered state to update in place.</param>
        /// <param name="deadzone">The per-axis normalized deadzone threshold.</param>
        private static void CopyPredictedWithPerLandmarkDeadzone(
            DetectionSnapshot previous,
            DetectionSnapshot target,
            float alpha,
            long completedAtTicks,
            RenderedDetectionState destination,
            float deadzone)
        {
            destination.Rotation = target.Rotation;
            destination.CompletedAtTicks = completedAtTicks;

            for (int faceIndex = 0; faceIndex < destination.Faces.Count; faceIndex++)
            {
                var destinationLandmarks = destination.Faces[faceIndex].Landmarks;
                var previousLandmarks = previous.Result.Faces[faceIndex].Landmarks;
                var targetLandmarks = target.Result.Faces[faceIndex].Landmarks;

                for (int landmarkIndex = 0; landmarkIndex < destinationLandmarks.Count; landmarkIndex++)
                {
                    var current = destinationLandmarks[landmarkIndex];
                    var prev = previousLandmarks[landmarkIndex];
                    var next = targetLandmarks[landmarkIndex];

                    float predictedX = PredictValue(prev.X, next.X, alpha);
                    float predictedY = PredictValue(prev.Y, next.Y, alpha);
                    float x = Math.Abs(predictedX - current.X) > deadzone ? predictedX : current.X;
                    float y = Math.Abs(predictedY - current.Y) > deadzone ? predictedY : current.Y;

                    destinationLandmarks[landmarkIndex] = new NormalizedPoint(x, y);
                }
            }
        }

        /// <summary>
        /// Creates a One Euro filter pair for every landmark coordinate in the supplied snapshot so mask
        /// rendering can be stabilized over time.
        /// </summary>
        /// <param name="state">The rendered landmark state used to seed the filter state.</param>
        /// <param name="nowTicks">The timestamp assigned to the initial filter sample.</param>
        private void InitializeLandmarkFilters(RenderedDetectionState state, long nowTicks)
        {
            int total = 0;
            foreach (var face in state.Faces)
                total += face.Landmarks.Count;

            _filtersX = new OneEuroFilter[total];
            _filtersY = new OneEuroFilter[total];

            int idx = 0;
            foreach (var face in state.Faces)
            {
                foreach (var pt in face.Landmarks)
                {
                    _filtersX[idx] = new OneEuroFilter(FilterMinCutoff, FilterBeta, FilterDCutoff, pt.X, nowTicks);
                    _filtersY[idx] = new OneEuroFilter(FilterMinCutoff, FilterBeta, FilterDCutoff, pt.Y, nowTicks);
                    idx++;
                }
            }
        }

        /// <summary>
        /// Filters all landmark coordinates from the source snapshot into the rendered state using the
        /// previously initialized One Euro filters.
        /// </summary>
        /// <param name="source">The latest detection snapshot.</param>
        /// <param name="destination">The rendered state that should receive filtered coordinates.</param>
        /// <param name="nowTicks">The current timestamp in <see cref="Stopwatch"/> ticks.</param>
        private void ApplyOneEuroFilters(DetectionSnapshot source, RenderedDetectionState destination, long nowTicks)
        {
            destination.Rotation = source.Rotation;
            destination.CompletedAtTicks = source.CompletedAtTicks;

            int idx = 0;
            for (int f = 0; f < destination.Faces.Count; f++)
            {
                var dstLandmarks = destination.Faces[f].Landmarks;
                var srcLandmarks = source.Result.Faces[f].Landmarks;

                for (int i = 0; i < dstLandmarks.Count; i++)
                {
                    dstLandmarks[i] = new NormalizedPoint(
                        _filtersX![idx].Filter(srcLandmarks[i].X, nowTicks),
                        _filtersY![idx].Filter(srcLandmarks[i].Y, nowTicks));
                    idx++;
                }
            }
        }

        /// <summary>
        /// Filters predicted landmark coordinates directly into the rendered state using the previously
        /// initialized One Euro filters.
        /// </summary>
        /// <param name="previous">The previous completed detection snapshot.</param>
        /// <param name="target">The latest completed detection snapshot.</param>
        /// <param name="alpha">The extrapolation factor returned by <see cref="TryComputePredictionAlpha"/>.</param>
        /// <param name="nowTicks">The current timestamp in <see cref="Stopwatch"/> ticks.</param>
        /// <param name="destination">The rendered state that should receive filtered predicted coordinates.</param>
        private void ApplyPredictedOneEuroFilters(
            DetectionSnapshot previous,
            DetectionSnapshot target,
            float alpha,
            long nowTicks,
            RenderedDetectionState destination)
        {
            destination.Rotation = target.Rotation;
            destination.CompletedAtTicks = nowTicks;

            int idx = 0;
            for (int faceIndex = 0; faceIndex < destination.Faces.Count; faceIndex++)
            {
                var destinationLandmarks = destination.Faces[faceIndex].Landmarks;
                var previousLandmarks = previous.Result.Faces[faceIndex].Landmarks;
                var targetLandmarks = target.Result.Faces[faceIndex].Landmarks;

                for (int landmarkIndex = 0; landmarkIndex < destinationLandmarks.Count; landmarkIndex++)
                {
                    var prev = previousLandmarks[landmarkIndex];
                    var next = targetLandmarks[landmarkIndex];
                    destinationLandmarks[landmarkIndex] = new NormalizedPoint(
                        _filtersX![idx].Filter(PredictValue(prev.X, next.X, alpha), nowTicks),
                        _filtersY![idx].Filter(PredictValue(prev.Y, next.Y, alpha), nowTicks));
                    idx++;
                }
            }
        }

        /// <summary>
        /// Lazily creates and updates the Skia paints used by landmark, rectangle, and mask overlays for
        /// the current frame scale.
        /// </summary>
        /// <param name="scale">The frame scale used to size strokes consistently across outputs.</param>
        private void EnsureDetectionPaints(float scale)
        {
            _detectionStrokePaint ??= new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.LimeGreen,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };

            _detectionFillPaint ??= new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.LimeGreen,
                Style = SKPaintStyle.Fill
            };

            _maskPaint ??= new SKPaint
            {
                IsAntialias = true,
                FilterQuality = SKFilterQuality.High
            };

            _detectionStrokePaint.StrokeWidth = Math.Max(2f, 2f * scale);
        }


        /// <summary>
        /// Draws every normalized landmark for a face as a dot overlay on the current frame.
        /// </summary>
        /// <param name="frame">The frame receiving the overlay.</param>
        /// <param name="face">The detected face whose landmarks should be rendered.</param>
        /// <param name="rotation">The detector-space rotation that must be projected into frame space.</param>
        private void DrawFaceLandmarks(DrawableFrame frame, DetectedFace face, int rotation)
        {
            float radius = Math.Max(2f, 2.5f * frame.Scale);
            foreach (var point in face.Landmarks)
            {
                var projected = ProjectPoint(point, rotation, false);
                frame.Canvas.DrawCircle(projected.X * frame.Width, projected.Y * frame.Height, radius, _detectionFillPaint);
            }
        }

        /// <summary>
        /// `DrawMode == DetectionType.Rectangle` draws a bounding rectangle around each detected face.
        /// </summary>
        /// <param name="frame"></param>
        /// <param name="face"></param>
        /// <param name="rotation"></param>
        private void DrawFaceRectangle(DrawableFrame frame, DetectedFace face, int rotation)
        {
            if (face.Landmarks.Count == 0)
                return;

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            foreach (var point in face.Landmarks)
            {
                var projected = ProjectPoint(point, rotation, false);
                minX = Math.Min(minX, projected.X);
                minY = Math.Min(minY, projected.Y);
                maxX = Math.Max(maxX, projected.X);
                maxY = Math.Max(maxY, projected.Y);
            }

            frame.Canvas.DrawRect(minX * frame.Width, minY * frame.Height, (maxX - minX) * frame.Width, (maxY - minY) * frame.Height, _detectionStrokePaint);
        }

        /// <summary>
        /// Draws the configured bitmap mask aligned to key facial anchor points for the current face.
        /// </summary>
        /// <param name="frame">The frame receiving the overlay.</param>
        /// <param name="face">The detected face whose landmarks define mask position and rotation.</param>
        /// <param name="rotation">The detector-space rotation that must be projected into frame space.</param>
        private void DrawFaceMask(DrawableFrame frame, DetectedFace face, int rotation)
        {
            if (face.Landmarks.Count < 455 || MaskBitmap == null)
                return;

            var maskPos = ActiveMaskConfig?.Position ?? MaskPosition.Inside;

            var anchorPt = maskPos switch
            {
                MaskPosition.Top => face.Landmarks[10],
                MaskPosition.Bottom => face.Landmarks[152],
                _ => face.Landmarks[1]
            };

            var leftCheek = face.Landmarks[234];
            var rightCheek = face.Landmarks[454];

            var anchor = ProjectPoint(anchorPt, rotation, false);
            var left = ProjectPoint(leftCheek, rotation, false);
            var right = ProjectPoint(rightCheek, rotation, false);

            float xAnchor = anchor.X * frame.Width;
            float yAnchor = anchor.Y * frame.Height;
            float xLeft = left.X * frame.Width;
            float yLeft = left.Y * frame.Height;
            float xRight = right.X * frame.Width;
            float yRight = right.Y * frame.Height;

            float dx = xRight - xLeft;
            float dy = yRight - yLeft;
            float faceWidth = (float)Math.Sqrt(dx * dx + dy * dy);

            float activeWidthMult = ActiveMaskConfig?.WidthMultiplier ?? 1.3f;
            float activeYOffset = ActiveMaskConfig?.YOffsetRatio ?? 0f;
            float maskWidth = faceWidth * activeWidthMult;
            float maskHeight = maskWidth * (MaskBitmap.Height / (float)MaskBitmap.Width);
            float angle = (float)(Math.Atan2(yRight - yLeft, xRight - xLeft) * (180.0 / Math.PI));

            float targetDrawY = maskPos switch
            {
                MaskPosition.Top => -maskHeight + (maskHeight * activeYOffset),
                MaskPosition.Bottom => maskHeight * activeYOffset,
                _ => (-maskHeight / 2f) + (maskHeight * activeYOffset)
            };

            frame.Canvas.Save();
            frame.Canvas.Translate(xAnchor, yAnchor);
            frame.Canvas.RotateDegrees(angle);
            frame.Canvas.DrawBitmap(MaskBitmap, new SKRect(-maskWidth / 2f, targetDrawY, maskWidth / 2f, targetDrawY + maskHeight), _maskPaint);
            frame.Canvas.Restore();
        }

        /// <summary>
        /// Projects a normalized detector-space landmark into normalized frame space by applying the
        /// detector rotation and optional horizontal mirroring.
        /// </summary>
        /// <param name="point">The detector-space normalized landmark.</param>
        /// <param name="rotation">The detector rotation to apply.</param>
        /// <param name="mirrorX">Whether to mirror the projected point horizontally.</param>
        /// <returns>The projected normalized point in frame space.</returns>
        private static NormalizedPoint ProjectPoint(NormalizedPoint point, int rotation, bool mirrorX)
        {
            float x = point.X;
            float y = point.Y;

            (x, y) = NormalizeRotation(rotation) switch
            {
                90 => (1f - y, x),
                180 => (1f - x, 1f - y),
                270 => (y, 1f - x),
                _ => (x, y)
            };

            if (mirrorX)
            {
                x = 1f - x;
            }

            return new NormalizedPoint(x, y);
        }

        /// <summary>
        /// Normalizes any rotation value into the range 0 to 359 degrees.
        /// </summary>
        /// <param name="rotation">The raw rotation value.</param>
        /// <returns>The normalized clockwise rotation in degrees.</returns>
        private static int NormalizeRotation(int rotation)
        {
            rotation %= 360;
            if (rotation < 0)
            {
                rotation += 360;
            }

            return rotation;
        }

        protected SkiaLayout? OverlayPreview;
        protected SkiaLayout? OverlayRecording;

        /// <summary>
        /// Set layouts to be rendered over preview and recording frames.
        /// Different instances are needed to avoid remeasuring when switching between preview and recording.
        /// This must be two copies of *same* layout, if you specify different layouts for preview and recording on some platforms only recording layout will be displayed while recording .
        /// </summary>
        /// <param name="previewLayout"></param>
        /// <param name="recordingLayout"></param>
        public void InitializeOverlayLayouts(SkiaLayout previewLayout, SkiaLayout recordingLayout)
        {
            if (previewLayout != null && recordingLayout != null)
            {
                this.OverlayPreview = previewLayout;
                this.OverlayRecording = recordingLayout;

                previewLayout.UseCache = SkiaCacheType.Operations;
                previewLayout.Tag = "Preview";

                recordingLayout.UseCache = SkiaCacheType.Operations;
                recordingLayout.Tag = "Recording";
 
                InvalidateOverlays();
            }
        }

        /// <summary>
        /// Call this when overlays need remeasuring, like camera format change, orientation change etc..
        /// </summary>
        public void InvalidateOverlays()
        {
            _overlayScaleChanged = -1;
            _rectFramePreview = SKRect.Empty;
            _rectFrameRecording = SKRect.Empty;
        }

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
