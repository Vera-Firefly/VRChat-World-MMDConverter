# VMD Converter

[English](README.md)

VMD Converter 是面向 VRChat Worlds 与 MikuMikuDanceStation 的 Unity Editor 转换工具。它可以将 MMD 人物动作转换为 Unity Humanoid `AnimationClip`，并将 VMD 镜头转换为符合 MikuMikuDanceStation 层级的镜头动画。

## 环境要求

- Unity 2022.3
- 不依赖 VRChat Avatars SDK
- 不依赖 Gesture Manager

## 功能

- VMD 人物动作批量转换
- 标准 Humanoid 与目标校准 Humanoid 输出
- 目标校准模式支持选择项目中的 Humanoid FBX
- 自动保留全部 VMD Morph 曲线
- PMX 骨骼、继承、IK 与 Humanoid 烘焙
- MikuMikuDanceStation 镜头动画输出
- PMX 解析缓存与转换设置

## 使用

打开 `VeraFirefly > VMD Converter`。

### 人物动作

1. 在“VMD动作转换”页加入一个或多个 `.vmd` 文件。
2. 选择输出类型。
3. 选择“目标校准 Humanoid”时，在下方指定有效的 Humanoid FBX。
4. 设置输出位置并点击“转换”。

转换固定使用直接导出方案，不生成中间转换数据资源。Morph 映射数据结构与转换接口仍保留，但映射编辑器暂不在窗口显示；导出会写入全部源 Morph，同名目标 BlendShape 会自动使用其实际路径。

### 镜头

“VMD镜头转换”输出采用以下层级绑定：

```text
MMD Camera Animator
└─ Distance
   └─ Camera
```

- 根节点：中心点位置与旋转
- `Distance`：镜头距离
- `Distance/Camera`：视野角与透视模式

输出动画可放入 MikuMikuDanceStation 的 `Camera Controller Base` 中使用。

## 说明

- 内置解析器支持 PMX，不支持 PMD。
- 转换精度会受到源动作、PMX 骨架、Humanoid 映射、初始姿势和目标模型配置影响。
- 请确保 MMD 模型、动作、音频等资源的许可证允许目标用途。

本包使用 Apache License 2.0。
