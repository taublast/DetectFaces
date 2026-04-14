# Included Models and Assets

This document details the machine learning models and configuration files embedded within the application, their locations, and how each target platform utilizes them to perform Face Landmark Detection.

## Folder Structure & Location

All Machine Learning models and graph dependencies are centralized in the MAUI raw assets folder. In your source repository, they are located at:

```text
TestFaces/
└── Resources/
    └── Raw/
        ├── AboutAssets.txt
        ├── face_detection_short_range.tflite
        ├── face_landmark.tflite
        ├── face_landmark_front_cpu.pbtxt
        └── face_landmarker.task
```

### Conditional Build Optimization
To prevent deploying bloated applications, `TestFaces.csproj` strictly maps what assets are compiled per-platform using **conditional item groups**:
- **Android & iOS**: Compiles `face_landmarker.task` exclusively (ignoring the legacy `.tflite` files).
- **Windows**: Compiles `*.tflite` and `*.pbtxt` exclusively (ignoring the `3.5MB` `.task` bundle).
- **All Platforms**: Share `AboutAssets.txt`.

This ensures models built for Windows do not pollute iOS installations, and vice-versa. During build, `.csproj` logic dynamically bundles the allowed files directly into the APK `assets` folder for Android, the `.app` bundle for iOS, and the application execution folder for Windows.

---

## File Manifest

| File Name | Description | Used By |
| --- | --- | --- |
| `face_landmarker.task` | The modern Google MediaPipe Tasks bundle. It is essentially a zip archive containing updated TFLite models and metadata (including face blendshapes and attention mappings). | Android, iOS |
| `face_detection_short_range.tflite` | Legacy unbundled TFLite model specifically for short-range face bounding box detection. | Windows |
| `face_landmark.tflite` | Legacy unbundled TFLite model capable of generating a 468-point face mesh without attention/iris tracking. | Windows |
| `face_landmark_front_cpu.pbtxt` | The legacy MediaPipe execution graph definition detailing the calculators, nodes, and pathways required to process frames on the CPU. | Windows |
| `AboutAssets.txt` | Standard text file noting the licensing and origins of the model components. | N/A |

---

## Platform Utilization Breakdown

### 🤖 Android
* **Files Used:** `face_landmarker.task`
* **Implementation:** Android utilizes the official `MediaPipeTasksVision.Android` library, which is designed to natively ingest the modern `.task` bundles. 
* **Packaging Note:** Inside the`.csproj`, this file is specified under `<AndroidStoreUncompressedFileExtensions>` ensuring the compressed `.task` asset isn't double-compressed by the APK process, allowing the unmanaged C++ runtime to memory-map the model directly.

### 🍏 iOS
* **Files Used:** `face_landmarker.task`
* **Implementation:** iOS utilizes the official `MediaPipeTasksVision.iOS` library. It extracts the path to the `.task` model using the Native `NSBundle` resource locator and hands it seamlessly to the modern Tasks API to generate landmarks (up to 478 points).

### 🪟 Windows
* **Files Used:** `face_landmark_front_cpu.pbtxt`, `face_detection_short_range.tflite`, `face_landmark.tflite`
* **Implementation:** Windows uses the C# community wrapper `Mediapipe.Net` which targets an older legacy layer of MediaPipe (v0.9.2). Because the modern `.task` model contains tensor updates that cause the older legacy graph architecture to fail silently, Windows completely ignores the `.task` bundle.
* **Execution Flow:** 
  1. Reads the raw `face_landmark_front_cpu.pbtxt` file to construct the internal calculator network.
  2. Extracts the two genuine legacy `.tflite` files out of the MAUI filesystem into memory.
  3. Uses a custom `ResourceManager` delegate to feed these explicitly required models into the native C++ MediaPipe graph when it initiates.

### 💻 Mac Catalyst
* **Files Used:** None
* **Implementation:** Currently exists as a stub throwing `PlatformNotSupportedException`. Needs a specific Mac native implementation to be compiled in the future.
