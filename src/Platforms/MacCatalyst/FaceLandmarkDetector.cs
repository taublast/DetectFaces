using TestFaces.Services;

namespace TestFaces.Platforms.MacCatalyst;

public class FaceLandmarkDetector : IFaceLandmarkDetector
{
    public int MaxFaces { get; set; } = 2;
    public float MinFaceDetectionConfidence { get; set; } = 0.5f;
    public float MinFacePresenceConfidence { get; set; } = 0.5f;
    public float MinTrackingConfidence { get; set; } = 0.5f;

    public event EventHandler<PreviewDetectionCompletedEventArgs>? PreviewDetectionCompleted;

    public event EventHandler<PreviewDetectionFailedEventArgs>? PreviewDetectionFailed;

    public Task<FaceLandmarkResult> DetectAsync(Stream imageStream)
    {
        throw new PlatformNotSupportedException(
            "Face landmark detection is not yet supported on macOS (Mac Catalyst).");
    }

    public void EnqueuePreviewDetection(byte[] rgbaBytes, PreviewDetectionRequest request)
    {
        PreviewDetectionFailed?.Invoke(this, new PreviewDetectionFailedEventArgs(
            request,
            new PlatformNotSupportedException(
                "Live face landmark detection is not yet supported on macOS (Mac Catalyst).")));
    }
}
