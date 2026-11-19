# PhotoSplat：把照片捏成粒子，扔出去变成 3D 场景（Meta Quest 3）

效果：面前浮着一张照片 → 用手指捏住，照片碎成粒子吸到指尖 → 松手把粒子扔出去 → 粒子飞到房间里，展开成边缘羽化的 3D 场景（透视 MR）。
3D 场景由 Apple 的 SHARP（ml-sharp）从单张照片生成，在你的 Mac 上离线跑。

技术栈和 InnerFrame 一样：Unity 6000.3.23f1、URP、OpenXR、XR Hands。没有用 `com.meta.xr.*`，以后想合进 InnerFrame 也不冲突。

```
tools/sharp_to_unity.py        SHARP 的 .ply → Unity 用的 .bytes（只依赖 numpy；Pillow 可选）
Assets/PhotoSplat/
  Runtime/PhotoSplatEffect.cs  状态机：Idle → 捏住 → 握着 → 扔出 → 展开 → 再捏回手里
  Runtime/PinchInput.cs        XR Hands 捏合检测；在 Editor 里没有头显时用鼠标模拟
  Runtime/PhotoSplatData.cs    读取 .bytes
  Shaders/PhotoSplat.shader    每个 Gaussian splat 同时也是一个粒子，所有动画都在 vertex shader 里完成
```

---

## 1. 在 Mac 上跑 SHARP（照片 → 3D 高斯 .ply）

```bash
git clone https://github.com/apple/ml-sharp.git
cd ml-sharp
conda create -n sharp python=3.13 -y
conda activate sharp
pip install -r requirements.txt
sharp --help                                   # 看一下 predict 的参数

mkdir -p ~/splat/in ~/splat/out
cp 你的照片.jpg ~/splat/in/autumn.jpg
sharp predict -i ~/splat/in -o ~/splat/out     # 首次运行会自动下载模型
```

- Mac 可以跑 prediction（Apple Silicon 用 MPS）。**不要加 `--render`**：官方的视频渲染需要 CUDA，我们也用不到它。
- 选图建议：有明显前景、中景、背景的照片效果最好，比如一棵树、一把椅子加远处的草地。SHARP 生成的场景只在原拍摄角度附近好看，绕到背后会露馅。

## 2. 转成 Unity 能读的格式

```bash
pip install numpy pillow       # sharp 的环境里一般已经有了
python tools/sharp_to_unity.py ~/splat/out/autumn.ply \
    -o <你的Unity项目>/Assets/PhotoSplat/Data/autumn.bytes \
    --image ~/splat/in/autumn.jpg \
    --max-splats 150000
```

脚本会输出 `autumn.bytes`（约 7 MB）和 `autumn.jpg`。常用参数：

| 参数 | 用途 |
|---|---|
| `--max-splats 80000` | Quest 掉帧时减少 splat 数量（建议控制在 20 万以内） |
| `--depth-percentile 90` | 去掉更多远景，比如天空 |
| `--min-opacity 0.1` | 去掉更多半透明的碎点 |
| `--linear-to-srgb` | 颜色发暗或发灰时加上 |

## 3. Unity 项目

1. Unity Hub → New project → 选 **6000.3.23f1** → 模板选 **Mixed Reality**（Unity 自带，已经配好 OpenXR Meta、AR Foundation 透视、XRI 和 XR Hands）。
2. 把本包的 `Assets/PhotoSplat` 文件夹拷进项目的 `Assets/`。
3. 打开模板自带的 `SampleScene`。把模板里的示例 UI 和示例物体关掉，避免它们抢捏合手势。
4. Hierarchy 里新建空物体 `PhotoSplat`，Add Component → **Photo Splat Effect**（会自动加上 Pinch Input）。
   - **Splat Data** ← `autumn.bytes`
   - **Photo** ← `autumn.jpg`
   - 两个 Shader 字段会自动填好；如果是空的，手动选 `PhotoSplat/Splat` 和 `Universal Render Pipeline/Unlit`。
5. Project Settings → XR Plug-in Management → OpenXR → **Android** 页签：确认勾选 **Hand Tracking Subsystem**（Meta Hand Tracking Aim 也一起勾上）。头显里也要在设置中开启手部追踪。

### 先在 Editor 里试（不需要戴头显）
点 Play：
- 照片出现在镜头前方偏下的位置
- **按住鼠标左键**放在照片上 = 捏住，照片碎成粒子，跟着鼠标走
- **松开** = 扔出去，场景展开
- 场景出现后，**按住左键约 0.4 秒** = 把场景收回手里
- **滚轮**调整"手"离镜头的距离，**R** 重置

### 装到 Quest 3
File → Build Profiles → Android → Switch Platform，用 USB 连接 Quest → **Build And Run**。
在头显里：先低头看照片，捏住它，然后松手扔出去。**双手同时捏住 1.5 秒**可以重置。

## 4. 可调参数（Photo Splat Effect 组件上）

| 参数 | 默认值 | 作用 |
|---|---|---|
| Place Distance | 1.6 m | 场景中心离你多远 |
| Scene Scale | 1 | 场景整体缩放。设为 1 时，场景占据的视角和原照片拍摄时相同 |
| Scene Drop | 0.1 m | 把场景的"拍摄视点"放到眼睛下方一点 |
| Vignette Inner / Outer | 0.55 / 1.0 | 场景边缘羽化的范围（视频里那种圆形渐隐） |
| Hold Radius / Dot Sigma | 3.5 cm / 1.5 mm | 指尖粒子团的大小、粒子的大小 |
| Glow | 0.6 | 飞行中粒子的提亮程度 |
| Gather / Expand / Collapse Time | 1.4 / 2.8 / 1.2 s | 三段动画的时长 |
| Flip Ellipse Y | 关 | 如果最终场景看起来像斜着拉丝，而不是清晰的画面，就勾上 |

## 5. 原理（改代码前可以先看这里）

- SHARP 生成的高斯基本上是每个像素一个。转换脚本用相机内参把每个高斯投影回照片，算出它来自哪个像素（uv）。
- 所以**每个 splat 同时就是一个粒子**：
  1. 起点在照片平面上对应的像素位置，所以刚碎开时看起来仍是那张照片；
  2. 然后沿贝塞尔曲线飞到指尖，绕着指尖旋转；
  3. 扔出后飞到它在 3D 场景里的位置，大小也从小圆点渐变为真实的高斯椭圆。

  整个过程没有切换，是连续的。
- 场景摆放：把 SHARP 的相机原点放在你眼睛的位置，朝向为你扔出的方向，并按比例缩放，使场景中位深度正好落在 Place Distance。这样你看到的永远是原照片的拍摄角度，这也是 SHARP 质量最好的角度。
- 渲染：每个 splat 是一个四顶点的四边形，按 3DGS 标准做法把 3D 协方差投影成屏幕上的椭圆。透明度用预乘 alpha，可以正确叠加在透视画面上。从后往前的排序在后台线程做，头移动超过 5 cm 才重新排序。
- 没有用 compute shader，只用普通网格加 vertex shader，所以在 Quest 的 Vulkan 上兼容性比较稳。

## 6. 下一步可以加的

- **落叶**：照原视频的做法，用 Tripo 把一片叶子转成模型，在场景展开后用 Unity Particle System 从树冠位置往下飘。
- **照片绑在左手上**，模拟拿手机的感觉：用 XR Hands 的 Palm 关节 pose 驱动照片面板。
- **多张照片**：做成照片列表，换不同的 `.bytes`。
- 想要真实"从手机里抓"：手机端用一个小网页把照片发到 Quest，Mac 端跑 SHARP 后再把结果推回去。这已经超出 demo 的范围了。
