using TestFaces.Services;

namespace TestFaces.Drawables;

public class LandmarkDrawable : IDrawable
{
    private FaceLandmarkResult? _result;

    public DetectionType DrawMode { get; set; } = DetectionType.Landmark;
    public Microsoft.Maui.Graphics.IImage? MaskImage { get; set; }
    public MaskConfiguration? ActiveMaskConfig { get; set; }

    public void Update(FaceLandmarkResult? result)
    {
        _result = result;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var result = _result;
        if (result is null || result.Faces.Count == 0
            || result.ImageWidth == 0 || result.ImageHeight == 0)
            return;

        // Calculate AspectFit rect (same logic as Image control with Aspect.AspectFit)
        float viewW = dirtyRect.Width;
        float viewH = dirtyRect.Height;
        float imgW = result.ImageWidth;
        float imgH = result.ImageHeight;

        float scale = Math.Min(viewW / imgW, viewH / imgH);
        float fitW = imgW * scale;
        float fitH = imgH * scale;
        float offsetX = (viewW - fitW) / 2f;
        float offsetY = (viewH - fitH) / 2f;

        canvas.FillColor = Colors.LimeGreen;
        canvas.StrokeColor = Colors.LimeGreen;
        canvas.StrokeSize = Math.Max(2f, Math.Min(fitW, fitH) / 100f);
        float dotRadius = Math.Max(1.5f, Math.Min(fitW, fitH) / 250f);

        foreach (var face in result.Faces)
        {
            if (DrawMode == DetectionType.Rectangle)
            {
                if (face.Landmarks.Count == 0) continue;

                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;

                foreach (var pt in face.Landmarks)
                {
                    if (pt.X < minX) minX = pt.X;
                    if (pt.Y < minY) minY = pt.Y;
                    if (pt.X > maxX) maxX = pt.X;
                    if (pt.Y > maxY) maxY = pt.Y;
                }

                float x = offsetX + minX * fitW;
                float y = offsetY + minY * fitH;
                float w = (maxX - minX) * fitW;
                float h = (maxY - minY) * fitH;

                canvas.DrawRectangle(x, y, w, h);
            }
            else if (DrawMode == DetectionType.Mask && MaskImage != null)
            {
                if (face.Landmarks.Count < 455) continue;

                var maskPos = ActiveMaskConfig?.Position ?? MaskPosition.Inside;

                // Pick the correct mathematical anchor based on position
                var anchorPt = maskPos switch
                {
                    MaskPosition.Top => face.Landmarks[10],    // High forehead
                    MaskPosition.Bottom => face.Landmarks[152], // Bottom of chin
                    _ => face.Landmarks[1]                      // Tip of nose (Inside)
                };

                var leftCheek = face.Landmarks[234];
                var rightCheek = face.Landmarks[454];

                float xAnchor = offsetX + anchorPt.X * fitW;
                float yAnchor = offsetY + anchorPt.Y * fitH;

                float xLeft = offsetX + leftCheek.X * fitW;
                float yLeft = offsetY + leftCheek.Y * fitH;

                float xRight = offsetX + rightCheek.X * fitW;
                float yRight = offsetY + rightCheek.Y * fitH;

                float dx = xRight - xLeft;
                float dy = yRight - yLeft;
                float faceWidth = (float)Math.Sqrt(dx * dx + dy * dy);

                float activeWidthMult = ActiveMaskConfig?.WidthMultiplier ?? 1.3f;
                float activeYOffset = ActiveMaskConfig?.YOffsetRatio ?? 0.0f; 

                float maskWidth = faceWidth * activeWidthMult;
                float maskHeight = maskWidth * (MaskImage.Height / (float)MaskImage.Width);

                float angle = (float)(Math.Atan2(yRight - yLeft, xRight - xLeft) * (180.0 / Math.PI));

                canvas.SaveState();
                canvas.Translate(xAnchor, yAnchor);
                canvas.Rotate(angle);

                // Calculate where the top edge of the image should sit relative to the anchor
                float targetDrawY = maskPos switch
                {
                    // Hat sits ON TOP of the forehead. 
                    // So we subtract the entire maskHeight so its bottom edge touches the anchor.
                    // YOffset moves it downward over the forehead.
                    MaskPosition.Top => -maskHeight + (maskHeight * activeYOffset),
                    
                    // Beard hangs BELOW the chin.
                    // So we start drawing right AT the anchor.
                    // YOffset moves it further downward.
                    MaskPosition.Bottom => (maskHeight * activeYOffset),
                    
                    // Mask sits OVER the face.
                    // Centered on the nose by default (-maskHeight / 2). 
                    // YOffset moves it up/down.
                    _ => (-maskHeight / 2f) + (maskHeight * activeYOffset)
                };

                canvas.DrawImage(MaskImage, -maskWidth / 2f, targetDrawY, maskWidth, maskHeight);
                canvas.RestoreState();
            }
            else
            {
                foreach (var pt in face.Landmarks)
                {
                    float x = offsetX + pt.X * fitW;
                    float y = offsetY + pt.Y * fitH;
                    canvas.FillCircle(x, y, dotRadius);
                }
            }
        }
    }
}
