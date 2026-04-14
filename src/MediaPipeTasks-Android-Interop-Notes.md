# MediaPipeTasks Android interop optimization notes

## Goal

Investigate why Android preview face landmark detection was much slower than expected, reduce the bottleneck, and preserve the reasoning before moving on to packaging and upstreaming.

## What we found

The important timing split became:

- `det`: total detector turnaround from enqueue to completion in the sample app
- `conv`: input conversion / MPImage build cost
- `mp`: MediaPipe inference cost reported from the detector path
- `map`: result materialization / mapping cost from MediaPipe result into app objects
- `other`: leftover detector overhead after subtracting the above

The critical observation was that Android was not mainly slow in inference. It was slow in result mapping.

Representative numbers seen during investigation:

- Before root-cause fix: `size 0.5 det 180 conv 1.1 mp 35 map 130 other 0.2 gpu`
- Temporary reduced-landmark experiment: about `det 60 mp 35 map 30`
- After real binding fix: about `det 45 mp 35 map 0.1`

This showed:

- `mp` was never the dominant problem in the failing path
- `map` dominated because the Android binding exposed nested Java landmark lists and each point access paid heavy Java/.NET interop cost
- the huge win came from removing wrapper churn, not from changing the model

## Root cause

`FaceLandmarkerResult.FaceLandmarks()` in the Android binding returned:

- `IList<IList<NormalizedLandmark>>`

That shape forced the app to pay for:

- Java list wrappers
- nested list enumeration
- per-landmark wrapper creation / marshaling
- per-point JNI calls for `x()` and `y()`

For 468 landmarks per face, this became expensive enough that `map` could exceed `mp` by a wide margin.

## What was changed in MediaPipeTasks

The Android binding fork was extended with a custom partial class for `FaceLandmarkerResult`.

Added file:

- `MediaPipeTasksVision/Additions/FaceLandmarkerResult.cs`

Added API:

- `float[][] GetFaceLandmarksXYInterleaved()`

Behavior of the new API:

- calls the Java `faceLandmarks()` method directly through JNI
- walks the outer face list and inner landmark list with cached JNI method IDs
- reads only `x()` and `y()` from each landmark directly via JNI
- packs each face into a flat interleaved coordinate array: `[x0, y0, x1, y1, ...]`
- returns one `float[]` per face as `float[][]`

Why this API shape was chosen:

- it preserves full landmark detail
- it avoids allocating and marshaling the full nested managed object graph
- it avoids app-side repeated `NormalizedLandmark` wrapper access
- it keeps the fix localized to the Android binding layer rather than pushing JNI details into the sample app

## What changed in the sample app

The Android detector was rewired to consume the new bulk API instead of `FaceLandmarks()`.

Main changes:

- use `GetFaceLandmarksXYInterleaved()` in `Platforms/Android/FaceLandmarkDetector.cs`
- map packed `float[]` coordinates into app `DetectedFace` / `NormalizedPoint` objects
- keep timing instrumentation so we can continue measuring `conv`, `mp`, `map`, and `other`

The earlier temporary "lite preview landmarks" experiment was intentionally removed after the real fix was working, because it was only a diagnostic shortcut and not the desired final solution.

## Packaging note

When publishing local preview NuGets, the package version must be bumped each time. Otherwise NuGet can serve stale cached packages and make it look like the new binding code is not being used.

Relevant local preview flow used here:

- package Android binding with preview version suffixes
- copy package to `C:\Nugets`
- reference the bumped preview version from DetectFaces

## Current conclusions

1. The major Android regression in preview detection was binding/result-materialization overhead, not raw MediaPipe inference.
2. The raw-JNI bulk extraction path fixed the main bottleneck.
3. With the current fix, remaining work is more likely to come from input ingestion and general pipeline overhead than from landmark result mapping.
4. A credible next optimization area is replacing the current `Bitmap`-based MPImage ingest path with a reusable direct `ByteBuffer` path.
5. The current `gpu` indicator means GPU delegate initialization succeeded, but it should still be treated as a practical indicator rather than absolute proof of every internal execution detail.

## Suggested follow-up after this note

- prepare an upstream PR for the Android binding addition
- describe the problem as Android interop overhead caused by `IList<IList<NormalizedLandmark>>`
- present the new API as an Android-specific bulk extraction helper for performance-sensitive consumers
- include benchmark deltas showing `map` dropping from roughly `130 ms` to `0.1 ms` and `det` dropping from roughly `180 ms` to `45 ms` on the observed preview workload