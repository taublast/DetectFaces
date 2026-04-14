using Android.Graphics;
using MediaPipe.Tasks.Core;
using MediaPipe.Tasks.Vision.Core;
using MediaPipe.Tasks.Vision.FaceLandmarker;
using System.Diagnostics;
using TestFaces.Services;
using MPImage = MediaPipe.Framework.Image.MPImage;
using BitmapImageBuilder = MediaPipe.Framework.Image.BitmapImageBuilder;

namespace TestFaces.Platforms.Droid;

/// <summary>
/// https://ai.google.dev/edge/mediapipe/solutions/vision/face_landmarker/android
/// </summary>
public class FaceLandmarkDetector : IFaceLandmarkDetector
{
    private FaceLandmarker? _landmarker;
    private readonly object _landmarkerSync = new();
    private readonly object _pendingSync = new();
    private readonly ResultListener _resultListener;
    private readonly ErrorListener _errorListener;
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
    private PendingDetection? _pendingDetection;

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
            ResetLandmarker();
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
            ResetLandmarker();
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
            ResetLandmarker();
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
            ResetLandmarker();
        }
    }

    public FaceLandmarkDetector()
    {
        _resultListener = new ResultListener(this);
        _errorListener = new ErrorListener(this);
    }

    private FaceLandmarker GetLandmarker()
    {
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
                catch (Exception ex)
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
            // These thresholds are intentionally exposed by the sample so confidence tuning can be tested live.
            .SetMinFaceDetectionConfidence(new Java.Lang.Float(_minFaceDetectionConfidence))
            .SetMinFacePresenceConfidence(new Java.Lang.Float(_minFacePresenceConfidence))
            .SetMinTrackingConfidence(new Java.Lang.Float(_minTrackingConfidence))
            .SetRunningMode(RunningMode.LiveStream)
            .SetResultListener(_resultListener)
            .SetErrorListener(_errorListener)
            .Build();

        return FaceLandmarker.CreateFromOptions(
            global::Android.App.Application.Context, options);
    }

    public void EnqueuePreviewDetection(byte[] rgbaBytes, PreviewDetectionRequest request)
    {
        try
        {
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

            BeginPendingPreviewDetection(request, convertStopwatch.Elapsed.TotalMilliseconds);

            var timestampMs = Interlocked.Increment(ref _videoTimestampMs);
            GetLandmarker().DetectAsync(mpImage, timestampMs);
        }
        catch (Exception ex)
        {
            RaisePreviewDetectionFailed(request, ex);
        }
    }

    private void ResetLandmarker()
    {
        lock (_landmarkerSync)
        {
            if (_landmarker is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _landmarker = null;
        }
    }

    private static float ClampConfidence(float value)
    {
        return Math.Clamp(value, 0f, 1f);
    }

    private void BeginPendingPreviewDetection(PreviewDetectionRequest request, double conversionMilliseconds)
    {
        lock (_pendingSync)
        {
            _pendingDetection = new PendingDetection(request, conversionMilliseconds, Stopwatch.GetTimestamp());
        }
    }

    private void OnLiveStreamResult(FaceLandmarkerResult result, MPImage inputImage)
    {
        PendingDetection? pendingDetection;

        lock (_pendingSync)
        {
            pendingDetection = _pendingDetection;
            if (pendingDetection == null)
                return;

            _pendingDetection = null;
        }

        var converted = ConvertResult(
            result,
            pendingDetection.Request.Width,
            pendingDetection.Request.Height,
            pendingDetection.ConversionMilliseconds,
            Stopwatch.GetElapsedTime(pendingDetection.InferenceStartTicks).TotalMilliseconds,
            _usingGpuDelegate);

        RaisePreviewDetectionCompleted(pendingDetection.Request, converted);
    }

    private void OnLiveStreamError(Java.Lang.RuntimeException error)
    {
        PendingDetection? pendingDetection;

        lock (_pendingSync)
        {
            pendingDetection = _pendingDetection;
            _pendingDetection = null;
        }

        if (pendingDetection == null)
            return;

        var exception = new InvalidOperationException($"Android FaceLandmarker failed: {error.Message}", error);
        RaisePreviewDetectionFailed(pendingDetection.Request, exception);
    }

    private void RaisePreviewDetectionCompleted(PreviewDetectionRequest request, FaceLandmarkResult result)
    {
        PreviewDetectionCompleted?.Invoke(this, new PreviewDetectionCompletedEventArgs(request, result));
    }

    private void RaisePreviewDetectionFailed(PreviewDetectionRequest request, Exception exception)
    {
        PreviewDetectionFailed?.Invoke(this, new PreviewDetectionFailedEventArgs(request, exception));
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

    private sealed class ResultListener : Java.Lang.Object, OutputHandler.IResultListener
    {
        private readonly FaceLandmarkDetector _owner;

        public ResultListener(FaceLandmarkDetector owner)
        {
            _owner = owner;
        }

        public void Run(Java.Lang.Object result, Java.Lang.Object inputImage)
        {
            try
            {
                _owner.OnLiveStreamResult((FaceLandmarkerResult)result, (MPImage)inputImage);
            }
            finally
            {
                (result as IDisposable)?.Dispose();
                (inputImage as IDisposable)?.Dispose();
            }
        }
    }

    private sealed class ErrorListener : Java.Lang.Object, IErrorListener
    {
        private readonly FaceLandmarkDetector _owner;

        public ErrorListener(FaceLandmarkDetector owner)
        {
            _owner = owner;
        }

        public void OnError(Java.Lang.RuntimeException error)
        {
            _owner.OnLiveStreamError(error);
        }
    }

    private sealed class PendingDetection
    {
        public PendingDetection(PreviewDetectionRequest request, double conversionMilliseconds, long inferenceStartTicks)
        {
            Request = request;
            ConversionMilliseconds = conversionMilliseconds;
            InferenceStartTicks = inferenceStartTicks;
        }

        public PreviewDetectionRequest Request { get; }

        public double ConversionMilliseconds { get; }

        public long InferenceStartTicks { get; }
    }
}
