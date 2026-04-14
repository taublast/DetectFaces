using CoreGraphics;
using Foundation;
using UIKit;
using MediaPipeTasksVision;
using TestFaces.Services;

namespace TestFaces.Platforms.iOS;

public class FaceLandmarkDetector : IFaceLandmarkDetector
{
    private const float DefaultMinFaceDetectionConfidence = 0.5f;
    private const float DefaultMinFacePresenceConfidence = 0.5f;
    private const float DefaultMinTrackingConfidence = 0.5f;

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

    public Task<FaceLandmarkResult> DetectAsync(Stream imageStream)
    {
        return Task.Run(() =>
        {
            using var ms = new MemoryStream();
            imageStream.CopyTo(ms);
            var data = NSData.FromArray(ms.ToArray());
            var uiImage = UIImage.LoadFromData(data)
                ?? throw new InvalidOperationException("Failed to decode image");

            var mpImage = new MPPImage(uiImage, out var imageError);
            if (imageError is not null)
                throw new InvalidOperationException($"Failed to create MPPImage: {imageError.LocalizedDescription}");
            var result = GetLandmarker().DetectImage(mpImage, out var error);
            if (error is not null)
                throw new InvalidOperationException($"Detection failed: {error.LocalizedDescription}");

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

            var width = (int)(uiImage.Size.Width * uiImage.CurrentScale);
            var height = (int)(uiImage.Size.Height * uiImage.CurrentScale);

            return new FaceLandmarkResult
            {
                Faces = faces,
                ImageWidth = width,
                ImageHeight = height,
            };
        });
    }

    public void EnqueuePreviewDetection(byte[] rgbaBytes, PreviewDetectionRequest request)
    {
        _ = Task.Run(() =>
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
        });
    }

    private void BeginPendingTaskDetection(TaskCompletionSource<FaceLandmarkResult> completion, int width, int height)
    {
        lock (_pendingSync)
        {
            _pendingDetection = PendingDetection.ForTask(completion, width, height);
        }
    }

    private void BeginPendingPreviewDetection(PreviewDetectionRequest request)
    {
        lock (_pendingSync)
        {
            _pendingDetection = PendingDetection.ForPreview(request);
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
            if (pendingDetection.Completion != null)
            {
                pendingDetection.Completion.TrySetException(exception);
                return;
            }

            if (pendingDetection.Request != null)
            {
                RaisePreviewDetectionFailed(pendingDetection.Request, exception);
            }
            return;
        }

        var converted = ConvertResult(result, pendingDetection.Width, pendingDetection.Height);
        if (pendingDetection.Completion != null)
        {
            pendingDetection.Completion.TrySetResult(converted);
            return;
        }

        if (pendingDetection.Request != null)
        {
            RaisePreviewDetectionCompleted(pendingDetection.Request, converted);
        }
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
        private PendingDetection(TaskCompletionSource<FaceLandmarkResult>? completion, PreviewDetectionRequest? request, int width, int height)
        {
            Completion = completion;
            Request = request;
            Width = width;
            Height = height;
        }

        public TaskCompletionSource<FaceLandmarkResult>? Completion { get; }

        public PreviewDetectionRequest? Request { get; }

        public int Width { get; }

        public int Height { get; }

        public static PendingDetection ForTask(TaskCompletionSource<FaceLandmarkResult> completion, int width, int height)
            => new(completion, null, width, height);

        public static PendingDetection ForPreview(PreviewDetectionRequest request)
            => new(null, request, request.Width, request.Height);
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
