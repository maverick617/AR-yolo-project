# Tag Model AR

一个面向 Android / iOS 的最小 Unity AR Demo：用户在手机中导入自己的 `.glb` 3D 模型，摄像头识别任意 `tagStandard41h12` AprilTag 后，将模型放在 Tag 正面；拿起、平移或旋转 Tag 时，模型会以完整 6DoF 姿态持续跟随。

这个版本没有 `Tag ID → 预置模型` 映射，也不依赖 YOLO。3D 模型来自用户运行时选择的文件，Tag 只负责提供锚点和尺度。

## Demo 流程

```text
用户选择 GLB
      │
      ├── 校验 glTF 2.0 二进制头与 100 MB 上限
      ├── 运行时加载网格和材质
      └── 按 Tag 物理尺寸等比例归一化模型
                              │
摄像头识别第一张 AprilTag ────┤
                              ▼
                  模型绑定该 Tag ID
                              │
                              ▼
                 Tag 的位置和旋转逐帧更新模型
```

- 导入新 GLB 会替换当前模型。
- 第一张被识别的 Tag 会成为稳定锚点；画面里出现其他 Tag 时不会抢走模型。
- 点击 `REBIND TAG / 重新绑定` 可清除当前绑定，再绑定下一张可见 Tag。
- 成功导入的模型会复制到 `Application.persistentDataPath`，App 下次启动自动恢复。
- Tag 暂时离开画面时模型隐藏；同一 Tag ID 再次出现后继续跟随。

## 尺寸规则

手机界面提供 `20 / 30 / 50 / 80 / 100 mm` 五档 Tag 尺寸，默认 `30 mm`。

选择值必须等于打印后 AprilTag 四个检测角点所围正方形的实际边长，不包含纸张外侧留白。尺寸不匹配会让位姿深度和模型大小按同一比例出错。

当前模型尺寸公式为：

```text
模型最长边 = Tag 实际边长 × 3
```

例如选择 `30 mm`，导入模型的最长渲染边会被归一化为 `90 mm`。模型保持原始长宽高比例、水平居中，并把最低点贴到 Tag 平面。模型本地 `+Y` 朝向 Tag 正面法线，Tag 图片的上边决定模型正前方。

## 快速开始

1. 使用 Unity `2022.3.62f3` 打开项目。
2. 在 Unity Hub 安装目标平台模块：Android Build Support 或 iOS Build Support。
3. 如需重新生成干净场景，执行：

   ```text
   Tools > Tag Model AR > Rebuild Demo Scene
   ```

4. 打开并构建 [UserModelAR.unity](Assets/Scenes/UserModelAR.unity)。该场景已经是唯一启用的 Build Scene。
5. 打印一张 `tagStandard41h12` Tag。可从 [AprilRobotics 官方 Tag 图片仓库](https://github.com/AprilRobotics/apriltag-imgs/tree/master/tagStandard41h12)选择任意 ID；不要拉伸、镜像或裁掉图案。
6. 启动 App，在顶部选择实物 Tag 边长，点击 `IMPORT .GLB / 导入模型`，选择自己的 GLB。
7. 把 Tag 正面放在桌面并对准摄像头。模型出现后拿起 Tag，模型会跟随它移动和旋转。

## 模型输入约束

- 支持 glTF 2.0 二进制文件 `.glb`；暂不支持分离的 `.gltf + .bin + 贴图` 文件组。
- 默认文件上限为 `100 MB`。
- 支持静态 `MeshRenderer` 和 `SkinnedMeshRenderer` 网格；当前 Demo 不播放模型动画。
- 为兼容 Android 文档提供器返回的不带扩展名临时文件，程序依据 GLB 文件头、版本和声明长度校验格式。
- 建议在 Blender 等工具中导出嵌入贴图的 GLB，并在手机上使用较低面数和 1K/2K 贴图。

## 代码结构

```text
Assets/TagModelAR/
├── Scripts/
│   ├── ARCameraFrameProvider.cs   # AR 相机 CPU 图像与相机内参
│   ├── AprilTagDetector.cs        # tagStandard41h12 检测和世界位姿
│   ├── AprilTagPoseSource.cs      # 按 ID 缓存、平滑及过期位姿
│   └── UserModelTagController.cs  # 文件选择、GLB 加载、定尺、绑定和 UI
└── Editor/
    └── UserModelDemoSetup.cs      # 重建场景、Shader 配置和自动校验
```

如需接入自己的 UI 或文件来源，可以绕过原生文件选择器：

```csharp
await controller.ImportModelFromPathAsync(localGlbPath, saveAfterSuccess: true);
```

## 验证

Unity 菜单提供：

```text
Tools > Tag Model AR > Validate Demo
```

它会检查最小 AR 场景所需组件，并验证任意长宽高模型能否按目标米制最长边正确缩放。项目也包含批处理入口：

```text
TagModelAREditor.UserModelDemoSetup.RebuildDemoSceneBatch
TagModelAREditor.UserModelDemoSetup.ValidateDemoSceneBatch
TagModelAREditor.UserModelDemoSetup.ValidateRuntimeGlbBatch
```

最后一个入口读取环境变量 `TAG_MODEL_AR_TEST_GLB`，对真实 GLB 的运行时加载与网格实例化执行 smoke test。

## 已知范围

- 当前版本是一张 Tag 对应一个用户模型；不是多模型同时跟踪系统。
- AprilTag 的打印质量、反光、运动模糊、摄像头对焦和实物尺寸填写都会影响稳定性。
- 编辑器可验证结构和 GLB 加载；最终 6DoF 跟随仍需要 Android / iPhone 真机验收。

## 技术栈

- Unity 2022.3 LTS
- AR Foundation / ARCore / ARKit 5.2
- [Unity glTFast](https://github.com/Unity-Technologies/com.unity.cloud.gltfast) 6.14.1
- [keijiro/AprilTag](https://github.com/keijiro/AprilTag) 1.0.3
- [Native File Picker](https://github.com/yasirkula/UnityNativeFilePicker) 1.4.3（固定到提交版本）
- Universal Render Pipeline 14

项目代码采用 MIT License；第三方依赖许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
