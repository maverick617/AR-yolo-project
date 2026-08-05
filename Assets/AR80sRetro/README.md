# AR80sRetro 多物体替换模块

本目录实现 YOLO 类别检测、AprilTag 唯一身份/6DoF 跟踪、尺寸估计和虚拟模型替换。项目级介绍、Tag 数量及构建步骤见根目录 [README](../../README.md)。

所有安装角度都以 AprilRobotics 官方 PNG 未经旋转时的图像上边为准。固定版本、SHA-256 校验下载和完整安装表见 [AprilTag 官方正置与打印](../../APRILTAG_ORIENTATION_ZH.md)。

## 运行时数据流

```text
ARCameraFrameProvider
    ├── YoloObjectDetector
    │      └── cup / phone / tv / bottle / chair / couch / plant / table
    └── KeijiroAprilTagFrameDetector
           └── tagStandard41h12 ID 0-12 raw pose

YOLO detection + class-specific unique Tag
                    │
                    ▼
        RetroReplacementManager
          ├── one track per assigned Tag group/object
          ├── concurrent replacement instances
          ├── depth/YOLO metric sizing
          └── valid prefab, or skip when the model is unavailable
```

## 唯一 Tag 分配

| 类别 | ID |
|---|---|
| cup | `0、1、2、3` |
| phone | `4、5` |
| tv | `6` |
| bottle | `7` |
| chair | `8` |
| couch | `9` |
| plant | `10` |
| table | `11` |
| pen | `12`（不依赖 YOLO 的模型放置调试） |

`RetroPrefabLibrary.TryValidateUniqueAprilTagIds` 会拒绝通配 Tag 和跨类别重复 ID。检测关联先按类别规则过滤 Tag，再按 Tag 投影到检测框中心的距离选择最合适的检测，因此多个类别能够在同一推理帧中分别消费自己的 Tag。

## 主要组件

- `YoloObjectDetector`：解析标准 COCO YOLOv8 detection/segmentation 输出，并保留上述目标类别。
- `AprilTagPoseSource`：缓存新鲜 Tag 位姿，按类别规则和 YOLO 框关联。
- `RetroReplacementRule`：保存类别、Prefab、唯一 Tag、Tag→物体变换和是否允许 Tag-only 调试。
- `RetroPrefabLibrary`：查找类别规则并校验所有 Tag ID 不冲突。
- `CupDimensionEstimator`：将 YOLO 框在环境深度或 Tag 距离处换算成米制包围尺寸。
- `RetroReplacementManager`：管理并发轨迹、Tag 交接、短时回退、模型实例和状态。
- `CupModelFitter`：杯子使用杯身鲁棒尺寸拟合；其他有效模型按检测尺寸等比例拟合。

## 模型缺失行为

如果某条规则没有 Prefab、实例化失败，或实例中没有有效 `MeshFilter`/`SkinnedMeshRenderer`，管理器会跳过该物体并记录限频诊断信息。系统不再创建 `PrimitiveType.Cube` 或其他占位替换物。

手机使用 `Models/phone/Model/old_nokia_phone.obj` 和 `Models/phone/prefab/phone.prefab`，Prefab 含背壳、侧面、屏幕三个网格，基准尺寸约为 `6 × 15 × 2 cm`；来源与 CC BY 4.0 署名见 `Models/phone/ATTRIBUTION.md`。手机 Tag 组 `4/5` 会在正反面之间交接同一条轨迹。

TV 使用项目已有的 `Models/tv/Model/tv.fbx` 和 `Models/tv/prefab/tv.prefab`。pen 使用 `Models/pen/Model/old_pen.obj` 和 `Models/pen/prefab/pen.prefab`，笔尖统一为本地 `+Y`，基准尺寸约 `1 × 14 × 1 cm`。bottle、chair、couch、plant、table 暂无有效网格，因此只保留独立 Tag 规则，不会生成替换视觉。

## 手机连续追踪

手机背面使用 ID `4`，正面使用 ID `5`。两张 Tag 均贴在对应表面中心附近，图案上边朝向手机顶部、正面朝外。规则为正面 Tag 配置了 `Y=180°` 的 Tag→手机旋转，因此任意一面可见时都恢复到同一个手机坐标系；追踪器会在两张 Tag 之间切换而不创建第二个手机实例。

## 笔 Tag-only 调试

当前 COCO YOLO 没有 `pen` 类。pen 规则启用 `trackFromAprilTagWithoutYolo`，只要 ID `12` 新鲜可见，就会直接建立笔模型并用同一份 AprilTag 6DoF 位姿逐帧更新。Tag 固定在笔杆中部的平整小卡片上，官方 PNG 上边朝笔尖，默认沿 Tag 局部 `+Z` 向笔杆内部偏移 `6 mm` 到模型中心。

## Cup 特殊路径

Cup 继续使用三侧面 Tag `0/1/2` 加杯底 Tag `3`。四个安装位姿转换到统一的杯心/把手坐标系；侧面视角收集 5 个 YOLO 尺寸样本并锁定杯高与杯身直径。详细几何和真机排错见 [CUP_DEMO_SETUP_ZH.md](CUP_DEMO_SETUP_ZH.md)。

## 场景配置

唯一构建场景是：

```text
Assets/Scenes/SampleScene.unity
```

在 Unity 中执行：

```text
Tools > AR 80s Retro > Configure Build Scene (Multi-Object)
```

配置工具会验证 cup `0–3`、phone `4/5`、TV `6`、pen `12` 以及全资源表的 Tag 唯一性，关闭重复画面旋转补偿，并保留环境深度用于尺寸换算。每个 Tag 的准确粘贴位置见根目录 README 的“每个 Tag 的粘贴位置”表。
