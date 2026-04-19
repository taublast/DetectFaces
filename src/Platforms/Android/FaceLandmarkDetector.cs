using Android.Graphics;
using MediaPipe.Tasks.Core;
using MediaPipe.Tasks.Vision.Core;
using MediaPipe.Tasks.Vision.FaceLandmarker;
using System.Collections.Concurrent;
using System.Diagnostics;
using DetectFaces.Services;
using MPImage = MediaPipe.Framework.Image.MPImage;
using BitmapImageBuilder = MediaPipe.Framework.Image.BitmapImageBuilder;
using Object = Java.Lang.Object;
using RuntimeException = Java.Lang.RuntimeException;
using static MediaPipe.Tasks.Core.OutputHandler;

namespace DetectFaces.Platforms.Droid;

/// <summary>
/// https://ai.google.dev/edge/mediapipe/solutions/vision/face_landmarker/android
/// </summary>
public class FaceLandmarkDetector : Object, IFaceLandmarkDetector, IResultListener, IErrorListener, IDisposable
{
    private const int ConfigurationRestartDelayMs = 100;

    private sealed class PendingContext
    {
        public PreviewDetectionRequest Request;
        public MPImage Image = null!;
        public double ConversionMilliseconds;
        public long InferenceStartTicks;
    }

    private readonly ConcurrentDictionary<long, PendingContext> _pending = new();

    private FaceLandmarker? _landmarker;
    private readonly object _landmarkerSync = new();
    private bool _usingGpuDelegate;
    private long _videoTimestampMs;
    private int _maxFaces = 2;
    private float _minFaceDetectionConfidence = 0.5f;
    private float _minFacePresenceConfidence = 0.5f;
    private float _minTrackingConfidence = 0.5f;
    private Bitmap? _liveBitmap;
    private int[]? _livePixels;
    private int _liveWidth;
    private int _liveHeight;
    private bool _disposed;
    private int _configurationLockDepth;
    private bool _restartRequired;
    private bool _restartScheduled;
    private int _restartSequence;

    public event EventHandler<PreviewDetectionCompletedEventArgs>? PreviewDetectionCompleted;

    public event EventHandler<PreviewDetectionFailedEventArgs>? PreviewDetectionFailed;

    public int MaxFaces
    {
        get => _maxFaces;
        set
        {
            var normalized = Math.Max(1, value);
            if (_maxFaces == normalized)
                return;

            _maxFaces = normalized;
            OnConfigurationChanged();
        }
    }

    public float MinFaceDetectionConfidence
    {
        get => _minFaceDetectionConfidence;
        set
        {
            var normalized = ClampConfidence(value);
            if (_minFaceDetectionConfidence == normalized)
                return;

            _minFaceDetectionConfidence = normalized;
            OnConfigurationChanged();
        }
    }

    public float MinFacePresenceConfidence
    {
        get => _minFacePresenceConfidence;
        set
        {
            var normalized = ClampConfidence(value);
            if (_minFacePresenceConfidence == normalized)
                return;

            _minFacePresenceConfidence = normalized;
            OnConfigurationChanged();
        }
    }

    public float MinTrackingConfidence
    {
        get => _minTrackingConfidence;
        set
        {
            var normalized = ClampConfidence(value);
            if (_minTrackingConfidence == normalized)
                return;

            _minTrackingConfidence = normalized;
            OnConfigurationChanged();
        }
    }

    public void LockConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool shouldStop = false;

        lock (_landmarkerSync)
        {
            if (_configurationLockDepth == 0)
            {
                CancelScheduledRestartNoLock();
                _restartRequired = true;
                shouldStop = true;
            }

            _configurationLockDepth++;
        }

        if (shouldStop)
            ResetLandmarker();
    }

    public void UnlockConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool shouldRestart = false;

        lock (_landmarkerSync)
        {
            if (_configurationLockDepth == 0)
                return;

            _configurationLockDepth--;

            if (_configurationLockDepth == 0 && _restartRequired)
            {
                _restartRequired = false;
                shouldRestart = true;
            }
        }

        if (shouldRestart)
            ScheduleRestart();
    }

    private FaceLandmarker GetLandmarker()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_landmarker is not null)
            return _landmarker;

        lock (_landmarkerSync)
        {
            if (_landmarker is not null)
                return _landmarker;

            if (DetectionSettings.TryUseGpu)
            {
                try
                {
                    _landmarker = CreateLandmarker(useGpu: true);
                    _usingGpuDelegate = true;
                    Debug.WriteLine("FaceLandmarker Android: using GPU delegate.");
                }
                catch (System.Exception ex)
                {
                    Debug.WriteLine($"FaceLandmarker Android: GPU delegate init failed, falling back to CPU. {ex}");
                    _landmarker = CreateLandmarker(useGpu: false);
                    _usingGpuDelegate = false;
                    Debug.WriteLine("FaceLandmarker Android: using CPU delegate.");
                }
            }
            else
            {
                _landmarker = CreateLandmarker(useGpu: false);
                _usingGpuDelegate = false;
                Debug.WriteLine("FaceLandmarker Android: using CPU delegate (GPU disabled by DetectionSettings).");
            }

            return _landmarker;
        }
    }

    private FaceLandmarker CreateLandmarker(bool useGpu)
    {
        var baseOptionsBuilder = BaseOptions.InvokeBuilder()
            .SetModelAssetPath("face_landmarker.task")
            .SetDelegate(useGpu ? global::MediaPipe.Tasks.Core.Delegates.Gpu : global::MediaPipe.Tasks.Core.Delegates.Cpu);

        var baseOptions = baseOptionsBuilder.Build();

        var options = FaceLandmarker.FaceLandmarkerOptions.InvokeBuilder()
            .SetBaseOptions(baseOptions)
            .SetNumFaces(new Java.Lang.Integer(_maxFaces))
            .SetOutputFaceBlendshapes(false)
            .SetOutputFacialTransformationMatrixes(false)
            // These thresholds are intentionally exposed by the sample so confidence tuning can be tested live.
            .SetMinFaceDetectionConfidence(new Java.Lang.Float(_minFaceDetectionConfidence))
            .SetMinFacePresenceConfidence(new Java.Lang.Float(_minFacePresenceConfidence))
            .SetMinTrackingConfidence(new Java.Lang.Float(_minTrackingConfidence))
            .SetRunningMode(RunningMode.LiveStream)
            .SetResultListener(this)
            .SetErrorListener(this)
            .Build();

        return FaceLandmarker.CreateFromOptions(
            global::Android.App.Application.Context, options);
    }

    public void EnqueuePreviewDetection(byte[] rgbaBytes, PreviewDetectionRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            if (IsConfigurationLocked())
            {
                RaisePreviewDetectionCompleted(request, new FaceLandmarkResult());
                return;
            }

            if (rgbaBytes == null || request.Width <= 0 || request.Height <= 0 || rgbaBytes.Length < request.Width * request.Height * 4)
            {
                RaisePreviewDetectionCompleted(request, new FaceLandmarkResult());
                return;
            }

            var bitmap = GetOrCreateLiveBitmap(request.Width, request.Height);
            var pixels = GetOrCreateLivePixels(request.Width, request.Height);
            int pixelCount = request.Width * request.Height;
            var convertStopwatch = Stopwatch.StartNew();

            for (int src = 0, dst = 0; dst < pixelCount; src += 4, dst++)
            {
                int r = rgbaBytes[src + 0];
                int g = rgbaBytes[src + 1];
                int b = rgbaBytes[src + 2];
                int a = rgbaBytes[src + 3];
                pixels[dst] = (a << 24) | (r << 16) | (g << 8) | b;
            }

            bitmap.SetPixels(pixels, 0, request.Width, 0, 0, request.Width, request.Height);

            var mpImage = new BitmapImageBuilder(bitmap).Build();
            convertStopwatch.Stop();

            var timestampMs = Interlocked.Increment(ref _videoTimestampMs);
            var conversionMilliseconds = convertStopwatch.Elapsed.TotalMilliseconds;

            var context = new PendingContext
            {
                Request = request,
                Image = mpImage,
                ConversionMilliseconds = conversionMilliseconds,
                InferenceStartTicks = Stopwatch.GetTimestamp(),
            };
            _pending[timestampMs] = context;

            try
            {
                GetLandmarker().DetectAsync(mpImage, timestampMs);
            }
            catch
            {
                _pending.TryRemove(timestampMs, out _);
                mpImage.Dispose();
                throw;
            }
        }
        catch (System.Exception ex)
        {
            RaisePreviewDetectionFailed(request, ex);
        }
    }

    private bool IsConfigurationLocked()
    {
        lock (_landmarkerSync)
        {
            return _configurationLockDepth > 0 || _restartScheduled;
        }
    }

    private void OnConfigurationChanged()
    {
        bool shouldRestart = false;

        lock (_landmarkerSync)
        {
            _restartRequired = true;
            CancelScheduledRestartNoLock();

            if (_configurationLockDepth == 0)
            {
                _restartRequired = false;
                shouldRestart = true;
            }
        }

        if (!shouldRestart)
            return;

        ResetLandmarker();
        ScheduleRestart();
    }

    private void ScheduleRestart()
    {
        int restartSequence;

        lock (_landmarkerSync)
        {
            if (_disposed)
                return;

            _restartScheduled = true;
            restartSequence = ++_restartSequence;
        }

        _ = RestartAfterDelayAsync(restartSequence);
    }

    private async Task RestartAfterDelayAsync(int restartSequence)
    {
        await Task.Delay(ConfigurationRestartDelayMs).ConfigureAwait(false);

        bool shouldRestart = false;

        lock (_landmarkerSync)
        {
            if (restartSequence != _restartSequence)
                return;

            _restartScheduled = false;

            if (_disposed)
                return;

            if (_configurationLockDepth > 0)
            {
                _restartRequired = true;
                return;
            }

            shouldRestart = true;
        }

        if (!shouldRestart)
            return;

        try
        {
            GetLandmarker();
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine($"FaceLandmarker Android: delayed restart failed. {ex}");
        }
    }

    private void CancelScheduledRestartNoLock()
    {
        _restartScheduled = false;
        _restartSequence++;
    }

    private void ResetLandmarker()
    {
        FaceLandmarker? landmarker;

        lock (_landmarkerSync)
        {
            landmarker = _landmarker;
            _landmarker = null;
            _usingGpuDelegate = false;
        }

        if (landmarker is not null)
        {
            try
            {
                landmarker.Close();
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"FaceLandmarker Android: close during reset failed. {ex}");
            }

            if (landmarker is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        foreach (var kv in _pending.ToArray())
        {
            if (_pending.TryRemove(kv.Key, out var ctx))
                ctx.Image?.Dispose();
        }
    }

    private static float ClampConfidence(float value)
    {
        return Math.Clamp(value, 0f, 1f);
    }

    public void Run(Object resobj, Object image)
    {
        var result = resobj as FaceLandmarkerResult;
        if (result is null)
            return;

        var timestampMs = result.TimestampMs();
        if (!_pending.TryRemove(timestampMs, out var context))
        {
            result.Dispose();
            (image as MPImage)?.Dispose();
            return;
        }

        double inferenceMs = (Stopwatch.GetTimestamp() - context.InferenceStartTicks) * 1000.0 / Stopwatch.Frequency;

        try
        {
            if (_disposed)
                return;

            var converted = ConvertResult(
                result,
                context.Request.Width,
                context.Request.Height,
                context.ConversionMilliseconds,
                inferenceMs,
                _usingGpuDelegate);

            RaisePreviewDetectionCompleted(context.Request, converted);
        }
        catch (System.Exception ex)
        {
            if (!_disposed)
                RaisePreviewDetectionFailed(context.Request, ex);
        }
        finally
        {
            result.Dispose();
            (image as MPImage)?.Dispose();
        }
    }

    public void OnError(RuntimeException error)
    {
        var ex = new System.Exception(error?.Message ?? "MediaPipe FaceLandmarker error");
        foreach (var kv in _pending.ToArray())
        {
            if (_pending.TryRemove(kv.Key, out var ctx))
            {
                ctx.Image?.Dispose();
                if (!_disposed)
                    RaisePreviewDetectionFailed(ctx.Request, ex);
            }
        }
    }

    private void RaisePreviewDetectionCompleted(PreviewDetectionRequest request, FaceLandmarkResult result)
    {
        PreviewDetectionCompleted?.Invoke(this, new PreviewDetectionCompletedEventArgs(request, result));
    }

    private void RaisePreviewDetectionFailed(PreviewDetectionRequest request, System.Exception exception)
    {
        PreviewDetectionFailed?.Invoke(this, new PreviewDetectionFailedEventArgs(request, exception));
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            lock (_landmarkerSync)
            {
                CancelScheduledRestartNoLock();
            }

            ResetLandmarker();

            var liveBitmap = _liveBitmap;
            _liveBitmap = null;
            liveBitmap?.Dispose();

            _livePixels = null;
            _liveWidth = 0;
            _liveHeight = 0;
        }

        base.Dispose(disposing);
    }

    private Bitmap GetOrCreateLiveBitmap(int width, int height)
    {
        if (_liveBitmap == null || _liveWidth != width || _liveHeight != height)
        {
            var kill = _liveBitmap;
            _liveBitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888);
            _liveWidth = width;
            _liveHeight = height;
            kill?.Dispose();
        }

        return _liveBitmap;
    }

    private int[] GetOrCreateLivePixels(int width, int height)
    {
        int required = width * height;
        if (_livePixels == null || _livePixels.Length != required)
        {
            _livePixels = new int[required];
        }

        return _livePixels;
    }

    /// <summary>
    /// When true, landmarks are read via the low-overhead bulk JNI accessors (FaceLandmarksXY).
    /// Set to false to use the generated FaceLandmarks wrapper list — useful for benchmarking
    /// the difference between the two approaches.
    /// </summary>
    public static bool UseFastApi = true;

    private static FaceLandmarkResult ConvertResult(
        FaceLandmarkerResult result,
        int width,
        int height,
        double conversionMilliseconds,
        double inferenceMilliseconds,
        bool usedGpuDelegate)
    {
        var mappingStopwatch = Stopwatch.StartNew();
        List<DetectedFace> faces;

        if (UseFastApi) // use new FAST landmarks read
        {
            var faceLandmarks = result?.FaceLandmarksXY();
            faces = faceLandmarks != null ? new List<DetectedFace>(faceLandmarks.Length) : new List<DetectedFace>(2);
            if (faceLandmarks is not null)
            {
                for (int faceIndex = 0; faceIndex < faceLandmarks.Length; faceIndex++)
                    faces.Add(MapDetectedFace(faceLandmarks[faceIndex], 2));
            }
        }
        else
        {
            // Slow path: use default API and traverses IList<IList<NormalizedLandmark>> ~1400 JNI crossings per frame for 478 landmarks
            var faceLandmarks = result.FaceLandmarks();
            faces = faceLandmarks != null ? new List<DetectedFace>(faceLandmarks.Count) : new List<DetectedFace>(2);
            if (faceLandmarks is not null)
            {
                foreach (var face in faceLandmarks)
                {
                    var points = new List<NormalizedPoint>(face.Count);
                    foreach (var lm in face)
                        points.Add(new NormalizedPoint(lm.X(), lm.Y()));
                    faces.Add(new DetectedFace { Landmarks = points });
                }
            }
        }

        mappingStopwatch.Stop();

        return new FaceLandmarkResult
        {
            Faces = faces,
            ImageWidth = width,
            ImageHeight = height,
            ConversionMilliseconds = conversionMilliseconds,
            InferenceMilliseconds = inferenceMilliseconds,
            ResultMappingMilliseconds = mappingStopwatch.Elapsed.TotalMilliseconds,
            UsedGpuDelegate = usedGpuDelegate,
        };
    }

    private static DetectedFace MapDetectedFace(float[] coordinates, int stride)
    {
        int landmarkCount = coordinates.Length / stride;
        var points = new List<NormalizedPoint>(landmarkCount);
        for (int landmarkIndex = 0; landmarkIndex < landmarkCount; landmarkIndex++)
        {
            int coordinateIndex = landmarkIndex * stride;
            points.Add(new NormalizedPoint(
                coordinates[coordinateIndex],
                coordinates[coordinateIndex + 1]));
        }

        return new DetectedFace { Landmarks = points };
    }

}
