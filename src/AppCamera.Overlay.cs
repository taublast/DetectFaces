using DetectFaces.Services;
using DrawnUi.Camera;
using SkiaSharp;
using System.Diagnostics;

namespace CameraTests.UI
{
    public partial class AppCamera : SkiaCamera
    {
        /// <summary>
        /// When enabled, landmark positions are extrapolated forward from the latest detection
        /// using the velocity between the two most recent detections. This compensates for
        /// MediaPipe's asynchronous pipeline latency and makes overlays track moving faces
        /// more closely. Disable this to observe raw, uncompensated positions.
        /// </summary>
        public bool EnablePrediction { get; set; } = true;

        /// <summary>
        /// Enables the One Euro filter for landmark stabilization.
        /// It adaptively smooths landmarks: strong filtering when still, minimal lag when moving.
        /// When enabled, replaces the deadzone + interpolation smoothing path.
        /// </summary>
        public bool EnableOneEuroFilter { get; set; } = true;

        /// <summary>
        /// Minimum cutoff frequency for the One Euro filter, in hertz.
        /// Lower values produce more smoothing while stationary. Typical range: 0.5-3.0.
        /// </summary>
        public float FilterMinCutoff { get; set; } = 2.5f;

        /// <summary>
        /// Derivative cutoff frequency for the One Euro filter, in hertz.
        /// </summary>
        public float FilterDCutoff { get; set; } = 2.0f;

        /// <summary>
        /// Speed coefficient for the One Euro filter.
        /// Higher values reduce lag during fast movement by increasing the adaptive cutoff.
        /// </summary>
        public float FilterBeta { get; set; } = 25.0f;

        #region Detection Overlay

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

            var scaleForMarks = frame.Scale * RenderingScale;
            if (!frame.IsPreview || RenderingScale < 2f)
            {
                //still photo or desktop, for our app case, make marks bigger
                scaleForMarks = Math.Min(frame.Width, frame.Height) / 300f;
            }

            EnsureDetectionPaints(scaleForMarks);

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
            var hasFinitePoint = false;

            foreach (var point in face.Landmarks)
            {
                var projected = ProjectPoint(point, rotation);

                if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y))
                    continue;

                hasFinitePoint = true;
                minX = Math.Min(minX, projected.X);
                minY = Math.Min(minY, projected.Y);
                maxX = Math.Max(maxX, projected.X);
                maxY = Math.Max(maxY, projected.Y);
            }

            if (!hasFinitePoint)
                return;

            minX = Math.Clamp(minX, 0f, 1f);
            minY = Math.Clamp(minY, 0f, 1f);
            maxX = Math.Clamp(maxX, 0f, 1f);
            maxY = Math.Clamp(maxY, 0f, 1f);

            if (maxX <= minX || maxY <= minY)
                return;

            float left = minX * frame.Width;
            float top = minY * frame.Height;
            float right = maxX * frame.Width;
            float bottom = maxY * frame.Height;

            if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom))
                return;

            frame.Canvas.DrawLine(left, top, right, top, _paintDetectionFrameStroke);
            frame.Canvas.DrawLine(right, top, right, bottom, _paintDetectionFrameStroke);
            frame.Canvas.DrawLine(right, bottom, left, bottom, _paintDetectionFrameStroke);
            frame.Canvas.DrawLine(left, bottom, left, top, _paintDetectionFrameStroke);
        }

        /// <summary>
        /// Draws every normalized landmark for a face as a dot overlay on the current frame.
        /// </summary>
        /// <param name="frame">The frame receiving the overlay.</param>
        /// <param name="face">The detected face whose landmarks should be rendered.</param>
        /// <param name="rotation">The detector-space rotation that must be projected into frame space.</param>
        private void DrawFaceLandmarks(DrawableFrame frame, DetectedFace face, int rotation)
        {
            var landmarks = face.Landmarks;
            var pts = new SKPoint[landmarks.Count];
            for (int i = 0; i < landmarks.Count; i++)
            {
                var projected = ProjectPoint(landmarks[i], rotation);
                pts[i] = new SKPoint(projected.X * frame.Width, projected.Y * frame.Height);
            }

            frame.Canvas.DrawPoints(SKPointMode.Points, pts, _paintDetectionDotsStroke);
        }

        /// <summary>
        /// Loads or clears the bitmap used for mask rendering so preview and recording overlays use the
        /// same already-decoded asset.
        /// </summary>
        /// <param name="config">The mask configuration to activate, or <see langword="null"/> to disable masks.</param>
        /// <returns>A task that completes once the mask asset has been loaded or cleared.</returns>
        public async Task SetupMaskAsync(MaskConfiguration? config)
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

            //exec on GPU thread: store bitmap in GPU texture
            SafeAction(() =>
            {
                var kill3 = MaskImage;
                using var gpu = this.CreateSurface(MaskBitmap.Width, MaskBitmap.Height, true);
                gpu.Canvas.Clear(SKColors.Transparent);
                gpu.Canvas.DrawBitmap(MaskBitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
                gpu.Canvas.Flush();
                MaskImage = gpu.Snapshot();
                DisposeObject(kill3);
            });
        }

        /// <summary>
        /// Draws the configured bitmap mask aligned to key facial anchor points for the current face.
        /// </summary>
        /// <param name="frame">The frame receiving the overlay.</param>
        /// <param name="face">The detected face whose landmarks define mask position and rotation.</param>
        /// <param name="rotation">The detector-space rotation that must be projected into frame space.</param>
        private void DrawFaceMask(DrawableFrame frame, DetectedFace face, int rotation)
        {
            if (face.Landmarks.Count < 455 || MaskImage == null || MaskBitmap == null)
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

            var anchor = ProjectPoint(anchorPt, rotation);
            var left = ProjectPoint(leftCheek, rotation);
            var right = ProjectPoint(rightCheek, rotation);  

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
            frame.Canvas.DrawImage(MaskImage,
                new SKRect(-maskWidth / 2f, targetDrawY, maskWidth / 2f, targetDrawY + maskHeight), _maskSampling, _maskPaint);
            frame.Canvas.Restore();
        }

        /// <summary>
        /// Lazily creates and updates the Skia paints used by landmark, rectangle, and mask overlays for
        /// the current frame scale.
        /// </summary>
        /// <param name="scale">The frame scale used to size strokes consistently across outputs.</param>
        private void EnsureDetectionPaints(float scale)
        {
            _paintDetectionFrameStroke ??= new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.LimeGreen,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };

            _paintDetectionDotsStroke ??= new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.LimeGreen,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round
            };

            if (_maskPaint == null)
            {
                if (RenderingScale < 2)
                {
                    _maskPaint = new SKPaint
                    {
                        IsAntialias = true
                    };
                    _maskSampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                }
                else
                {
                    _maskPaint = new SKPaint
                    {
                        IsAntialias = false
                    };
                    _maskSampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
                }
            }

            _paintDetectionFrameStroke.StrokeWidth = Math.Max(2f, 2f * scale);
            _paintDetectionDotsStroke.StrokeWidth = Math.Max(2f, 2f * scale);
        }

        /// <summary>
        /// Projects a normalized detector-space landmark into normalized frame space by applying the
        /// detector rotation and optional horizontal mirroring.
        /// </summary>
        /// <param name="point">The detector-space normalized landmark.</param>
        /// <param name="rotation">The detector rotation to apply.</param>
        /// <param name="mirrorX">Whether to mirror the projected point horizontally.</param>
        /// <returns>The projected normalized point in frame space.</returns>
        private static NormalizedPoint ProjectPoint(NormalizedPoint point, int rotation, bool mirrorX = false)
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

        #endregion

        #region Camera Overlay

        private SKPaint? _paintPreview;
        private SKPaint? _paintRec;
        private SKPaint? _paintDetectionFrameStroke;
        private SKPaint? _paintDetectionDotsStroke;
        private SKPaint? _maskPaint;
        private SKSamplingOptions _maskSampling;
        private SKFont? _fontOverlay;
        private SKBitmap? MaskBitmap;
        private SKImage? MaskImage;
        private MaskConfiguration? ActiveMaskConfig;
        private DetectionSnapshot? _latestDetection;
        private DetectionSnapshot? _previousDetection;
        private RenderedDetectionState? _renderedDetection;
        private DetectionSnapshot? _lastConsumedTarget;
        private OneEuroFilter[]? _filtersX;
        private OneEuroFilter[]? _filtersY;
        private long _lastRenderedDetectionUpdateTicks;
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
                0 => 1920f, // Default 1080p max dimension
                720 => 1280f, // 1280x720 max
                1080 => 1920f, // 1920x1080 max
                1440 => 2560f, // 2560x1440 max
                2160 => 3840f, // 3840x2160 max
                4320 => 7680f, // 7680x4320 max
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

                var frameRect = new SKRect(0, 0, frame.Width, frame.Height);
                ;
                var rectLimits = frameRect;

                if (frame.IsPreview && _rectFramePreview == SKRect.Empty)
                {
                    _rectFramePreview = frameRect;
                    if (!layout.NeedMeasure)
                    {
                        layout.Invalidate();
                    }
                }
                else if (!frame.IsPreview && _rectFrameRecording == SKRect.Empty)
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
                    if (orientation == DeviceOrientation.LandscapeLeft ||
                        orientation == DeviceOrientation.LandscapeRight)
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
                    Superview = this.Superview //to enable animations and use disposal manager
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
            _fontOverlay ??= new SKFont();
            _fontOverlay.Size = 32 * frame.Scale;
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
            else if (this.IsRecording)
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
            else if (OverlayRecording != null && !frame.IsPreview) //RAW frame being recorded
            {
                DrawOverlay(OverlayRecording, false);
            }

            DrawDetectionOverlay(frame);

            var useScale = RenderingScale * frame.Scale;

            //if (frame.IsPreview)
            {
                // draw frame indicator
                if (paint.Color != SKColors.Transparent)
                {
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 2 * useScale;
                    frame.Canvas.DrawRect(10 * useScale, 10 * useScale, frame.Width - 20 * useScale,
                        frame.Height - 20 * useScale, paint);
                }

                if (!string.IsNullOrEmpty(text))
                {
                    _fontOverlay.Size = 48 * useScale;
                    paint.Color = IsPreRecording ? SKColors.White : SKColors.Red;
                    paint.Style = SKPaintStyle.Fill;

                    if (IsRecording || IsPreRecording)
                    {
                        // text at top left
                        frame.Canvas.DrawText(text, 50 * useScale, 100 * useScale, _fontOverlay, paint);
                        frame.Canvas.DrawText(text2, 50 * useScale, 160 * useScale, _fontOverlay, paint);
                    }
                    else
                    {
                        paint.Color = SKColors.White;
                        frame.Canvas.DrawText(text, 50 * useScale, 100 * useScale, _fontOverlay, paint);
                    }
                }
            }
        }

        #endregion

        #region Overlay Smoothing

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
                                    && TryComputePredictionAlpha(_previousDetection, target, nowTicks,
                                        out predictionPrevious, out predictionAlpha);

                // Extrapolate ahead of the latest detection using inter-detection velocity.
                // This compensates for pipeline latency so the overlay tracks the live face
                // rather than where it was when the frame was captured.
                // One Euro Filter path — smooth landmarks for all draw modes (masks, dots, rectangles).
                // Rectangle bbox derives from filtered landmarks so box edges no longer jump on outlier noise.
                if (EnableOneEuroFilter)
                {
                    if (_renderedDetection == null || _filtersX == null ||
                        !CanSmoothRenderedDetection(_renderedDetection, target))
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
                        ApplyPredictedOneEuroFilters(predictionPrevious!, target, predictionAlpha, nowTicks,
                            _renderedDetection);
                    }
                    else
                    {
                        ApplyOneEuroFilters(target, _renderedDetection, nowTicks);
                    }

                    _lastRenderedDetectionUpdateTicks = nowTicks;
                    _lastConsumedTarget = target;
                    return _renderedDetection;
                }

                // Per-landmark deadzone path (zero-lag fallback when OneEuro is disabled).
                // Reset filter state so re-enabling OneEuro starts fresh.
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
                        CopyPredictedDetectionToRenderedState(predictionPrevious!, target, predictionAlpha, nowTicks,
                            _renderedDetection);
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
                        InterpolateRenderedStateTowardsSnapshot(_renderedDetection, target, smoothingFactor,
                            OverlayDeadzone);
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

            var sampleDtMs = Stopwatch.GetElapsedTime(previous.CompletedAtTicks, target.CompletedAtTicks)
                .TotalMilliseconds;
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
        private static bool IsRenderedDetectionWithinDeadzone(RenderedDetectionState current, DetectionSnapshot target,
            float deadzone)
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
        private static void CopyDetectionSnapshotToRenderedState(DetectionSnapshot source,
            RenderedDetectionState destination)
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
        private static void InterpolateRenderedStateTowardsSnapshot(RenderedDetectionState current,
            DetectionSnapshot target, float amount, float deadzone)
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
        private static void CopyWithPerLandmarkDeadzone(DetectionSnapshot source, RenderedDetectionState destination,
            float deadzone)
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

        #endregion

        #region Overlay Layouts

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

        #endregion
    }
}