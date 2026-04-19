using CoreGraphics;
using Foundation;
using UIKit;
using MediaPipeTasksVision;
using DetectFaces.Services;
using System.Diagnostics;

namespace DetectFaces.Platforms.iOS;

public class FaceLandmarkDetector : IFaceLandmarkDetector
{
    private const float DefaultMinFaceDetectionConfidence = 0.3f;
    private const float DefaultMinFacePresenceConfidence = 0.3f;
    private const float DefaultMinTrackingConfidence = 0.3f;
    private const int ConfigurationRestartDelayMs = 100;

    private MPPFaceLandmarker? _landmarker;
    private readonly object _landmarkerSync = new();
    private readonly object _pendingSync = new();
    private readonly LiveStreamDelegate _liveStreamDelegate;
    private long _videoTimestampMs;
    private int _maxFaces = 2;
    private float _minFaceDetectionConfidence = DefaultMinFaceDetectionConfidence;
    private float _minFacePresenceConfidence = DefaultMinFacePresenceConfidence;
    private float _minTrackingConfidence = DefaultMinTrackingConfidence;
    private PendingDetection? _pendingDetection;
    private int _configurationLockDepth;
    private bool _restartRequired;
    private bool _restartScheduled;
    private int _restartSequence;

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

    private MPPFaceLandmarker GetLandmarker()
    {
        if (_landmarker is not null)
            return _landmarker;

        lock (_landmarkerSync)
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
    }

    private static float ClampConfidence(float value)
    {
        return Math.Clamp(value, 0f, 1f);
    }

    public void EnqueuePreviewDetection(byte[] rgbaBytes, PreviewDetectionRequest request)
    {
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

    private void ResetLandmarker()
    {
        MPPFaceLandmarker? landmarker;

        lock (_landmarkerSync)
        {
            landmarker = _landmarker;
            _landmarker = null;
        }

        if (landmarker is IDisposable disposable)
            disposable.Dispose();

        lock (_pendingSync)
        {
            _pendingDetection = null;
        }
    }

    private void ScheduleRestart()
    {
        int restartSequence;

        lock (_landmarkerSync)
        {
            _restartScheduled = true;
            restartSequence = ++_restartSequence;
        }

        _ = RestartAfterDelayAsync(restartSequence);
    }

    private async Task RestartAfterDelayAsync(int restartSequence)
    {
        await Task.Delay(ConfigurationRestartDelayMs).ConfigureAwait(false);

        lock (_landmarkerSync)
        {
            if (_configurationLockDepth > 0 || restartSequence != _restartSequence)
            {
                if (_configurationLockDepth > 0)
                    _restartRequired = true;

                return;
            }

            _restartScheduled = false;
        }

        try
        {
            GetLandmarker();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FaceLandmarker iOS: delayed restart failed. {ex}");
        }
    }

    private void CancelScheduledRestartNoLock()
    {
        _restartScheduled = false;
        _restartSequence++;
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
            var faces = faceLandmarks is not null
                ? new List<DetectedFace>(faceLandmarks.Length)
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

        private static DetectedFace MapDetectedFace(NSArray<MPPNormalizedLandmark>? landmarkList)
    {
            if (landmarkList is null)
            return new DetectedFace { Landmarks = [] };

            var points = new List<NormalizedPoint>((int)landmarkList.Count);
            foreach (var landmark in landmarkList)
        {
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
