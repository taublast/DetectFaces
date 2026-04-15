# Implementation Notes (DetectFaces)

This document describes the current state of the application — a .NET MAUI app that performs real-time face landmark detection on a live camera preview across Android, iOS, and Windows, using platform-specific MediaPipe bindings.

## Architecture

```
Live camera (SkiaCamera) → RGBA frame → IFaceLandmarkDetector.EnqueuePreviewDetection() → async callback → FaceLandmarkResult → AppCamera overlay draws landmarks/rectangles/masks on SkiaSharp canvas
```

- **DrawnUI framework** — the entire UI is rendered via DrawnUI (`DrawnUi.Maui.Camera` NuGet). The camera preview, overlays, and FPS counter are all SkiaSharp-based DrawnUI controls, not standard MAUI views.
- **Shared interface** `IFaceLandmarkDetector` with platform-specific implementations.
- **Event-driven detection** — the interface uses `EnqueuePreviewDetection(byte[] rgbaBytes, PreviewDetectionRequest)` with `PreviewDetectionCompleted`/`PreviewDetectionFailed` events, not a `Task<T>` return.
- **Platform files** in `Platforms/{Platform}/` folders (compiled only for their platform).
- **DI registration** in `MauiProgram.cs` via `#if` platform conditionals.
- **Landmark visualization** via `AppCamera` (extends `SkiaCamera`), which internally uses a `LandmarkDrawable` for rendering overlays on the SkiaSharp canvas.
- **No image resizing needed** — MediaPipe handles it internally. The camera preview frames are downscaled by `AppCamera` before being sent to the detector for performance.

## NuGet Packages

| Platform | Package | Version |
|----------|---------|---------|
| All | `DrawnUi.Maui.Camera` | 1.9.7.6 |
| Android | `AppoMobi.Preview.MediaPipeTasksVision.Android` | 0.10.33-preview.14 |
| iOS | `MediaPipeTasksVision.iOS` | 0.10.21 |
| Windows | `Mediapipe.Net` | 0.9.2 |
| Windows | `Mediapipe.Net.Runtime.CPU` | 0.9.1 |

Also: `AndroidStoreUncompressedFileExtensions` is set to `.task` so the model isn't compressed in the APK.

## Model Files

- **Android/iOS**: `Resources/Raw/face_landmarker.task` (modern MediaPipe Tasks bundle)
- **Windows**: `Resources/Raw/face_detection_short_range.tflite`, `face_landmark.tflite`, `face_landmark_front_cpu.pbtxt` (legacy MediaPipe graph + models)
- **Overlay images**: `Resources/Raw/mask_spiderman.png`, `Resources/Raw/hat_cake.png` (shared across all platforms)

See [Includes.md](Includes.md) for the full asset manifest and per-platform build conditions.

## Shared Contracts

### `Services/IFaceLandmarkDetector.cs`
Event-driven interface for live preview detection:
- `EnqueuePreviewDetection(byte[] rgbaBytes, PreviewDetectionRequest request)` — accepts raw RGBA bytes from the camera.
- `PreviewDetectionCompleted` / `PreviewDetectionFailed` events for async results.
- Configurable properties: `MaxFaces`, `MinFaceDetectionConfidence`, `MinFacePresenceConfidence`, `MinTrackingConfidence`.

### `Services/FaceLandmarkResult.cs`
Shared result model:
- `FaceLandmarkResult` containing `Faces` (list of `DetectedFace`), `ImageWidth`, `ImageHeight`.
- Performance metrics: `ConversionMilliseconds`, `InferenceMilliseconds`, `ResultMappingMilliseconds`, `UsedGpuDelegate`.
- `DetectionType` enum: `Disabled`, `Landmark`, `Rectangle`, `Mask`.
- `MaskConfiguration` class for configuring overlay images (filename, position, width multiplier, Y offset).
- Each `DetectedFace` contains `Landmarks` as normalized `(X, Y)` points.

### `Services/DetectionSettings.cs`
Global static settings:
- `TryUseGpu` — when true, Android/iOS attempt GPU delegate (with CPU fallback). Windows is always CPU-only.
- `InitialDetectionType` — the overlay mode shown at startup.

## UI

### `MainPage.xaml` / `MainPage.xaml.cs`
- Uses DrawnUI's `AppCanvas` (SkiaSharp-based canvas) as the rendering surface.
- Contains an `AppCamera` control (extends `SkiaCamera`) for live camera preview with face detection overlay.
- Top bar contains a Debug button (toggles Android fast/slow JNI API) and a `Picker` with four modes:
  - `Landmark` — green dots at each landmark point
  - `Rectangle` — bounding box around each face
  - `Mask (Spider-Man)` — Spider-Man mask overlay anchored to face landmarks
  - `Hat (Cake)` — cake hat overlay positioned above the forehead
- `StatusLabel` displays face count and detection benchmark timing.
- `SkiaLabelFps` shows real-time FPS counter.
- Constructor-injects `IFaceLandmarkDetector`; includes a parameterless constructor fallback for Shell/DataTemplate scenarios.

### `AppCamera.cs` (extends `SkiaCamera`)
- Manages the camera-to-detector pipeline: captures preview frames, downscales to a configurable ML dimension, and calls `EnqueuePreviewDetection`.
- Draws the face overlay (landmarks, rectangles, or masks) directly on the SkiaSharp canvas during rendering.
- Implements overlay smoothing (interpolation toward latest landmarks) and a per-landmark deadzone to suppress jitter.
- Exposes events: `PreviewDetectionMeasured`, `PreviewDetectionUpdated`, `PreviewDetectionFailed`.

### `AppCanvas.cs` (extends `Canvas`)
- DrawnUI canvas with XAML hot-reload support. Manages singleton instance lifecycle to prevent leaks during hot reload.

### `Drawables/LandmarkDrawable.cs`
- `IDrawable` that renders face overlays using MAUI Graphics:
  - `Landmark` mode: green dots at each landmark position.
  - `Rectangle` mode: bounding box computed from min/max of all landmarks.
  - `Mask` mode: draws a mask/hat image anchored to specific landmark points (nose tip, forehead, chin), scaled to face width, rotated to match face tilt.
- Uses AspectFit math to map normalized landmark coordinates to view bounds.

## Dependency Injection

`MauiProgram.cs`:
- Registers DrawnUI via `builder.UseDrawnUi(...)` with a portrait desktop window configuration.
- Platform-specific `IFaceLandmarkDetector` registered as singleton via `#if` conditionals:
  - Android: `Platforms.Droid.FaceLandmarkDetector`
  - iOS: `Platforms.iOS.FaceLandmarkDetector`
  - MacCatalyst: `Platforms.MacCatalyst.FaceLandmarkDetector`
  - Windows: `Platforms.Windows.FaceLandmarkDetector`
- `MainPage` registered as transient.

## Platform Implementations

### Android (`Platforms/Android/FaceLandmarkDetector.cs`)
- Uses `AppoMobi.Preview.MediaPipeTasksVision.Android`.
- Loads the `.task` model via `SetModelAssetPath("face_landmarker.task")`.
- **Running mode: `LiveStream`** — uses `DetectAsync(mpImage, timestampMs)` with a `ResultListener` callback and `ErrorListener`.
- Converts incoming RGBA bytes to an Android `Bitmap` (reusing a pooled bitmap), wraps in `MPImage` via `BitmapImageBuilder`.
- GPU delegate attempted first; falls back to CPU. Controlled by `DetectionSettings.TryUseGpu`.
- Lazily constructs the `FaceLandmarker` via `GetLandmarker()` (recreated when settings like `MaxFaces` change).
- Two result-mapping paths: a fast bulk JNI accessor (`FaceLandmarksXY`) and a slow per-landmark wrapper path, toggled via `UseFastApi` static flag (exposed via Debug button in UI).
- Reports `ConversionMilliseconds`, `InferenceMilliseconds`, `ResultMappingMilliseconds`, and `UsedGpuDelegate` in results.

### iOS (`Platforms/iOS/FaceLandmarkDetector.cs`)
- Uses `MediaPipeTasksVision.iOS`.
- Loads the `.task` model from the app bundle via `NSBundle.MainBundle.PathForResource("face_landmarker", "task")`.
- **Running mode: `LiveStream`** — uses `DetectAsyncImage(mpImage, timestampMs)` with a `MPPFaceLandmarkerLiveStreamDelegate` callback.
- Converts RGBA bytes to `CGImage` → `UIImage` → `MPPImage`.
- GPU delegate attempted first via `MPPDelegate.Gpu`. Controlled by `DetectionSettings.TryUseGpu`.
- Lazily constructs the `MPPFaceLandmarker` via `GetLandmarker()`.

### Mac Catalyst (`Platforms/MacCatalyst/FaceLandmarkDetector.cs`)
- Stub that raises `PlatformNotSupportedException` via the `PreviewDetectionFailed` event.
- Also retains a legacy `DetectAsync(Stream)` method (throws `PlatformNotSupportedException`).

### Windows (`Platforms/Windows/FaceLandmarkDetector.cs`)
- Uses `Mediapipe.Net` (0.9.2) + `Mediapipe.Net.Runtime.CPU` (0.9.1).
- Loads the legacy `.tflite` models and `.pbtxt` graph config from MAUI raw assets.
- **Model loading:** Reads the two `.tflite` files from MAUI's `FileSystem.OpenAppPackageFileAsync`, extracts them to disk under `mediapipe-task/face_landmarker/mediapipe/modules/...` so the native C++ graph can find them at the expected relative paths. Uses a `CurrentDirectoryScope` to temporarily set the working directory.
- **Live preview:** Uses a persistent `LiveGraphSession` that keeps a `CalculatorGraph` running across frames, feeding RGBA→RGB-converted `ImageFrame` packets and polling `face_landmarks` output.
- **One-shot detection:** Also retains a `DetectAsync(Stream)` method that creates a fresh graph per call, decoding via WinRT `BitmapDecoder` (BGRA8→RGB conversion).
- Observes the internal graph node `face_landmarks` (`NormalizedLandmarkList`) rather than the public vector output `multi_face_landmarks`, bypassing the missing custom `Vector` envelope in `Mediapipe.Net`.
- **C/C++ interop stability:** Explicit `Dispose()` scopes and typed unmanaged pointers (e.g., `Timestamp(1L)`) prevent the GC from prematurely destroying wrappers while the unmanaged background thread processes frames, resolving Access Violations (`0xc0000005`).
- **Landmark count:** `with_attention=false` in graph side packets means Windows generates exactly **468 landmarks** per face (no iris tracking). Android/iOS generate up to **478 landmarks** including iris.

### iOS Privacy
- `Platforms/iOS/Info.plist` contains both `NSCameraUsageDescription` and `NSPhotoLibraryUsageDescription`.

### Project Configuration (`DetectFaces.csproj`)
- Target frameworks: `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`, `net10.0-windows10.0.19041.0`.
- Uses `WindowsPackageType=None` (unpackaged).
- Conditional `MauiAsset` item groups per platform (see [Includes.md](Includes.md)).
- Conditional NuGet package references per platform.
- `AndroidStoreUncompressedFileExtensions` set to `.task` for Android.

## Verification

- **Android/iOS/Windows:**
  - Launch the app — camera preview should start automatically.
  - Confirm the status label shows detected face count and benchmark timing.
  - Select `Landmark` — confirm green dots overlay the face mesh.
  - Select `Rectangle` — confirm a bounding box overlays each face.
  - Select `Mask (Spider-Man)` — confirm the mask anchors to face tilt and proportions.
  - Select `Hat (Cake)` — confirm the hat sits above the forehead.
  - Test with 0 faces, 1 face, multiple faces.
- **Mac Catalyst:**
  - Launch and confirm a friendly `PlatformNotSupportedException` message for detection.

## Other Tasks/Models Compatible

The architecture (MediaPipe Tasks on mobile, Mediapipe.Net TFLite graphs on Windows) is a generalized pipeline. By swapping the model file and calling a different MediaPipe API class, entirely distinct computer vision tasks can be performed:

* **Hand Landmarking** (`hand_landmarker.task`): Detects 21 3D knuckles and joints per hand. Used for sign language translation, gesture controls, or virtual finger-tracking.
* **Pose Landmarking** (`pose_landmarker.task`): Maps 33 major body joints. Used for fitness apps, motion capture, or fall detection.
* **Object Detection** (`efficientdet.task`): Draws bounding boxes around objects and identifies them from a trained list.
* **Image Segmentation** (`image_segmenter.task`): Performs pixel-perfect foreground/background separation (the technology behind Zoom/Teams background blur).
* **Image Classification** (`classifier.task`): Analyzes the whole image to classify it into categories.

Because the app already handles managing C++ unmanaged memory pointers on Windows and hooking the native iOS/Android MediaPipe binaries, adding any of these tasks would mainly involve parsing different output data structures.

## Face Recognition

Doable as a two-stage pipeline:

* **Stage 1 (Current Engine):** Use the current FaceLandmarkDetector to find the face. The Rectangle bounding box (min/max X and Y from landmarks) crops the face out of the original image.
* **Stage 2 (New Model):** Feed that cropped face image into a TFLite/ONNX Face Recognition model. This outputs a "Face Embedding" (a mathematical vector, usually 128 to 512 floats).
* **Comparison:** Compare the mathematical distance (Cosine Similarity or Euclidean Distance) between a saved vector and the newly generated vector. If the distance is below a threshold, it's a match.
