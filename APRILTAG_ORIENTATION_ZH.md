# AprilTag 官方正置与打印

本项目只使用 AprilRobotics 官方 `tagStandard41h12` 图像。项目中的“Tag 上边”严格指官方 PNG 文件未经旋转时的图像上边，不根据肉眼猜测黑白图案方向。

## 获取经过校验的官方文件

官方来源固定为：

- 仓库：`AprilRobotics/apriltag-imgs`
- 目录：`tagStandard41h12`
- 修订：`f3fd9a7add5bfd82a886fc65240fdb8e3c9ac5a1`
- 文件：`tag41_12_00000.png` 至 `tag41_12_00012.png`

下载 ID `0–12`：

```text
ruby Tools/fetch_official_tagstandard41h12.rb /绝对路径/输出目录
```

脚本会逐个核对 SHA-256；下载内容与固定的 AprilRobotics 修订不完全一致就会停止。已经下载后可离线复查：

```text
ruby Tools/fetch_official_tagstandard41h12.rb --verify /绝对路径/输出目录
```

不要使用搜索引擎预览图、截图、重新绘制的 Tag 或其他 Tag family。打印软件中禁止自动旋转、镜像、翻转或转置；只能等比例缩放，最好在裁切前给官方图像上边做物理记号。

## 正置安装方向

| 物体 | Tag | 官方 PNG 上边的实体方向 |
|---|---|---|
| cup 侧面 | `0、1、2` | 朝杯口 |
| cup 底部 | `3` | 指向杯把 |
| phone 背面 | `4` | 朝手机顶部 |
| phone 正面 | `5` | 朝手机顶部 |
| TV / 显示屏 | `6` | 朝显示屏顶部 |
| bottle | `7` | 朝瓶口 |
| chair | `8` | 朝椅背顶部 |
| couch | `9` | 朝靠背顶部 |
| plant | `10` | 朝植物顶部 |
| table | `11` | 朝桌面 |
| pen | `12` | 朝笔尖 |

所有 Tag 的印刷正面都朝物体外部。手机正面 ID `5` 的 `Y=180°` 是代码中的正反面刚体补偿，不允许通过把 PNG 倒置来代替。

## 坐标约定

AprilRobotics 的相机图像坐标从观察者视角为：`x` 向右、`y` 向下、`z` 指向 Tag 内部。Keijiro Unity 包把图像向下轴转换成 Unity 向上轴，因此未经旋转的官方 PNG 上边对应本项目 Tag 局部 `+Y`。

项目已经固定：

- `KeijiroAprilTagFrameDetector.compensateFrameRotation = false`
- `detectorToCameraRotationCorrectionEuler = (0, 0, 0)`

相机纹理已经转成竖屏检测坐标，再加旋转补偿会破坏官方正置约定。

## 尺寸

场景的 `tagSizeMeters = 0.01`。按 AprilRobotics 的定义，这个尺寸是 Tag 位姿估计使用的两检测角之间边长，也就是对应的白/黑边界距离；不包含 PNG 外侧留白。打印后必须实际测量该边长为 `10 mm`，不能只把整个 PNG 文件宽度设置为 `10 mm`。
