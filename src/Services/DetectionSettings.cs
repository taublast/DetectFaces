namespace DetectFaces.Services;

/// <summary>
/// Global detection settings shared across all platforms.
/// </summary>
public static class DetectionSettings
{
    /// <summary>
    /// When true, the detector will attempt to use the GPU delegate for inference.
    /// Falls back to CPU automatically if GPU is unavailable.
    /// Currently only affects Android and iOS; Windows is CPU-only.
    /// </summary>
    public static bool TryUseGpu = true;

    /// <summary>
    /// Initial detection overlay mode shown at startup.
    /// 0 = Landmark, 1 = Rectangle, 2 = Mask (Spider-Man), 3 = Hat (Cake).
    /// </summary>
    public static DetectionType InitialDetectionType = DetectionType.Rectangle;
}   