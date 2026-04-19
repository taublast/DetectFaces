# DetectFaces

This is a cross-platform **.NET MAUI** sample app that runs live face landmark detection using **MediaPipe Tasks** over a `SkiaCamera` preview. It draws overlays directly on the same Skia canvas, and allows capturing photos with detection overlay applied. Available overlay types are Landmark, Rectangle, Spiderman Mask, Funny Hat. App uses **DrawnUi** for rendering camera and overlays.

## Platform Status

| Platform | Backend | Status |
| --- | --- | --- |
| Android | `AppoMobi.Preview.MediaPipeTasksVision.Android` | Implemented |
| iOS | `MediaPipeTasksVision.iOS` | Implemented |
| Windows | `Mediapipe.Net` + `Mediapipe.Net.Runtime.CPU` | Implemented |

## How It Works

1. `AppCamera` downsizes camera live preview frames and extracts RGBA pixels with `frame.TryGetRgba(...)` inside `OnRawFrameAvailable(...)`.
2. The prepared frame enters a coalescing detector queue that keeps at most one active request and one newest pending request.
3. Completed results are published as normalized face landmarks.
4. The overlay renderer projects those landmarks into preview space and draws dots, rectangles, or bitmap masks on the Skia canvas.
5. Photo capture reuses the same render hook through `RenderCapturedPhotoAsync(captured, CameraControl.ProcessFrame, useGpu: true)`, so saved stills match the live overlay.

## Repo Structure

- `src/AppCamera.Detection.cs` - camera-to-detector pipeline, reusable buffers, and latest-frame-wins queue.
- `src/AppCamera.Overlay.cs` - overlay rendering, prediction, and One Euro smoothing.
- `src/MainPage.xaml` / `src/MainPage.xaml.cs` - sample UI, mode switching, and photo capture flow.
- `src/Implementation.md` - architecture notes.
- `src/Includes.md` - embedded model and asset manifest.

## Requirements

- .NET SDK `10.0.104` as pinned in `src/global.json`.
- .NET MAUI workloads for the platforms you want to build.

## Getting Started

1. Clone this repository.
2. Install MAUI workloads if needed:

```powershell
dotnet workload install maui
```

3. Build the target you care about:

```powershell
dotnet build .\src\DetectFaces.csproj -f net10.0-windows10.0.19041.0
dotnet build .\src\DetectFaces.csproj -f net10.0-android
```

For iOS, build on macOS with Xcode and the MAUI iOS workload installed.

## Model Packaging Notes

- Android and iOS use `Resources/Raw/face_landmarker.task`.
- Windows uses the legacy `*.tflite` + `*.pbtxt` graph files because `Mediapipe.Net` does not consume the modern `.task` bundle directly.
- Overlay images such as `mask_spiderman.png` and `hat_cake.png` are shared by all supported platforms.

## More Info

- [src/Implementation.md](src/Implementation.md) documents the current architecture in more detail.
- [src/Includes.md](src/Includes.md) lists the embedded models and explains why the asset set differs between mobile and Windows.
- [SkiaCamera](https://github.com/taublast/DrawnUi.Maui.Camera) repo with docs.


