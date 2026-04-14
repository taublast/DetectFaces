using CoreGraphics;
using Foundation;
using UIKit;
using MediaPipeTasksVision;
using TestFaces.Services;

namespace TestFaces.Platforms.iOS;

public class FaceLandmarkDetector : IFaceLandmarkDetector
{
    private const float DefaultMinFaceDetectionConfidence = 0.3f;
    private const float DefaultMinFacePresenceConfidence = 0.3f;
    private const float DefaultMinTrackingConfidence = 0.3f;

    private MPPFaceLandmarker? _landmarker;
    private readonly object _pendingSync = new();
    private readonly LiveStreamDelegate _liveStreamDelegate;
    private long _videoTimestampMs;
    private int _maxFaces = 2;
    private float _minFaceDetectionConfidence = DefaultMinFaceDetectionConfidence;
    private float _minFacePresenceConfidence = DefaultMinFacePresenceConfidence;
    private float _minTrackingConfidence = DefaultMinTrackingConfidence;
    private PendingDetection? _pendingDetection;

    public event EventHandler<PreviewDetectionCompletedEventArgs>? PreviewDetectionCompleted;

    public event EventHandler<PreviewDetectionFailedEventArgs>? PreviewDetectionFailed;

    public FaceLandmarkDetector()
    {
        _liveStreamDelegate = new LiveStreamDelegate(this);
    }

    public int MaxFaces
    {
        get => _maxFaces;
        set
        {
            var normalized = Math.Max(1, value);
            if (_maxFaces == normalized)
                return;

            _maxFaces = normalized;
            _landmarker = null;
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
            _landmarker = null;
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
            _landmarker = null;
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
            _landmarker = null;
        }
    }

    private MPPFaceLandmarker GetLandmarker()
    {
        if (_landmarker is not null)
            return _landmarker;

        var modelPath = NSBundle.MainBundle.PathForResource("face_landmarker", "task")
            ?? throw new FileNotFoundException("face_landmarker.task not found in app bundle");

        var baseOptions = new MPPBaseOptions();
        baseOptions.ModelAssetPath = modelPath;
        baseOptions.Delegate = DetectionSettings.TryUseGpu ? MPPDelegate.Gpu : MPPDelegate.Cpu;

        var options = new MPPFaceLandmarkerOptions();
        options.BaseOptions = baseOptions;
        options.NumFaces = _maxFaces;
        options.MinFaceDetectionConfidence = _minFaceDetectionConfidence;
        options.MinFacePresenceConfidence = _minFacePresenceConfidence;
        options.MinTrackingConfidence = _minTrackingConfidence;
        options.RunningMode = MPPRunningMode.LiveStream;
        options.FaceLandmarkerLiveStreamDelegate = _liveStreamDelegate;

        _landmarker = new MPPFaceLandmarker(options, out var error);
        if (error is not null)
            throw new InvalidOperationException($"Failed to create FaceLandmarker: {error.LocalizedDescription}");

        return _landmarker;
    }

    private static float ClampConfidence(float value)
    {
        return Math.Clamp(value, 0f, 1f);
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

            using var data = NSData.FromArray(rgbaBytes);
            using var provider = new CGDataProvider(data);
            using var colorSpace = CGColorSpace.CreateDeviceRGB();
            using var cgImage = new CGImage(
                request.Width,
                request.Height,
                8,
                32,
                request.Width * 4,
                colorSpace,
                CGBitmapFlags.ByteOrderDefault | CGBitmapFlags.PremultipliedLast,
                provider,
                null,
                false,
                CGColorRenderingIntent.Default);

            using var uiImage = new UIImage(cgImage);
            var mpImage = new MPPImage(uiImage, out var imageError);
            if (imageError is not null)
                throw new InvalidOperationException($"Failed to create MPPImage: {imageError.LocalizedDescription}");

            BeginPendingPreviewDetection(request);

            var timestampMs = (nint)Interlocked.Increment(ref _videoTimestampMs);
            GetLandmarker().DetectAsyncImage(mpImage, timestampMs, out var error);
            if (error is not null)
                throw new InvalidOperationException($"Detection failed: {error.LocalizedDescription}");
        }
        catch (Exception ex)
        {
            RaisePreviewDetectionFailed(request, ex);
        }
    }

    private void BeginPendingPreviewDetection(PreviewDetectionRequest request)
    {
        lock (_pendingSync)
        {
            _pendingDetection = new PendingDetection(request);
        }
    }

    private void OnLiveStreamResult(MPPFaceLandmarkerResult? result, nint timestampInMilliseconds, NSError? error)
    {
        PendingDetection? pendingDetection;

        lock (_pendingSync)
        {
            pendingDetection = _pendingDetection;
            if (pendingDetection == null)
                return;

            _pendingDetection = null;
        }

        if (error is not null)
        {
            var exception = new InvalidOperationException($"Detection failed: {error.LocalizedDescription}");
            RaisePreviewDetectionFailed(pendingDetection.Request, exception);
            return;
        }

        var converted = ConvertResult(result, pendingDetection.Request.Width, pendingDetection.Request.Height);
        RaisePreviewDetectionCompleted(pendingDetection.Request, converted);
    }

    private void RaisePreviewDetectionCompleted(PreviewDetectionRequest request, FaceLandmarkResult result)
    {
        PreviewDetectionCompleted?.Invoke(this, new PreviewDetectionCompletedEventArgs(request, result));
    }

    private void RaisePreviewDetectionFailed(PreviewDetectionRequest request, Exception exception)
    {
        PreviewDetectionFailed?.Invoke(this, new PreviewDetectionFailedEventArgs(request, exception));
    }

    private static FaceLandmarkResult ConvertResult(MPPFaceLandmarkerResult? result, int width, int height)
    {
        var faceLandmarks = result?.FaceLandmarks;
        var faces = faceLandmarks is NSArray faceLandmarkArray
            ? new List<DetectedFace>((int)faceLandmarkArray.Count)
            : new List<DetectedFace>();
        if (faceLandmarks is not null)
        {
            foreach (var landmarkList in faceLandmarks)
            {
                faces.Add(MapDetectedFace(landmarkList));
            }
        }

        return new FaceLandmarkResult
        {
            Faces = faces,
            ImageWidth = width,
            ImageHeight = height,
        };
    }

    private static DetectedFace MapDetectedFace(NSObject? landmarkList)
    {
        if (landmarkList is not NSArray arr)
            return new DetectedFace { Landmarks = [] };

        var points = new List<NormalizedPoint>((int)arr.Count);
        for (nuint i = 0; i < arr.Count; i++)
        {
            var landmark = arr.GetItem<MPPNormalizedLandmark>(i);
            points.Add(new NormalizedPoint(landmark.X, landmark.Y));
        }

        return new DetectedFace { Landmarks = points };
    }

    private sealed class PendingDetection
    {
        public PendingDetection(PreviewDetectionRequest request)
        {
            Request = request;
        }

        public PreviewDetectionRequest Request { get; }
    }

    private sealed class LiveStreamDelegate : MPPFaceLandmarkerLiveStreamDelegate
    {
        private readonly FaceLandmarkDetector _owner;

        public LiveStreamDelegate(FaceLandmarkDetector owner)
        {
            _owner = owner;
        }

        public override void DidFinishDetectionWithResult(MPPFaceLandmarker faceLandmarker, MPPFaceLandmarkerResult? result,
            IntPtr timestampInMilliseconds, NSError? error)
        {
            _owner.OnLiveStreamResult(result, timestampInMilliseconds, error);

            //            base.DidFinishDetectionWithResult(faceLandmarker, result, timestampInMilliseconds, error);

        }
    }
}
