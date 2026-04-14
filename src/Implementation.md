# Implementation Notes (TestFaces)

This document summarizes what was implemented from [Plan.md](Plan.md), and what differs from that plan.

## Implemented (matches Plan.md)

### Model asset
- `Resources/Raw/face_landmarker.task` is included as a MAUI asset.
- `Resources/Raw/AboutAssets.txt` remains alongside the model asset.

### Shared contracts
- `Services/IFaceLandmarkDetector.cs`: `Task<FaceLandmarkResult> DetectAsync(Stream imageStream)`.
- `Services/FaceLandmarkResult.cs`: `FaceLandmarkResult` containing:
  - `DetectionType` enum (`Landmark`, `Rectangle`, `Mask`)
  - `Faces` (list of `DetectedFace`)
  - `ImageWidth`, `ImageHeight`
  - each `DetectedFace` contains `Landmarks` as normalized `(X, Y)` points.

### UI + overlay rendering
- `MainPage.xaml` contains:
  - Photo pick button
  - A `Picker` (dropdown) to select between `Landmark`, `Rectangle`, and `Mask` display modes
  - ActivityIndicator (spinner)
  - Status label
  - An `Image` with a `GraphicsView` overlay for landmarks
- `MainPage.xaml.cs`:
  - Uses constructor injection for `IFaceLandmarkDetector`.
  - Includes a parameterless constructor fallback for Shell/DataTemplate scenarios.
  - Opens two streams from the picked photo: one for display and one for detection.
  - Handles the `Picker` selection change to dynamically update the `DrawMode` and invalidate the overlay.
- `Drawables/LandmarkDrawable.cs`:
  - Draws dots for landmarks or bounding boxes (min/max bounds of the 468 landmarks), or 2D image overlays (Spider-Man Mask) based on the selected `DetectionType`.
  - Uses the same AspectFit math as the `Image`.

### Dependency injection
- `MauiProgram.cs` registers a platform-specific `IFaceLandmarkDetector` implementation, and registers `MainPage` as transient.

### Platform implementations
- Android: `Platforms/Android/FaceLandmarkDetector.cs`
  - Uses `MediaPipeTasksVision.Android`.
  - Loads the `.task` model via `SetModelAssetPath("face_landmarker.task")`.
  - Decodes the stream into a `Bitmap`, wraps it into an `MPImage`, runs detection, and maps to `FaceLandmarkResult`.
  - Runs detection work on a background thread with `Task.Run`.
- iOS: `Platforms/iOS/FaceLandmarkDetector.cs`
  - Uses `MediaPipeTasksVision.iOS`.
  - Loads the `.task` model from the app bundle via `NSBundle.MainBundle.PathForResource("face_landmarker", "task")`.
  - Decodes the stream into `UIImage`, wraps it in `MPPImage`, runs detection, and maps to `FaceLandmarkResult`.
  - Runs detection work on a background thread with `Task.Run`.
- Mac Catalyst: `Platforms/MacCatalyst/FaceLandmarkDetector.cs`
  - Remains a stub that throws `PlatformNotSupportedException`.

### iOS privacy
- `Platforms/iOS/Info.plist` contains `NSPhotoLibraryUsageDescription`.

### Project configuration
- `TestFaces.csproj`:
  - Uses a MAUI asset wildcard for `Resources/Raw/**`.
  - Adds `AndroidStoreUncompressedFileExtensions` for `.task` files (Android).
  - Adds platform-conditional NuGet package references for Android and iOS.

## Implemented differently than Plan.md

### Windows is not a stub (out-of-box landmark detection)
Plan.md originally called for a Windows stub.

What's implemented instead:
- `Platforms/Windows/FaceLandmarkDetector.cs` implements real landmark detection using `Mediapipe.Net` + CPU runtime.
- `TestFaces.csproj` includes Windows-only package references:
  - `Mediapipe.Net` (0.9.2)
  - `Mediapipe.Net.Runtime.CPU` (0.9.1)
- A CPU MediaPipe graph config is shipped as an app asset:
  - `Resources/Raw/face_landmark_front_cpu.pbtxt`

How Windows detection works:
- Decodes the selected image using WinRT `BitmapDecoder` into BGRA8 bytes, manually converting layout to conform with MediaPipe `Srgb` parameters natively.
- Builds a MediaPipe `ImageFrame` and runs a `CalculatorGraph` using the pbtxt graph.
- **Model Resolution:** At runtime, rather than blindly unzipping models from the modern `face_landmarker.task` file (which proved incompatible with the `0.9.2` wrapper architecture causing pipeline logic silently dropping detection tensors), Windows explicitly maps genuine legacy `.tflite` files placed separately in `Resources/Raw`.
- **C/C++ Interop Stability:** Explicit C# `Dispose()` scopes and typed unmanaged pointers (e.g., `Timestamp(1L)` routing avoiding `IntPtr` address crashes mapping `1`) are implemented to block the Garbage Collector from prematurely destroying the Image and Timestamp context wrappers while the unmanaged background thread asynchronously filters the image, completely resolving Access Violations (`0xc0000005`).
- Observes the internal loop node pipeline `face_landmarks` (`NormalizedLandmarkList`) rather than decoding the public vector output (`multi_face_landmarks`) to bypass the missing custom `Vector` envelope implementation within `Mediapipe.Net` without breaking pipeline execution.

### Windows landmark count can differ from "478-point mesh"
Plan.md describes "478-point mesh".

On Windows, the current implementation sets `with_attention=false` in the graph side packets, natively loading the legacy non-attention models generating exactly **468 landmarks** per face as part of the standalone fallback.

Android/iOS continue utilizing the official MediaPipe Tasks framework directly, and appropriately generate up to 478 points containing enhanced iris tracking.

### Minor implementation shape differences
- Android and iOS implementations lazily construct the landmarker (via `GetLandmarker()`) instead of constructing it directly in the class constructor.
- Plan.md’s Windows verification step (“graceful not supported”) no longer applies; Windows should now detect landmarks.

## Suggested verification (updated)
- Android/iOS/Windows:
  - Pick a clear face photo.
  - Confirm the status label shows detected face count.
  - Confirm green dots overlay the face when `Landmark` is selected.
  - Confirm a bounding box overlays the face when `Rectangle` is selected.
  - Confirm the Spider-Man mask perfectly anchors to the face tilt and proportions when `Mask` is selected.
- Mac Catalyst:
  - Pick a photo and confirm a friendly `PlatformNotSupportedException` message is shown.

##  Other Tasks/Models Compatible

The architecture we gave (MediaPipeTasksVision on mobile, and Mediapipe.Net TFLite graphs on Windows) is a generalized pipeline. By simply swapping the model file in Raw and calling a different MediaPipe API class, we can perform entirely distinct computer vision tasks:

* Hand Landmarking (hand_landmarker.task): Detects 21 3D knuckles and joints per hand. Used for sign language translation, gesture controls (like pinch-to-zoom in VR), or virtual finger-tracking.
Pose Landmarking (pose_landmarker.task): Maps 33 major body joints (shoulders, elbows, knees, ankles). Used for fitness apps (counting squats, checking yoga form), motion capture for gaming, or fall detection.
* Object Detection (efficientdet.task): Draws bounding boxes around objects and identifies them from a trained list (e.g., "Car: 98%", "Dog: 85%", "Cup: 70%").
* Image Segmentation (image_segmenter.task): Performs pixel-perfect separation of the foreground subject from the background. This is the exact technology used to blur your background in Zoom or Teams calls.
* Image Classification (classifier.task): Doesn't find coordinates, but analyzes the whole image to tell you what it is (e.g., sorting photos into "Landscapes", "Food", "Receipts").

Because we already solved managing C++ unmanaged memory pointers on Windows and hooking the native iOS/Android MediaPipe binaries—adding any of these tasks to your app would mainly just involve parsing different output data structures (e.g., a NormalizedLandmarkList for hands instead of faces).

## Face Recognition

Doable as a two-stage pipeline:

* Stage 1 (Current Engine): Use our current FaceLandmarkDetector to find the face. Remember the Rectangle bounding box we just built? You would use those exact Min/Max X and Y coordinates to crop the face out of the original image.

* Stage 2 (New Model): we would feed that cropped, isolated face image into a TFLite/ONNX Face Recognition model. This model outputs a "Face Embedding" (a mathematical vector, usually an array of 128 to 512 floats).

* Comparison: to compare the mathematical distance (Cosine Similarity or Euclidean Distance) between John Doe's saved vector and the newly generated vector. If the distance is below a certain threshold, it's a match.

