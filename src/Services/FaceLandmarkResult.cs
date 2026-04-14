namespace TestFaces.Services;

public enum MaskPosition
{
    Inside,  // Overlays over the face area (e.g., masks, glasses, mustaches)
    Top,     // Stacks above the forehead (e.g., crowns, hats)
    Bottom   // Hangs below the chin (e.g., long beards, bowties)
}

public class MaskConfiguration
{
    public string Filename { get; set; } = string.Empty;

    public MaskPosition Position { get; set; } = MaskPosition.Inside;

    // Multiplier against the face width (cheek-to-cheek) 
    public float WidthMultiplier { get; set; } = 1.3f;
    
    // Adjusts the final Y position after the Position enum places it.
    // E.g., for Top, an offset of 0.1 pushes the hat 10% of its height down over the hairline.
    public float YOffsetRatio { get; set; } = 0.1f;
}

public enum DetectionType
{
    Landmark,
    Rectangle,
    Mask
}

public class FaceLandmarkResult
{
    public List<DetectedFace> Faces { get; init; } = [];
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public double ConversionMilliseconds { get; init; }
    public double InferenceMilliseconds { get; init; }
    public double ResultMappingMilliseconds { get; init; }
    public bool UsedGpuDelegate { get; init; }
}

public class DetectedFace
{
    public List<NormalizedPoint> Landmarks { get; init; } = [];
}

public record struct NormalizedPoint(float X, float Y);

