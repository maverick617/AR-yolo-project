# 三 AprilTag 杯子 AR 替换实验

本项目只处理一个类别：`cup`。系统用 YOLO 确认杯子并估计尺寸，用固定在同一个杯子侧面的三个 AprilTag（ID `0`、`1`、`2`）提供 6DoF 位姿，再把三种 Tag 位姿转换到同一个“杯身中心 + 把手方向”坐标系，最终只显示一个复古杯模型。

## 方法

```text
AR Camera
  ├─ YOLO cup detection ──> 2D box / class / size samples
  ├─ AR depth (optional) ─> metric size assistance
  └─ AprilTag 0/1/2 ─────> 6DoF tag pose
                                │
                 known rigid Tag-to-cup transform
                                │
                                v
                    common cup-center pose
                                │
                                v
                      retro cup replacement
```

- YOLO 负责确认画面中的目标是杯子，防止仅看到一个孤立 Tag 就生成模型。
- 程序在普通视角连续收集 5 个有效杯框/深度样本并取中值，锁定杯高和杯身直径；不需要绕杯扫描。
- AprilTag 负责平移、旋转、倾斜和标签之间的接力。任何时刻看到一个已配置 Tag 即可更新完整位姿。
- 三个 Tag 共用一个 track，切换 ID 时不会生成第二个杯模型。
- 所有 Tag 暂时消失时，程序最多约 `0.8 s` 用 YOLO/深度修正位置并保持最后旋转；二维框无法恢复人手在遮挡期间产生的未知旋转。

## 三个 Tag 的实体安装

使用 AprilRobotics `tagStandard41h12` 的 ID `0`、`1`、`2`。每个图案的**黑色有效方形**边长为 `1 cm`，白色外边不计入；场景中的 `tagSizeMeters` 必须为 `0.01`。

俯视杯子，并令实体把手指向 3 点钟方向：

```text
                         12 点
                              ID 1（1 点）

 ID 0（9 点）          杯身中心          把手（3 点）

                              ID 2（5 点）
                          6 点
```

安装要求：

1. ID `0` 位于 9 点，正对把手；ID `1` 位于 1 点；ID `2` 位于 5 点。不要交换 ID `1` 和 `2`。
2. 三个 Tag 的中心都位于杯身高度中点附近，并处于同一水平带。
3. 每个 Tag 正面沿杯身半径朝外，图案上边都朝杯口。
4. 标签必须刚性固定并尽量平整。曲面明显时使用小型平面卡座；卡座厚度需填入 `tagMountStandoffMeters`。
5. 不使用 ID `3`，杯底也不贴 Tag。

当前 Tag→杯子旋转配置为：

| Tag | 安装位置 | `tagToObjectRotationEuler` |
|---|---|---|
| 0 | 9 点 | `(0, -90, 0)` |
| 1 | 1 点 | `(0, 150, 0)` |
| 2 | 5 点 | `(0, 30, 0)` |

Keijiro AprilTag 的本地 `+Z` 指向图案背面/杯心，所以三个 mount 的杯心方向均为 `(0, 0, +1)`。安装位置、ID、图案上边方向和上述旋转是同一套约定；任何一项改变都会产生固定的位置或把手角度误差。

## Unity 配置与真机运行

1. 用 Unity `2022.3.62f3` 打开项目。
2. 打开唯一构建场景：`Assets/Scenes/SampleScene.unity`。
3. 执行 `Tools > AR 80s Retro > Configure Build Scene (3-Tag Cup)`。该命令会验证三个 mount 的几何关系、连接相机/深度/YOLO/AprilTag 组件并保存场景。
4. 确认 Build Settings 只包含 `Assets/Scenes/SampleScene.unity`。
5. 构建并安装到支持 ARKit 的 iPhone。建议先使用竖屏，并在约 `15–30 cm` 距离测试。
6. 首次画面同时包含完整杯子和 ID `0`，静止约 `1–2 s`。进入 `TRACKING CUP` 后，再缓慢转动到 ID `1` 和 ID `2`。

核心依赖：

- AR Foundation / ARKit `5.2.0`
- Unity Sentis `2.1.3`
- `jp.keijiro.apriltag` `1.0.3`
- `Assets/AR80sRetro/Models/YOLO/yolov8n.onnx`

## 屏幕状态

- `WAITING: show cup + AprilTag 0/1/2`：尚未同时获得 cup 检测和任一有效 Tag。
- `CUP FOUND - WAITING FOR APRILTAG 0/1/2`：YOLO 已看到杯子，但三个 Tag 都没有新鲜位姿。
- `AUTO SIZING CUP: n/5 - HOLD STILL`：正在收集 5 个尺寸样本。
- `TRACKING CUP (APRILTAG n)`：已经跟踪，`n` 是当前使用的 ID。
- `APRILTAG HIDDEN - SHORT POSITION FALLBACK`：短时只校正位置并保持最后旋转。
- `CUP LOST - SHOW CUP + APRILTAG 0/1/2`：超过丢失宽限时间，需要重新看到杯子和任一 Tag。

## 建议实验

固定打印尺寸、光照和手机后，可分别测试：

1. **Yaw 接力**：从 ID `0` 开始，绕杯轴转动 `0–360°`，记录 ID `0→1→2` 切换时的模型位置/角度跳变量与丢失帧数。
2. **手部遮挡**：分别遮挡一个、两个 Tag，记录仍有一个 Tag 可见时的稳定性；再完全遮挡三个 Tag，测量 fallback 的持续时间。
3. **距离与倾角**：在 `15/20/25/30 cm` 以及正视、`30°`、`45°`、`60°` 斜视下记录检测率和位姿抖动。
4. **快速运动**：比较静态、慢速转动和快速转动时的丢失率，重点观察 1 cm Tag 的运动模糊。

每次实验先让 ID `0` 与完整杯框共同可见并完成 5 帧定尺寸，避免把初始化失败与跟踪失败混在一起。

## 已知限制

- 三个侧面 Tag 主要覆盖杯子绕竖轴旋转。杯底没有 Tag，因此把杯底完全朝向相机时可能同时看不到三个侧面 Tag；这种情况下不能保证持续 6DoF。
- 1 cm Tag 对焦距、反光、打印清晰度和运动模糊敏感。标签被手完全遮住时没有视觉算法可以从该 Tag 恢复位姿。
- 当前 `yolov8n.onnx` 是检测模型，不是分割模型。它不能像素级擦除真实杯子；完全替换仍需分割和背景补全。
- 当前 cup FBX 是连续网格。系统能匹配杯身中心、高度、直径和把手方向，但无法独立调整现实把手的孔径和伸出长度。

更详细的真机状态与排错见 [CUP_DEMO_SETUP_ZH.md](Assets/AR80sRetro/CUP_DEMO_SETUP_ZH.md)。
