# AR 80s Retro：三侧面标签 + 一杯底标签 Cup 实现

## 运行时数据流

```text
ARCameraFrameProvider
    ├── YoloObjectDetector ── cup bbox / optional mask
    └── KeijiroAprilTagFrameDetector ── Tag 0/1/2/3 raw pose

Tag ID ── configured 3D mount rotation/center offset ── common cup/handle frame
YOLO screen box + environment depth (Tag range only as no-depth fallback)
                         │
                         └── 5 same-view size samples
    └── RetroReplacementManager
        ├── one reusable cup track across Tag handovers
        └── wrapper pose + CupModelFitter + retro cup visual
```

YOLO 负责确认 `cup` 和提供测量区域。三个侧面 Tag 与一个杯底 Tag 的位姿通过各自固定的安装变换换算到同一个杯子坐标系；实时跟踪不依赖 AR 平面，因此杯子被拿起后仍可跟随。

## 主要实现

- `ARCameraFrameProvider`：统一相机帧、中心裁剪、缓存、内参 FOV 和检测图像旋转补偿。
- `AprilTagPoseSource`：保存 ID 0/1/2/3 的新鲜原始位姿，按检测框关联 Tag，并允许已有杯子轨迹在 Tag 之间交接。
- `RetroReplacementRule`：为主 Tag 和额外 Tag 保存不同的最终 Tag→杯子旋转；非标准安装还能覆盖 Tag→杯心方向及使用杯高/杯径中的哪一个计算距离。
- `CupDimensionEstimator`：始终以 YOLO 杯框作为尺寸范围；优先用环境深度把像素范围换算成米，无深度时只借用 Tag 的相机距离做透视回退。
- `RetroReplacementManager`：以 5 个同视角 YOLO 测量的中值初始化并锁定本次杯子尺寸，再让虚拟杯的投影高度匹配 YOLO 杯框；Tag 切换时复用同一实例并持续更新旋转/短时位姿。
- `CupModelFitter`：优先拟合杯身直径和高度，并用鲁棒网格统计定位杯身中心，降低把手对整体包围盒的偏置。
- `CupRegistrationSession/ProfileStore`：仅为兼容旧的单 Tag 规则保留；当前四 Tag cup 规则不读取旧 Profile。

## 四标签几何约定

俯视时令现实把手指向 3 点。侧面标签上边朝杯口、正面朝外；杯底标签上边指向把手、正面朝杯外：

| Tag | 实体位置 | 最终 Tag→cup Euler |
|---|---:|---:|
| ID 0 | 9 点，把手正对面；上边朝杯口 | `(0, -90, 0)` |
| ID 1 | 1 点；上边朝杯口 | `(0, +150, 0)` |
| ID 2 | 5 点；上边朝杯口 | `(0, +30, 0)` |
| ID 3 | 杯底，正面朝外；上边指向把手 | `(0, +90, +90)` |

三个侧面位置相隔 120°，中心位于杯身高度中点的同一水平带。四个标签的有效黑色方形边长均为 `0.01 m`。侧面 Tag 到杯心的距离使用实测杯身直径的一半；杯底 ID 3 使用实测杯高的一半。Keijiro 输出中 Tag 局部 `+Y` 指向官方图案上边，局部 `+Z` 从图案正面指入标签背面，所以两种安装的局部杯心方向都是 `(0, 0, +1)`，但尺度轴不同。

核心公式：

```text
worldCupPosition = worldTagPosition
                 + worldTagRotation × configuredCenterOffset[tagId]
worldCupRotation = worldTagRotation × configuredTagToCupRotation[tagId]
```

其中侧面 `configuredCenterOffset` 使用 `measuredBodyDiameter / 2`，杯底 ID 3 使用 `measuredHeight / 2`。四个安装变换都以同一个杯口轴和把手轴为目标，因此实体按约定正贴时，切换 Tag 不改变模型的最终姿态。

当前 Tag 保持新鲜时继续使用它；只有失效后才选最新的另一个已配置 Tag，以减少重叠可见区的交接抖动。

## 无引导扫描的动态尺寸

首次从侧面 ID 0/1/2 的有效检测在同一视角累积 `cupRegistrationSamples = 5` 个尺寸和 YOLO 框样本，逐轴取中值后创建模型。YOLO 间隔为 0.25 秒，因此通常只需静止约 1–2 秒。杯底 ID 3 不能首次解出杯高，只在尺寸锁定后接管翻转跟踪。中值在本次轨迹内锁定，避免杯子旋转时检测框受把手影响而产生呼吸缩放；更换实体杯时清除并重新测量。

尺寸没有写死：高度来自 YOLO 框，水平拟合使用由 YOLO 宽度估计的杯身直径。检测框可能含现实把手，虚拟模型也可能有更大的把手，但 `CupModelFitter` 和最终屏幕匹配都不会为了把虚拟把手塞入检测框而缩小整个杯身。

最终创建时会投影虚拟模型的本地网格边界，并让模型投影高度约占完整 YOLO 杯框高度的 `96%`。宽度不作为统一缩放限制，因为复古把手允许伸出无把手现实杯的检测框。投影匹配可同时放大过小模型或缩小过大模型；只有与 YOLO 高度的误差不超过 `4%` 才锁定，否则后续检测会继续修正。最大修正是比例安全限制而不是固定杯子尺寸。状态栏会显示最终 `H`（高度）与 `D`（杯身直径），便于真机核对。

## 分割模型接口

`YoloObjectDetector` 支持标准 COCO 80 类 YOLOv8-seg 双输出。可将兼容模型导入：

```text
Assets/AR80sRetro/Models/YOLO/yolov8n-seg.onnx
```

缺失时会明确警告并继续使用 detection-only 模型、环境深度或透视回退。

## 构建场景

唯一应维护和构建的场景：

```text
Assets/Scenes/SampleScene.unity
```

执行并保存：

```text
Tools > AR 80s Retro > Configure Build Scene (4-Tag Cup)
```

该工具显式连接尺寸估计器、检测器和 Tag tracker，验证 ID 0/1/2/3，并保证 `tagSizeMeters = 0.01`、`decimation = 1`。由于共享相机纹理已经旋转成竖屏，工具还固定 `compensateFrameRotation = false` 和手动姿态修正 `(0,0,0)`，防止侧面正置 Tag 被重复滚转 90°。测量用 `AROcclusionManager` 位于 `AR80sRetro System`，不会在相机背景阶段用现实杯深度遮掉虚拟模型。

设备操作与状态排错见 [CUP_DEMO_SETUP_ZH.md](CUP_DEMO_SETUP_ZH.md)。
