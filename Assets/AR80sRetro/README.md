# AR80sRetro：三 AprilTag Cup 实现说明

## 运行链路

```text
ARCameraFrameProvider
    ├── YoloObjectDetector ───────────── cup 检测框/置信度
    ├── ARDepthFrameProvider ─────────── 可选米制深度
    └── KeijiroAprilTagFrameDetector ── Tag 0/1/2 原始 6DoF
                                              │
AprilTagPoseSource <──────────────────────────┘
    │ 选择与 cup 框对应的最新 Tag
    v
RetroReplacementManager
    ├── CupDimensionEstimator ── 5 帧中值尺寸
    ├── RetroReplacementRule ─── Tag→公共杯子坐标系
    └── CupModelFitter ───────── 杯身直径/高度/把手方向
```

当前 `Retro Prefab Library.asset` 只有一个 `cup` rule：主 Tag 为 ID `0`，附加 Tag 为 ID `1/2`。三个 Tag 都安装在杯身侧面，任一 Tag 的位姿都被换算为同一个杯身中心与把手方向。ID 之间切换时复用同一个 `TrackedReplacement`。

## 三个刚性 mount

| ID | 杯身位置（把手为 3 点） | 图案上边 | Tag→Cup 欧拉角 |
|---|---|---|---|
| 0 | 9 点 | 朝杯口 | `(0,-90,0)` |
| 1 | 1 点 | 朝杯口 | `(0,+150,0)` |
| 2 | 5 点 | 朝杯口 | `(0,+30,0)` |

三者中心位于杯身高度中点附近，正面沿半径朝外。有效黑色方形边长为 `0.01 m`。Keijiro Tag 本地 `+Z` 从图案正面指入背面，因此默认 `tagToCupCenterDirection = (0,0,+1)`，中心距离为实测杯身直径的一半加卡座间隙。

模型位姿为：

```text
cupRotation = tagRotation * configuredTagToCupRotation
cupPosition = tagPosition
            + tagRotation * configuredTagToCupCenterOffset
```

首次看到完整 cup 框和任一 Tag 后，尺寸估计器收集 `cupRegistrationSamples = 5` 个有效样本并取中值。尺寸锁定后，Tag 可继续独立提供位姿；YOLO 暂时丢失不会立即销毁模型。

## 主要文件

- `Scripts/KeijiroAprilTagFrameDetector.cs`：从相机 CPU 图像检测 AprilTag 并发布世界位姿。
- `Scripts/AprilTagPoseSource.cs`：缓存新鲜位姿、过滤到规则允许的 ID，并关联 YOLO cup 框。
- `Scripts/RetroReplacementRule.cs`：描述三个 rigid mount 到公共杯子坐标系的变换。
- `Scripts/RetroReplacementManager.cs`：管理初始化、Tag 接力、短时 fallback 与单模型生命周期。
- `Scripts/CupDimensionEstimator.cs`：结合 YOLO 框、深度和相机投影估算米制尺寸。
- `Scripts/CupModelFitter.cs`：把复古杯模型匹配到测得的杯高/杯身直径。
- `Editor/AR80sRetroYoloSetup.cs`：验证 mount 几何并自动连接构建场景。

## 场景配置

在 Unity `2022.3.62f3` 中执行：

```text
Tools > AR 80s Retro > Configure Build Scene (3-Tag Cup)
```

配置工具会：

- 验证 ID `0/1/2` 都恢复到同一个杯子坐标系，并拒绝活动规则中的 ID `3`；
- 设置 `tagSizeMeters = 0.01`、`decimation = 1`；
- 设置 `compensateFrameRotation = false` 和零手动旋转修正；
- 连接 AR 相机、YOLO、深度、AprilTag、尺寸估计和替换管理器；
- 禁用 mock input 与相机渲染遮挡，保留系统对象上的深度测量。

真机步骤与排错见 `CUP_DEMO_SETUP_ZH.md`。
