# MediaPipe Face Landmark Detection for TestFaces

## Context
Add face landmark detection (478-point mesh) to the TestFaces MAUI app after picking a photo. Must work on Android, iOS, and Windows. Uses platform-specific MediaPipe NuGet bindings for Android/iOS and a stub for Windows initially.

## Architecture

```
MainPage picks photo → IFaceLandmarkDetector.DetectAsync(stream) → FaceLandmarkResult → LandmarkDrawable draws 478 points on GraphicsView overlay
```

- **Shared interface** `IFaceLandmarkDetector` with platform-specific implementations
- **Platform files** in `Platforms/{Platform}/` folders (compiled only for their platform)
- **DI registration** in `MauiProgram.cs`
- **Landmark visualization** via MAUI's built-in `GraphicsView` + `IDrawable` (no SkiaSharp needed)
- **No image resizing needed** — MediaPipe handles it internally

## NuGet Packages

| Platform | Package | Version | Condition |
|----------|---------|---------|-----------|
| Android | `MediaPipeTasksVision.Android` | 0.10.32 | `$(TargetFramework.Contains('-android'))` |
| iOS | `MediaPipeTasksVision.iOS` | 0.10.21 | `$(TargetFramework.Contains('-ios'))` |
| MacCatalyst | `MediaPipeTasksVision.iOS` | 0.10.21 | `$(TargetFramework.Contains('-maccatalyst'))` |
| Windows | _(stub — no package yet)_ | — | — |

Also needed for Android: `AndroidStoreUncompressedFileExtensions` set to `.task` so the model isn't compressed in the APK.

## Model File

- **Download**: `https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/latest/face_landmarker.task`
- **Place at**: `Resources/Raw/face_landmarker.task`
- Picked up automatically by existing `MauiAsset` wildcard in .csproj
- **Android access**: `SetModelAssetPath("face_landmarker.task")` (reads from assets/)
- **iOS access**: `NSBundle.MainBundle.PathForResource("face_landmarker", "task")`

## Files to Create/Modify

### New Files
1. **`Services/IFaceLandmarkDetector.cs`** — Interface: `Task<FaceLandmarkResult> DetectAsync(Stream imageStream)`
2. **`Services/FaceLandmarkResult.cs`** — Shared result model: `FaceLandmarkResult` (list of `DetectedFace`, each with 478 `NormalizedPoint`s), plus image dimensions
3. **`Platforms/Android/FaceLandmarkDetector.cs`** — Android impl using `MediaPipeTasksVision.Android`:
   - Constructor initializes `FaceLandmarker` from model asset path
   - `DetectAsync`: decode stream → `BitmapFactory.DecodeStream` → `BitmapImageBuilder` → `MPImage` → `FaceLandmarker.Detect()` → convert result
   - Wrap detection in `Task.Run` (MediaPipe detect is synchronous)
4. **`Platforms/iOS/FaceLandmarkDetector.cs`** — iOS impl using `MediaPipeTasksVision.iOS`:
   - Constructor initializes `MPPFaceLandmarker` with model path from app bundle
   - `DetectAsync`: load stream → `NSData` → `UIImage` → `MPPImage` → `MPPFaceLandmarker.Detect()` → convert result
5. **`Platforms/MacCatalyst/FaceLandmarkDetector.cs`** — Stub (throws `PlatformNotSupportedException`) unless iOS package works on Catalyst
6. **`Platforms/Windows/FaceLandmarkDetector.cs`** — Stub (throws `PlatformNotSupportedException`) with TODO for future `Mediapipe.Net` implementation
7. **`Drawables/LandmarkDrawable.cs`** — `IDrawable` that:
   - Receives `FaceLandmarkResult` via `Update()` method
   - Calculates AspectFit rect matching the Image control's display area
   - Draws green dots at each landmark position, scaled to view bounds

### Modified Files
8. **`TestFaces.csproj`** — Add conditional NuGet refs + `AndroidStoreUncompressedFileExtensions`
9. **`MauiProgram.cs`** — Register `IFaceLandmarkDetector` as singleton + `MainPage` as transient
10. **`MainPage.xaml`** — Replace layout with: Button + ActivityIndicator + StatusLabel + Grid(Image + GraphicsView overlay)
11. **`MainPage.xaml.cs`** — Constructor-inject detector, pick photo → open two streams (one for display, one for detection) → detect → update drawable → invalidate overlay. Includes parameterless constructor fallback for Shell DataTemplate compatibility.
12. **`Platforms/iOS/Info.plist`** — Add `NSPhotoLibraryUsageDescription`

## Implementation Order

1. Download model file → `Resources/Raw/face_landmarker.task`
2. Modify `.csproj` (NuGet refs, Android uncompressed extensions)
3. Create shared models (`IFaceLandmarkDetector`, `FaceLandmarkResult`)
4. Create platform implementations (Android, iOS, Windows/MacCatalyst stubs)
5. Create `LandmarkDrawable`
6. Update UI (`MainPage.xaml` + `.xaml.cs`)
7. Wire up DI (`MauiProgram.cs`)
8. Update iOS `Info.plist`

## Key Risks

| Risk | Mitigation |
|------|------------|
| C# binding API names differ from expected (Java/ObjC name mangling) | Build early, use IntelliSense to verify. API names are best-effort based on standard binding conventions. |
| `.task` file compressed in Android APK → MediaPipe can't load | `AndroidStoreUncompressedFileExtensions` property handles this |
| Shell `DataTemplate` bypasses DI constructor injection | Add parameterless constructor fallback resolving from `Application.Current.Handler.MauiContext.Services` |
| iOS package doesn't work on MacCatalyst | MacCatalyst gets a stub; can test later |
| `Mediapipe.Net` on Windows is outdated (v0.9.2, 2023) | Windows is a stub initially; can revisit with ONNX approach later |

## Verification
1. Build for Android → pick a photo with a clear face → verify green dots form a face mesh
2. Build for iOS → same test
3. Build for Windows → verify graceful "not supported" message
4. Test with 0 faces, 1 face, multiple faces
