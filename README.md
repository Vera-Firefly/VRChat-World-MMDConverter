# VMD Converter

[简体中文](README-ZH-rCN.md)

VMD Converter is a Unity Editor package for VRChat Worlds and MikuMikuDanceStation. It converts MMD character motion to Unity Humanoid `AnimationClip` assets and converts VMD camera tracks to the hierarchy used by MikuMikuDanceStation.

## Requirements

- Unity 2022.3
- No VRChat Avatars SDK dependency
- No Gesture Manager dependency

## Features

- Batch VMD motion conversion
- Standard and target-calibrated Humanoid output
- Humanoid FBX selection for target-calibrated output
- Automatic preservation of all VMD morph curves
- PMX bone inheritance, IK evaluation, Humanoid baking, and parse caching
- MikuMikuDanceStation camera animation output

Open `VeraFirefly > VMD Converter` to use the tool. Motion conversion always uses the direct-export scheme. Conversion scheme and morph mapping APIs remain available internally, while their editors are hidden. Camera clips bind the orbit center and rotation to the root, distance to `Distance`, and lens properties to `Distance/Camera`.

The built-in parser supports PMX reference models, not PMD. Use only source assets whose licenses permit the intended use.

Licensed under the Apache License 2.0.
