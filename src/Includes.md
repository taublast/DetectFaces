# Included Models and Assets

This document details the machine learning models, graph configs, and overlay images embedded within the application, their locations, and how each target platform utilizes them to perform Face Landmark Detection.

## Folder Structure & Location

All models and raw assets are centralized in the MAUI raw assets folder:

```text
DetectFaces/
└── Resources/
    └── Raw/
        ├── AboutAssets.txt
        ├── face_detection_short_range.tflite
        ├── face_landmark.tflite
        ├── face_landmark_front_cpu.pbtxt
        ├── face_landmarker.task
        ├── hat_cake.png
        └── mask_spiderman.png
```

### Conditional Build Optimization
To prevent deploying bloated applications, `DetectFaces.csproj` strictly maps what assets are compiled per-platform using **conditional item groups**:
- **Android & iOS**: Compiles `face_landmarker.task` exclusively (ignoring the legacy `.tflite` and `.pbtxt` files).
- **Windows**: Compiles `*.tflite` and `*.pbtxt` exclusively (ignoring the `.task` bundle).
- **All Platforms**: Share `AboutAssets.txt` and `*.png` overlay images (mask_spiderman.png, hat_cake.png).

This ensures models built for Windows do not pollute iOS installations, and vice-versa. During build, `.csproj` logic dynamically bundles the allowed files directly into the APK `assets` folder for Android, the `.app` bundle for iOS, and the application execution folder for Windows.

---

## File Manifest

| File Name | Description | Used By |
| --- | --- | --- |
| `face_landmarker.task` | The modern Google MediaPipe Tasks bundle. It is essentially a zip archive containing updated TFLite models and metadata (including face blendshapes and attention mappings). | Android, iOS |
| `face_detection_short_range.tflite` | Legacy unbundled TFLite model specifically for short-range face bounding box detection. | Windows |
| `face_landmark.tflite` | Legacy unbundled TFLite model capable of generating a 468-point face mesh without attention/iris tracking. | Windows |
| `face_landmark_front_cpu.pbtxt` | The legacy MediaPipe execution graph definition detailing the calculators, nodes, and pathways required to process frames on the CPU. | Windows |
| `mask_spiderman.png` | Spider-Man face mask overlay image, drawn over detected faces when "Mask (Spider-Man)" mode is selected. | All platforms |
| `hat_cake.png` | Cake hat overlay image, drawn above detected faces when "Hat (Cake)" mode is selected. | All platforms |
| `AboutAssets.txt` | Standard text file noting the licensing and origins of the model components. | N/A |

---

## Platform Utilization Breakdown

### Android
* **Files Used:** `face_landmarker.task`
* **Implementation:** Android utilizes the `AppoMobi.Preview.MediaPipeTasksVision.Android` library (a preview NuGet binding of the official MediaPipe Tasks SDK), which natively ingests the modern `.task` bundles.
* **Running Mode:** `LiveStream` — frames from the camera preview are fed asynchronously, and results arrive via a callback listener (`ResultListener`).
* **GPU Support:** Attempts GPU delegate first; falls back to CPU automatically if GPU initialization fails. Controlled by `DetectionSettings.TryUseGpu`.
* **Packaging Note:** Inside the `.csproj`, this file is specified under `<AndroidStoreUncompressedFileExtensions>` ensuring the compressed `.task` asset isn't double-compressed by the APK process, allowing the unmanaged C++ runtime to memory-map the model directly.

### iOS
* **Files Used:** `face_landmarker.task`
* **Implementation:** iOS utilizes the official `MediaPipeTasksVision.iOS` library. It extracts the path to the `.task` model using the native `NSBundle` resource locator and hands it to the modern Tasks API to generate landmarks (up to 478 points).
* **Running Mode:** `LiveStream` — frames are fed asynchronously via `DetectAsyncImage`, and results arrive via a `MPPFaceLandmarkerLiveStreamDelegate` callback.
* **GPU Support:** Attempts GPU delegate first via `MPPDelegate.Gpu`. Controlled by `DetectionSettings.TryUseGpu`.

### Windows
* **Files Used:** `face_landmark_front_cpu.pbtxt`, `face_detection_short_range.tflite`, `face_landmark.tflite`
* **Implementation:** Windows uses the C# community wrapper `Mediapipe.Net` which targets an older legacy layer of MediaPipe (v0.9.2). Because the modern `.task` model contains tensor updates that cause the older legacy graph architecture to fail silently, Windows completely ignores the `.task` bundle.
* **Running Mode:** Synchronous graph execution per frame, wrapped in a persistent `LiveGraphSession` that keeps the `CalculatorGraph` running across frames for live preview performance.
* **Execution Flow:**
  1. Reads the raw `face_landmark_front_cpu.pbtxt` file to construct the internal calculator network.
  2. Loads the two genuine legacy `.tflite` files from the MAUI filesystem and extracts them to disk under a `mediapipe-task/face_landmarker/` directory so the native C++ graph can load them via file I/O.
  3. Uses a `CurrentDirectoryScope` to set the working directory so the graph finds the models at the expected relative paths.

### Mac Catalyst
* **Files Used:** None
* **Implementation:** Currently exists as a stub throwing `PlatformNotSupportedException`. Needs a specific Mac native implementation to be compiled in the future.
